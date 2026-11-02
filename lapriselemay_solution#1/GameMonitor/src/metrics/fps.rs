//! FPS via ETW, la même source que PresentMon : on compte les événements « Present »
//! émis par DirectX (DXGI pour DX10/11/12, D3D9) pour chaque processus.
//! Nécessite les droits administrateur.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ferrisetw::provider::Provider;
use ferrisetw::trace::{UserTrace, stop_trace_by_name};
use ferrisetw::{EventRecord, SchemaLocator};

const SESSION: &str = "GameMonitorFPS";
const DXGI: &str = "CA11C036-0102-4A2D-A6AD-F03CFED5D3C9";
const DXGI_PRESENT_START: u16 = 42;
const D3D9: &str = "783ACA0A-790E-4D7F-8451-AA850511C6B9";
const D3D9_PRESENT_START: u16 = 1;

/// Horodatages ETW en unités de 100 ns.
const SECOND: i64 = 10_000_000;

#[derive(Clone, Copy, Default)]
pub struct FpsInfo {
    pub fps: f32,
    pub frametime_ms: f32,
    pub worst_ms: f32,
}

struct Frames {
    times: VecDeque<i64>,
    last_seen: Instant,
}

type Shared = Arc<Mutex<HashMap<u32, Frames>>>;

pub struct FpsTracker {
    frames: Shared,
    trace: Option<UserTrace>,
}

fn record(frames: &Shared, pid: u32, ts: i64) {
    let Ok(mut map) = frames.lock() else { return };
    let entry = map.entry(pid).or_insert_with(|| Frames {
        times: VecDeque::with_capacity(512),
        last_seen: Instant::now(),
    });
    entry.last_seen = Instant::now();
    entry.times.push_back(ts);
    while entry.times.front().is_some_and(|&t| t < ts - 2 * SECOND) {
        entry.times.pop_front();
    }
}

impl FpsTracker {
    pub fn start() -> Self {
        // Une session restée ouverte après un plantage bloquerait la nouvelle.
        let _ = stop_trace_by_name(SESSION);
        let frames: Shared = Arc::new(Mutex::new(HashMap::new()));

        let f = frames.clone();
        let dxgi = Provider::by_guid(DXGI)
            .add_callback(move |r: &EventRecord, _: &SchemaLocator| {
                if r.event_id() == DXGI_PRESENT_START {
                    record(&f, r.process_id(), r.raw_timestamp());
                }
            })
            .build();
        let f = frames.clone();
        let d3d9 = Provider::by_guid(D3D9)
            .add_callback(move |r: &EventRecord, _: &SchemaLocator| {
                if r.event_id() == D3D9_PRESENT_START {
                    record(&f, r.process_id(), r.raw_timestamp());
                }
            })
            .build();

        let trace = UserTrace::new()
            .named(SESSION.to_string())
            .enable(dxgi)
            .enable(d3d9)
            .start_and_process()
            .ok();
        Self { frames, trace }
    }

    pub fn available(&self) -> bool {
        self.trace.is_some()
    }

    /// FPS de la dernière seconde pour le processus donné (normalement le jeu au premier plan).
    pub fn sample(&self, pid: u32) -> Option<FpsInfo> {
        let mut map = self.frames.lock().ok()?;
        // On oublie les processus qui n'affichent plus rien.
        map.retain(|_, f| f.last_seen.elapsed() < Duration::from_secs(5));
        let frames = map.get(&pid)?;
        if frames.last_seen.elapsed() > Duration::from_secs(3) {
            return None;
        }
        let newest = *frames.times.back()?;
        let window: Vec<i64> = frames
            .times
            .iter()
            .copied()
            .filter(|&t| t > newest - SECOND)
            .collect();
        if window.len() < 2 {
            return None;
        }
        let span = (window[window.len() - 1] - window[0]) as f32;
        let intervals = (window.len() - 1) as f32;
        let worst = window.windows(2).map(|w| w[1] - w[0]).max().unwrap_or(0);
        let frametime_ms = span / intervals / 10_000.0;
        Some(FpsInfo {
            fps: if frametime_ms > 0.0 { 1000.0 / frametime_ms } else { 0.0 },
            frametime_ms,
            worst_ms: worst as f32 / 10_000.0,
        })
    }
}

impl Drop for FpsTracker {
    fn drop(&mut self) {
        if let Some(trace) = self.trace.take() {
            let _ = trace.stop();
        }
    }
}
