//! Transforme les mesures en lignes de texte colorées pour l'overlay.

use windows::Win32::Foundation::COLORREF;

use crate::metrics::Snapshot;

const fn rgb(r: u8, g: u8, b: u8) -> COLORREF {
    COLORREF(r as u32 | (g as u32) << 8 | (b as u32) << 16)
}

pub const LABEL: COLORREF = rgb(150, 155, 170);
const VALUE: COLORREF = rgb(240, 240, 240);
const DIM: COLORREF = rgb(120, 125, 140);
const GOOD: COLORREF = rgb(90, 220, 120);
const WARN: COLORREF = rgb(255, 190, 60);
const BAD: COLORREF = rgb(255, 90, 90);
const ACCENT: COLORREF = rgb(80, 200, 255);

pub struct Line {
    pub label: String,
    pub parts: Vec<(String, COLORREF)>,
}

impl Line {
    fn new(label: &str) -> Self {
        Self { label: label.to_string(), parts: Vec::new() }
    }
    fn add(mut self, text: impl Into<String>, color: COLORREF) -> Self {
        if !self.parts.is_empty() {
            self.parts.push(("  ".into(), VALUE));
        }
        self.parts.push((text.into(), color));
        self
    }
}

/// Nombre décimal à la française (virgule).
fn dec(v: f64, digits: usize) -> String {
    format!("{v:.digits$}").replace('.', ",")
}

fn bytes(b: f64) -> String {
    const K: f64 = 1024.0;
    // Seuil à 1000 (et non 1024) pour éviter d'afficher « 1000 Ko ».
    if b >= 1000.0 * K * K {
        format!("{} Go", dec(b / (K * K * K), 1))
    } else if b >= 1000.0 * K {
        format!("{} Mo", dec(b / (K * K), 1))
    } else {
        format!("{} Ko", dec(b / K, 0))
    }
}

fn rate(b: f64) -> String {
    format!("{}/s", bytes(b))
}

fn temp_color(t: f32) -> COLORREF {
    if t < 70.0 {
        GOOD
    } else if t < 85.0 {
        WARN
    } else {
        BAD
    }
}

fn load_color(pct: f64) -> COLORREF {
    if pct < 75.0 {
        VALUE
    } else if pct < 90.0 {
        WARN
    } else {
        BAD
    }
}

