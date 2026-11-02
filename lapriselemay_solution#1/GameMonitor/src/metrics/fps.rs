//! FPS via ETW, la même source que PresentMon : on compte les événements « Present »
//! émis par DirectX (DXGI pour DX10/11/12, D3D9) pour chaque processus.
//! Nécessite les droits administrateur.

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use ferrisetw::provider::{EventFilter, Provider};
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

/// Suit les images présentées par chaque processus, tant que la session ETW est ouverte.
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

/// Ferme la session ETW par son nom, quel que soit le thread ou le processus qui l'a ouverte.
/// Une session ETW survit au processus qui l'a créée : sans arrêt explicite, elle resterait
/// active (et DirectX continuerait d'y écrire) jusqu'au redémarrage de Windows.
pub fn stop_session() {
    let _ = stop_trace_by_name(SESSION);
}

fn present_provider(guid: &str, present_start: u16, frames: &Shared) -> Provider {
    let frames = frames.clone();
    Provider::by_guid(guid)
        // Seul le début de chaque Present compte : le filtre écarte les autres événements à la source.
        .add_filter(EventFilter::ByEventIds(vec![present_start]))
        .add_callback(move |r: &EventRecord, _: &SchemaLocator| {
            if r.event_id() == present_start {
                record(&frames, r.process_id(), r.raw_timestamp());
            }
        })
        .build()
}

impl FpsTracker {
    pub fn start() -> Self {
        let mut tracker = Self { frames: Shared::default(), trace: None };
        tracker.resume();
        tracker
    }

    /// Ouvre la session ETW, si elle ne l'est pas déjà.
    pub fn resume(&mut self) {
        if self.trace.is_some() {
            return;
        }
        // Une session restée ouverte après un plantage bloquerait la nouvelle.
        stop_session();
        self.trace = UserTrace::new()
            .named(SESSION.to_string())
            .enable(present_provider(DXGI, DXGI_PRESENT_START, &self.frames))
            .enable(present_provider(D3D9, D3D9_PRESENT_START, &self.frames))
            .start_and_process()
            .ok();
    }

    /// Ferme la session ETW : DirectX n'émet plus rien pour nous et plus rien n'est traité.
    pub fn pause(&mut self) {
        if let Some(trace) = self.trace.take() {
            let _ = trace.stop();
        }
        if let Ok(mut map) = self.frames.lock() {
            map.clear();
        }
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
        summarize(&frames.times)
    }
}

impl Drop for FpsTracker {
    fn drop(&mut self) {
        self.pause();
    }
}

/// Moyenne et pire temps d'image sur la seconde qui précède le dernier horodatage.
fn summarize(times: &VecDeque<i64>) -> Option<FpsInfo> {
    let newest = *times.back()?;
    let window: Vec<i64> = times.iter().copied().filter(|&t| t > newest - SECOND).collect();
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

#[cfg(test)]
mod tests {
    use super::*;

    const MS: i64 = 10_000;

    fn times(stamps: impl IntoIterator<Item = i64>) -> VecDeque<i64> {
        stamps.into_iter().collect()
    }

    #[test]
    fn steady_60_fps() {
        // 2 s d'images toutes les 16,667 ms : seule la dernière seconde compte.
        let info = summarize(&times((0..120).map(|i| i * 166_667))).unwrap();
        assert!((info.fps - 60.0).abs() < 0.1, "{}", info.fps);
        assert!((info.frametime_ms - 16.667).abs() < 0.01);
        assert!((info.worst_ms - 16.667).abs() < 0.01);
    }

    #[test]
    fn worst_frame_is_the_longest_interval() {
        let info = summarize(&times([0, 10 * MS, 20 * MS, 70 * MS, 80 * MS])).unwrap();
        assert!((info.worst_ms - 50.0).abs() < 0.001);
        assert!((info.frametime_ms - 20.0).abs() < 0.001);
        assert!((info.fps - 50.0).abs() < 0.01);
    }

    #[test]
    fn needs_two_frames_in_the_last_second() {
        assert!(summarize(&times([])).is_none());
        assert!(summarize(&times([0])).is_none());
        // La première image date de plus d'une seconde avant la dernière : il ne reste qu'une image.
        assert!(summarize(&times([0, 2 * SECOND])).is_none());
    }
}
