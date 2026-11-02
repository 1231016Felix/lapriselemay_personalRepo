// Pas de fenêtre de console en release.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod format;
mod metrics;
mod overlay;

use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};

use windows::Win32::UI::HiDpi::{
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext,
};

fn main() {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
    let shared = Arc::new(Mutex::new(metrics::Snapshot::default()));
    let active = Arc::new(AtomicBool::new(true));
    let quit = Arc::new(AtomicBool::new(false));

    metrics::spawn(shared.clone(), active.clone(), quit.clone());
    overlay::run(shared, active, quit);
}