pub fn build_lines(s: &Snapshot, detailed: bool, hint: bool) -> Vec<Line> {
    let mut lines = Vec::new();
    if !s.ready {
        lines.push(Line::new("").add("GameMonitor : démarrage…", DIM));
        return lines;
    }

    // FPS du jeu au premier plan
    let mut fps = Line::new("FPS");
    fps = match (s.fps_available, s.fps) {
        (false, _) => fps.add("lancer en admin", WARN),
        (true, None) => fps.add("–", DIM),
        (true, Some(f)) => {
            let color = if f.fps >= 60.0 { GOOD } else if f.fps >= 30.0 { WARN } else { BAD };
            fps.add(format!("{:.0}", f.fps), color)
                .add(format!("{} ms", dec(f.frametime_ms as f64, 1)), VALUE)
                .add(format!("pire {} ms", dec(f.worst_ms as f64, 1)), DIM)
        }
    };
    if !s.foreground.is_empty() && s.fps.is_some() {
        fps = fps.add(s.foreground.clone(), DIM);
    }
    lines.push(fps);

    // CPU
    let mut cpu = Line::new("CPU").add(format!("{:.0} %", s.cpu_usage), load_color(s.cpu_usage as f64));
    if let Some(mhz) = s.cpu_mhz {
        cpu = cpu.add(format!("{} GHz", dec(mhz / 1000.0, 2)), VALUE);
    }
    cpu = match s.cpu_temp {
        Some(t) => cpu.add(format!("{t:.0} °C"), temp_color(t)),
        None => cpu.add("temp. n/d", DIM),
    };
    lines.push(cpu);

    // GPU
    let g = &s.gpu;
    let mut gpu = Line::new("GPU");
    gpu = match g.usage {
        Some(u) => gpu.add(format!("{u:.0} %"), VALUE),
        None => gpu.add("–", DIM),
    };
    if let Some(c) = g.clock_mhz {
        gpu = gpu.add(format!("{c} MHz"), VALUE);
    }
    if let Some(t) = g.temp {
        gpu = gpu.add(format!("{t} °C"), temp_color(t as f32));
    }
    if let Some(p) = g.power_w {
        gpu = gpu.add(format!("{p:.0} W"), VALUE);
    }
    if let Some(f) = g.fan {
        gpu = gpu.add(format!("ventil. {f} %"), VALUE);
    }
    lines.push(gpu);

    if let Some(used) = g.vram_used {
        let text = match g.vram_total {
            Some(total) => format!("{} / {}", bytes(used as f64), bytes(total as f64)),
            None => bytes(used as f64),
        };
        let color = g
            .vram_total
            .map(|t| load_color(used as f64 * 100.0 / t as f64))
            .unwrap_or(VALUE);
        lines.push(Line::new("VRAM").add(text, color));
    }

    // RAM
    let ram_pct = s.ram_used as f64 * 100.0 / s.ram_total.max(1) as f64;
    lines.push(
        Line::new("RAM")
            .add(format!("{} / {}", bytes(s.ram_used as f64), bytes(s.ram_total as f64)), VALUE)
            .add(format!("{ram_pct:.0} %"), load_color(ram_pct)),
    );

    // Disque
    let mut disk = Line::new("Disque");
    if let (Some(r), Some(w)) = (s.disk_read, s.disk_write) {
        disk = disk.add(format!("L {}", rate(r)), VALUE).add(format!("É {}", rate(w)), VALUE);
    }
    if let Some(a) = s.disk_active {
        disk = disk.add(format!("actif {a:.0} %"), load_color(a));
    }
    lines.push(disk);

    // Réseau
    lines.push(
        Line::new("Réseau")
            .add(format!("↓ {}", rate(s.net_down)), VALUE)
            .add(format!("↑ {}", rate(s.net_up)), VALUE),
    );

    if detailed {
        lines.push(Line::new("").add(s.cpu_name.clone(), DIM));
        lines.push(Line::new("").add(g.name.clone(), DIM));
        if let Some(m) = g.mem_clock_mhz {
            lines.push(Line::new("GPU mém.").add(format!("{m} MHz"), VALUE));
        }
        for (i, chunk) in s.cpu_cores.chunks(8).enumerate() {
            let label = if i == 0 { "Cœurs" } else { "" };
            let mut line = Line::new(label);
            for (j, c) in chunk.iter().enumerate() {
                if j > 0 {
                    line.parts.push((" ".into(), VALUE));
                }
                line.parts.push((format!("{c:>3.0}"), load_color(*c as f64)));
            }
            if label.is_empty() {
                // Aligne les lignes suivantes sous la première.
                line.label = " ".into();
            }
            lines.push(line);
        }
        if s.swap_total > 0 {
            lines.push(Line::new("Pagin.").add(
                format!("{} / {}", bytes(s.swap_used as f64), bytes(s.swap_total as f64)),
                VALUE,
            ));
        }
        for d in &s.drives {
            lines.push(Line::new(&d.mount).add(
                format!("{} libres / {}", bytes(d.free as f64), bytes(d.total as f64)),
                VALUE,
            ));
        }
        let up = s.uptime;
        lines.push(Line::new("Allumé").add(format!("{} h {:02}", up / 3600, up / 60 % 60), VALUE));
        lines.push(
            Line::new("Overlay")
                .add(format!("{} % CPU", dec(s.self_cpu as f64, 1)), ACCENT)
                .add(bytes(s.self_mem as f64), ACCENT),
        );
    }

    if hint || detailed {
        lines.push(Line::new("").add("Ctrl+Maj+F9 détails · F10 masquer · F11 coin · F12 quitter", DIM));
    }
    lines
}
