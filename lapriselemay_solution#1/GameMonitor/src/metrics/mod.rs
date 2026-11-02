//! Collecte de toutes les mesures, dans un thread séparé, une fois par seconde.

mod fps;
mod gpu;
mod pdh;
mod temps;

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use sysinfo::{Disks, Networks, Pid, ProcessRefreshKind, ProcessesToUpdate, System};
use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};
use wmi::{Variant, WMIConnection};

pub use fps::FpsInfo;
pub use gpu::GpuInfo;

#[derive(Clone)]
pub struct Drive {
    pub mount: String,
    pub free: u64,
    pub total: u64,
}

#[derive(Clone, Default)]
pub struct Snapshot {
    pub ready: bool,
    pub cpu_name: String,
    pub cpu_usage: f32,
    pub cpu_cores: Vec<f32>,
    pub cpu_mhz: Option<f64>,
    pub cpu_temp: Option<f32>,
    pub cpu_temp_source: &'static str,
    pub ram_used: u64,
    pub ram_total: u64,
    pub swap_used: u64,
    pub swap_total: u64,
    pub gpu: GpuInfo,
    pub disk_read: Option<f64>,
    pub disk_write: Option<f64>,
    pub disk_active: Option<f64>,
    pub drives: Vec<Drive>,
    pub net_down: f64,
    pub net_up: f64,
    pub fps_available: bool,
    pub fps: Option<FpsInfo>,
    pub foreground: String,
    pub self_cpu: f32,
    pub self_mem: u64,
    pub uptime: u64,
}

pub type SharedSnapshot = Arc<Mutex<Snapshot>>;

/// Lance le thread de collecte. `active` à faux met la collecte en pause (overlay masqué).
pub fn spawn(shared: SharedSnapshot, active: Arc<AtomicBool>, quit: Arc<AtomicBool>) {
    std::thread::spawn(move || {
        let mut collector = Collector::new();
        while !quit.load(Ordering::Relaxed) {
            if active.load(Ordering::Relaxed) {
                let snap = collector.collect();
                if let Ok(mut s) = shared.lock() {
                    *s = snap;
                }
            }
            std::thread::sleep(Duration::from_secs(1));
        }
    });
}

struct Collector {
    sys: System,
    networks: Option<Networks>,
    disks: Option<Disks>,
    last_disks: Instant,
    last_tick: Instant,
    pdh: Option<pdh::Pdh>,
    counters: HashMap<&'static str, pdh::Counter>,
    gpu: gpu::Gpu,
    temps: temps::Temps,
    fps: fps::FpsTracker,
    self_pid: Pid,
    cpu_name: String,
}

const PDH_COUNTERS: &[(&str, &str)] = &[
    ("perf", r"\Processor Information(_Total)\% Processor Performance"),
    ("freq", r"\Processor Information(_Total)\Processor Frequency"),
    ("read", r"\PhysicalDisk(_Total)\Disk Read Bytes/sec"),
    ("write", r"\PhysicalDisk(_Total)\Disk Write Bytes/sec"),
    ("idle", r"\PhysicalDisk(_Total)\% Idle Time"),
];

fn gpu_name_from_wmi() -> String {
    WMIConnection::new()
        .ok()
        .and_then(|con| {
            let rows: Vec<HashMap<String, Variant>> =
                con.raw_query("SELECT Name FROM Win32_VideoController").ok()?;
            rows.iter().find_map(|r| match r.get("Name") {
                Some(Variant::String(s)) => Some(s.clone()),
                _ => None,
            })
        })
        .unwrap_or_else(|| "GPU".to_string())
}

fn foreground_pid() -> u32 {
    let mut pid = 0u32;
    unsafe {
        let hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
    }
    pid
}

