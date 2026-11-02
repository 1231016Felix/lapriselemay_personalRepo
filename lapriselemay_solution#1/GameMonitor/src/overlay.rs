//! Fenêtre overlay en Win32 pur : transparente, toujours au premier plan,
//! les clics la traversent, et elle ne prend jamais le focus du jeu.

use std::cell::RefCell;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::GetDpiForSystem;
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::w;

use crate::format::{Line, build_lines};
use crate::metrics::SharedSnapshot;

const HK_DETAILS: i32 = 1;
const HK_TOGGLE: i32 = 2;
const HK_CORNER: i32 = 3;
const HK_QUIT: i32 = 4;
const TIMER_ID: usize = 1;
const BG: COLORREF = COLORREF(0x0014_1414);
const OPACITY: u8 = 230;

struct State {
    shared: SharedSnapshot,
    active: Arc<AtomicBool>,
    quit: Arc<AtomicBool>,
    font: HFONT,
    scale: f32,
    detailed: bool,
    visible: bool,
    corner: u8,
    hint_until: Instant,
    lines: Vec<Line>,
    label_w: i32,
    line_h: i32,
    size: (i32, i32),
}

thread_local! {
    static STATE: RefCell<Option<State>> = const { RefCell::new(None) };
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().collect()
}

fn text_width(hdc: HDC, s: &str) -> i32 {
    let mut size = SIZE::default();
    unsafe {
        let _ = GetTextExtentPoint32W(hdc, &wide(s), &mut size);
    }
    size.cx
}

pub fn run(shared: SharedSnapshot, active: Arc<AtomicBool>, quit: Arc<AtomicBool>) {
    unsafe {
        let instance: HINSTANCE = GetModuleHandleW(None).unwrap_or_default().into();
        let class = w!("GameMonitorOverlay");
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: class,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            ..Default::default()
        };
        RegisterClassW(&wc);

        let scale = GetDpiForSystem() as f32 / 96.0;
        let font = CreateFontW(
            -((15.0 * scale) as i32),
            0,
            0,
            0,
            FW_SEMIBOLD.0 as i32,
            0,
            0,
            0,
            DEFAULT_CHARSET,
            OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS,
            CLEARTYPE_QUALITY,
            (FF_MODERN.0 | FIXED_PITCH.0) as u32,
            w!("Consolas"),
        );

        STATE.with(|s| {
            *s.borrow_mut() = Some(State {
                shared,
                active,
                quit,
                font,
                scale,
                detailed: false,
                visible: true,
                corner: 0,
                hint_until: Instant::now() + Duration::from_secs(10),
                lines: Vec::new(),
                label_w: 0,
                line_h: (19.0 * scale) as i32,
                size: (10, 10),
            })
        });

        let ex = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        let Ok(hwnd) = CreateWindowExW(
            ex,
            class,
            w!("GameMonitor"),
            WS_POPUP,
            0,
            0,
            10,
            10,
            None,
            None,
            Some(instance),
            None,
        ) else {
            return;
        };
        let _ = SetLayeredWindowAttributes(hwnd, COLORREF(0), OPACITY, LWA_ALPHA);

        let mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
        let _ = RegisterHotKey(Some(hwnd), HK_DETAILS, mods, VK_F9.0 as u32);
        let _ = RegisterHotKey(Some(hwnd), HK_TOGGLE, mods, VK_F10.0 as u32);
        let _ = RegisterHotKey(Some(hwnd), HK_CORNER, mods, VK_F11.0 as u32);
        let _ = RegisterHotKey(Some(hwnd), HK_QUIT, mods, VK_F12.0 as u32);

        refresh(hwnd);
        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(Some(hwnd), TIMER_ID, 1000, None);

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        let _ = DeleteObject(font.into());
    }
}

/// Recalcule le texte, redimensionne la fenêtre et la replace dans son coin.
fn refresh(hwnd: HWND) {
    STATE.with(|cell| {
        let mut guard = cell.borrow_mut();
        let Some(st) = guard.as_mut() else { return };
        if !st.visible {
            return;
        }
        let snap = st.shared.lock().map(|s| s.clone()).unwrap_or_default();
        let hint = Instant::now() < st.hint_until;
        st.lines = build_lines(&snap, st.detailed, hint);

        unsafe {
            let hdc = GetDC(Some(hwnd));
            let old = SelectObject(hdc, st.font.into());
            st.label_w = st
                .lines
                .iter()
                .map(|l| text_width(hdc, &l.label))
                .max()
                .unwrap_or(0);
            let gap = (12.0 * st.scale) as i32;
            let content_w = st
                .lines
                .iter()
                .map(|l| {
                    let values: i32 = l.parts.iter().map(|(t, _)| text_width(hdc, t)).sum();
                    if l.label.is_empty() { values } else { st.label_w + gap + values }
                })
                .max()
                .unwrap_or(0);
            SelectObject(hdc, old);
            ReleaseDC(Some(hwnd), hdc);

            let pad = (8.0 * st.scale) as i32;
            let w = content_w + pad * 2;
            let h = st.lines.len() as i32 * st.line_h + pad * 2;
            st.size = (w, h);

            let margin = (10.0 * st.scale) as i32;
            let sw = GetSystemMetrics(SM_CXSCREEN);
            let sh = GetSystemMetrics(SM_CYSCREEN);
            let (x, y) = match st.corner {
                0 => (margin, margin),
                1 => (sw - w - margin, margin),
                2 => (sw - w - margin, sh - h - margin),
                _ => (margin, sh - h - margin),
            };
            // Réaffirme « toujours au premier plan » à chaque seconde : certains jeux le retirent.
            let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            let _ = InvalidateRect(Some(hwnd), None, false);
        }
    });
}

fn paint(hwnd: HWND) {
    STATE.with(|cell| {
        let guard = cell.borrow();
        let Some(st) = guard.as_ref() else { return };
        unsafe {
            let mut ps = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut ps);
            let (w, h) = st.size;
            // Double tampon pour éviter le scintillement.
            let mem = CreateCompatibleDC(Some(hdc));
            let bmp = CreateCompatibleBitmap(hdc, w, h);
            let old_bmp = SelectObject(mem, bmp.into());
            let old_font = SelectObject(mem, st.font.into());

            let brush = CreateSolidBrush(BG);
            FillRect(mem, &RECT { left: 0, top: 0, right: w, bottom: h }, brush);
            let _ = DeleteObject(brush.into());
            SetBkMode(mem, TRANSPARENT);

            let pad = (8.0 * st.scale) as i32;
            let gap = (12.0 * st.scale) as i32;
            for (i, line) in st.lines.iter().enumerate() {
                let y = pad + i as i32 * st.line_h;
                let mut x = pad;
                if !line.label.is_empty() {
                    SetTextColor(mem, crate::format::LABEL);
                    let _ = TextOutW(mem, x, y, &wide(&line.label));
                    x += st.label_w + gap;
                }
                for (text, color) in &line.parts {
                    SetTextColor(mem, *color);
                    let t = wide(text);
                    let _ = TextOutW(mem, x, y, &t);
                    x += text_width(mem, text);
                }
            }

            let _ = BitBlt(hdc, 0, 0, w, h, Some(mem), 0, 0, SRCCOPY);
            SelectObject(mem, old_font);
            SelectObject(mem, old_bmp);
            let _ = DeleteObject(bmp.into());
            let _ = DeleteDC(mem);
            let _ = EndPaint(hwnd, &ps);
        }
    });
}

fn on_hotkey(hwnd: HWND, id: i32) {
    let mut quit = false;
    STATE.with(|cell| {
        let mut guard = cell.borrow_mut();
        let Some(st) = guard.as_mut() else { return };
        match id {
            HK_DETAILS => st.detailed = !st.detailed,
            HK_TOGGLE => {
                st.visible = !st.visible;
                // Masqué : on met aussi la collecte en pause pour ne rien consommer.
                st.active.store(st.visible, Ordering::Relaxed);
                unsafe {
                    let _ = ShowWindow(hwnd, if st.visible { SW_SHOWNOACTIVATE } else { SW_HIDE });
                }
            }
            HK_CORNER => st.corner = (st.corner + 1) % 4,
            HK_QUIT => quit = true,
            _ => {}
        }
    });
    if quit {
        unsafe {
            let _ = DestroyWindow(hwnd);
        }
    } else {
        refresh(hwnd);
    }
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    match msg {
        WM_TIMER => {
            refresh(hwnd);
            LRESULT(0)
        }
        WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
        WM_HOTKEY => {
            on_hotkey(hwnd, wparam.0 as i32);
            LRESULT(0)
        }
        WM_DESTROY => {
            STATE.with(|cell| {
                if let Some(st) = cell.borrow().as_ref() {
                    st.quit.store(true, Ordering::Relaxed);
                }
            });
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}