impl Collector {
    fn new() -> Self {
        let mut sys = System::new().expect("sysinfo");
        sys.refresh_cpu_all();
        let cpu_name = sys
            .cpus()
            .first()
            .map(|c| c.brand().trim().to_string())
            .unwrap_or_default();

        let pdh = pdh::Pdh::new();
        let mut counters = HashMap::new();
        if let Some(p) = &pdh {
            for (key, path) in PDH_COUNTERS {
                if let Some(c) = p.add(path) {
                    counters.insert(*key, c);
                }
            }
        }
        let gpu = gpu::Gpu::new(pdh.as_ref(), gpu_name_from_wmi());
        if let Some(p) = &pdh {
            p.collect(); // première mesure de référence pour les compteurs de débit
        }

        Self {
            sys,
            networks: Networks::new_with_refreshed_list().ok(),
            disks: Disks::new_with_refreshed_list().ok(),
            last_disks: Instant::now(),
            last_tick: Instant::now(),
            pdh,
            counters,
            gpu,
            temps: temps::Temps::new(),
            fps: fps::FpsTracker::start(),
            self_pid: Pid::from_u32(std::process::id()),
            cpu_name,
        }
    }

    fn counter(&self, key: &str) -> Option<f64> {
        self.counters.get(key)?.value()
    }

    fn collect(&mut self) -> Snapshot {
        let elapsed = self.last_tick.elapsed().as_secs_f64().max(0.001);
        self.last_tick = Instant::now();

        self.sys.refresh_cpu_usage();
        self.sys.refresh_memory();
        if let Some(p) = &self.pdh {
            p.collect();
        }

        let mut s = Snapshot {
            ready: true,
            cpu_name: self.cpu_name.clone(),
            cpu_usage: self.sys.global_cpu_usage(),
            cpu_cores: self.sys.cpus().iter().map(|c| c.usage()).collect(),
            ram_used: self.sys.used_memory(),
            ram_total: self.sys.total_memory(),
            swap_used: self.sys.used_swap(),
            swap_total: self.sys.total_swap(),
            uptime: System::uptime().unwrap_or(0),
            ..Default::default()
        };

        // Fréquence réelle = fréquence de base × % de performance (inclut le turbo).
        if let (Some(base), Some(perf)) = (self.counter("freq"), self.counter("perf")) {
            s.cpu_mhz = Some(base * perf / 100.0);
        }
        s.disk_read = self.counter("read");
        s.disk_write = self.counter("write");
        s.disk_active = self.counter("idle").map(|idle| (100.0 - idle).clamp(0.0, 100.0));

        let (temp, source) = self.temps.cpu();
        s.cpu_temp = temp;
        s.cpu_temp_source = source;

        s.gpu = self.gpu.read();

        if let Some(nets) = &mut self.networks {
            nets.refresh(true);
            let (mut down, mut up) = (0u64, 0u64);
            for (name, data) in nets.iter() {
                if name.to_string_lossy().contains("Loopback") {
                    continue;
                }
                down += data.received();
                up += data.transmitted();
            }
            s.net_down = down as f64 / elapsed;
            s.net_up = up as f64 / elapsed;
        }

        if let Some(disks) = &mut self.disks {
            if self.last_disks.elapsed() > Duration::from_secs(10) {
                disks.refresh(true);
                self.last_disks = Instant::now();
            }
            s.drives = disks
                .iter()
                .map(|d| Drive {
                    mount: d.mount_point().to_string_lossy().trim_end_matches('\\').to_string(),
                    free: d.available_space(),
                    total: d.total_space(),
                })
                .collect();
        }

        // FPS du programme au premier plan (le jeu), et consommation de l'overlay lui-même.
        let fg = foreground_pid();
        s.fps_available = self.fps.available();
        s.fps = self.fps.sample(fg);
        let fg_pid = Pid::from_u32(fg);
        self.sys.refresh_processes_specifics(
            ProcessesToUpdate::Some(&[self.self_pid, fg_pid]),
            true,
            ProcessRefreshKind::nothing().with_cpu().with_memory(),
        );
        if let Some(p) = self.sys.process(fg_pid) {
            s.foreground = p.name().to_string_lossy().trim_end_matches(".exe").to_string();
        }
        if let Some(p) = self.sys.process(self.self_pid) {
            s.self_cpu = p.cpu_usage() / self.sys.cpus().len().max(1) as f32;
            s.self_mem = p.memory();
        }
        s
    }
}
