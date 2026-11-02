//! Fenêtre overlay en Win32 pur : transparente, toujours au premier plan,
//! les clics la traversent, et elle ne prend jamais le focus du jeu.

use std::cell::RefCell;
use std::time::{Duration, Instant};

use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::HiDpi::{GetDpiForMonitor, GetDpiForSystem, MDT_EFFECTIVE_DPI};
use windows::Win32::UI::Input::KeyboardAndMouse::*;
use windows::Win32::UI::WindowsAndMessaging::*;
use windows::core::{PCWSTR, w};

use crate::format::{Freshness, Line, build_lines, hotkey_warning};
use crate::metrics::{self, Control, SharedSnapshot};

const CLASS: PCWSTR = w!("GameMonitorOverlay");
const HK_DETAILS: i32 = 1;
const HK_TOGGLE: i32 = 2;
const HK_CORNER: i32 = 3;
const HK_QUIT: i32 = 4;
/// Envoyé par un second lancement de GameMonitor : réafficher l'overlay.
const WM_APP_SHOW: u32 = WM_APP + 1;
const TIMER_ID: usize = 1;
const HINT: Duration = Duration::from_secs(10);
const BG: COLORREF = COLORREF(0x0014_1414);
const OPACITY: u8 = 230;

struct State {
    shared: SharedSnapshot,
    control: Control,
    /// Police et échelle au DPI de l'écran où s'affiche l'overlay (96 = 100 %).
    font: HFONT,
    dpi: u32,
    scale: f32,
    detailed: bool,
    visible: bool,
    corner: u8,
    hint_until: Instant,
    /// Raccourcis que Windows a refusés (déjà pris par une autre application).
    missing_hotkeys: String,
    /// Mesures reçues de seconde en seconde, pour repérer une collecte bloquée.
    freshness: Freshness,
    lines: Vec<Line>,
    label_w: i32,
    line_h: i32,
    size: (i32, i32),
}

impl State {
    /// Adapte la police, la hauteur de ligne et les espacements au DPI de l'écran : le processus est
    /// Per-Monitor V2, chaque écran a donc le sien. La police n'est recréée que si le DPI change.
    fn set_dpi(&mut self, dpi: u32) {
        if dpi == self.dpi {
            return;
        }
        self.dpi = dpi;
        self.scale = dpi as f32 / 96.0;
        self.line_h = (19.0 * self.scale) as i32;
        let old = std::mem::replace(&mut self.font, create_font(self.scale));
        if !old.is_invalid() {
            unsafe {
                let _ = DeleteObject(old.into());
            }
        }
    }
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

/// Consolas demi-gras, 15 px à 100 %.
fn create_font(scale: f32) -> HFONT {
    unsafe {
        CreateFontW(
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
        )
    }
}

/// Demande à l'overlay déjà lancé de se réafficher (et de rappeler les raccourcis).
pub fn show_existing() {
    unsafe {
        if let Ok(hwnd) = FindWindowW(CLASS, PCWSTR::null()) {
            let _ = PostMessageW(Some(hwnd), WM_APP_SHOW, WPARAM(0), LPARAM(0));
        }
    }
}

pub fn run(shared: SharedSnapshot, control: Control) {
    unsafe {
        let instance: HINSTANCE = GetModuleHandleW(None).unwrap_or_default().into();
        let wc = WNDCLASSW {
            lpfnWndProc: Some(wndproc),
            hInstance: instance,
            lpszClassName: CLASS,
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            ..Default::default()
        };
        RegisterClassW(&wc);

        let mut state = State {
            shared,
            control,
            font: HFONT::default(),
            dpi: 0,
            scale: 1.0,
            detailed: false,
            visible: true,
            corner: 0,
            hint_until: Instant::now() + HINT,
            missing_hotkeys: String::new(),
            freshness: Freshness::default(),
            lines: Vec::new(),
            label_w: 0,
            line_h: 0,
            size: (10, 10),
        };
        // DPI de l'écran principal pour commencer ; refresh() passe ensuite à celui de l'écran du jeu.
        state.set_dpi(GetDpiForSystem());
        STATE.with(|s| *s.borrow_mut() = Some(state));

        let ex = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        let Ok(hwnd) = CreateWindowExW(
            ex,
            CLASS,
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
        let hotkeys = [
            (HK_DETAILS, VK_F9, "F9"),
            (HK_TOGGLE, VK_F10, "F10"),
            (HK_CORNER, VK_F11, "F11"),
            (HK_QUIT, VK_F12, "F12"),
        ];
        // Sans raccourci, l'overlay (qui laisse passer les clics) ne se piloterait plus : on le signale.
        let missing: Vec<&str> = hotkeys
            .iter()
            .filter(|(id, key, _)| RegisterHotKey(Some(hwnd), *id, mods, u32::from(key.0)).is_err())
            .map(|(_, _, name)| *name)
            .collect();
        STATE.with(|s| {
            if let Some(st) = s.borrow_mut().as_mut() {
                st.missing_hotkeys = missing.join(" ");
            }
        });

        refresh(hwnd);
        let _ = ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        SetTimer(Some(hwnd), TIMER_ID, 1000, None);

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        // La police a pu être recréée à chaque changement de DPI : on libère celle en cours.
        if let Some(st) = STATE.with(|s| s.borrow_mut().take()) {
            let _ = DeleteObject(st.font.into());
        }
    }
}

/// Recalcule le texte, redimensionne la fenêtre et la replace dans son coin, sur l'écran du jeu
/// et à la taille qui convient au DPI de cet écran.
fn refresh(hwnd: HWND) {
    let placement = STATE.with(|cell| {
        let mut guard = cell.borrow_mut();
        let st = guard.as_mut()?;
        if !st.visible {
            return None;
        }
        let (screen, dpi) = target_screen(hwnd)?;
        st.set_dpi(dpi);

        let snap = st.shared.lock().map(|s| s.clone()).unwrap_or_default();
        let now = Instant::now();
        // Masqué, l'overlay ne passe pas ici : une mesure figée n'est signalée que s'il est visible.
        let frozen = st.freshness.frozen(snap.taken, now);
        st.lines = build_lines(&snap, frozen, st.detailed, now < st.hint_until);
        if !st.missing_hotkeys.is_empty() {
            st.lines.push(hotkey_warning(&st.missing_hotkeys));
        }

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
            let (x, y) = corner_position(screen, st.size, margin, st.corner);
            Some((x, y, w, h))
        }
    });
    // SetWindowPos hors de l'emprunt de STATE : en passant sur un écran d'un autre DPI,
    // Windows envoie WM_DPICHANGED à la fenêtre pendant l'appel.
    let Some((x, y, w, h)) = placement else { return };
    unsafe {
        // Réaffirme « toujours au premier plan » à chaque seconde : certains jeux le retirent.
        let _ = SetWindowPos(hwnd, Some(HWND_TOPMOST), x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        let _ = InvalidateRect(Some(hwnd), None, false);
    }
}

/// Rectangle (coordonnées du bureau virtuel) et DPI de l'écran où afficher l'overlay : celui de la
/// fenêtre au premier plan, donc du jeu. Quand c'est le bureau ou la barre des tâches (ou aucune
/// fenêtre, le temps d'un changement de focus), l'overlay reste sur son écran actuel.
fn target_screen(overlay: HWND) -> Option<(RECT, u32)> {
    let fg = unsafe { GetForegroundWindow() };
    let follow = !fg.is_invalid() && !is_shell_window(fg);
    let screen = if follow { screen_of(fg) } else { None };
    // Au lancement, l'écran actuel est l'écran principal, où la fenêtre est créée.
    screen.or_else(|| screen_of(overlay))
}

/// Rectangle et DPI de l'écran qui contient (la plus grande partie de) cette fenêtre.
fn screen_of(window: HWND) -> Option<(RECT, u32)> {
    unsafe {
        let monitor = MonitorFromWindow(window, MONITOR_DEFAULTTOPRIMARY);
        let mut info = MONITORINFO { cbSize: size_of::<MONITORINFO>() as u32, ..Default::default() };
        let (mut dpi, mut dpi_y) = (0, 0);
        // Échoue si l'écran vient d'être débranché.
        if GetMonitorInfoW(monitor, &mut info).as_bool()
            && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &mut dpi, &mut dpi_y).is_ok()
        {
            Some((info.rcMonitor, dpi))
        } else {
            None
        }
    }
}

fn is_shell_window(hwnd: HWND) -> bool {
    let mut class = [0u16; 256];
    let len = unsafe { GetClassNameW(hwnd, &mut class) };
    is_shell_class(&String::from_utf16_lossy(&class[..usize::try_from(len).unwrap_or(0)]))
}

/// Bureau et barres des tâches (écran principal et autres écrans) : cliquer dessus ne doit pas
/// faire changer l'overlay d'écran.
fn is_shell_class(class: &str) -> bool {
    matches!(class, "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd")
}

/// Position de l'overlay (taille `size`) dans le coin `corner` de l'écran : 0 en haut à gauche, puis
/// dans le sens des aiguilles d'une montre. Coordonnées négatives pour un écran à gauche ou au-dessus
/// de l'écran principal.
fn corner_position(screen: RECT, (w, h): (i32, i32), margin: i32, corner: u8) -> (i32, i32) {
    let (left, top) = (screen.left + margin, screen.top + margin);
    let (right, bottom) = (screen.right - w - margin, screen.bottom - h - margin);
    match corner {
        0 => (left, top),
        1 => (right, top),
        2 => (right, bottom),
        _ => (left, bottom),
    }
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

/// À chaque tic du minuteur, overlay masqué compris : note si la collecte a publié une nouvelle mesure.
/// Réaffiché pendant que la collecte est bloquée, l'overlay le signale ainsi tout de suite.
fn tick() {
    STATE.with(|cell| {
        if let Some(st) = cell.borrow_mut().as_mut() {
            let taken = st.shared.lock().ok().and_then(|s| s.taken);
            st.freshness.tick(taken);
        }
    });
}

fn set_visible(hwnd: HWND, st: &mut State, visible: bool) {
    if st.visible == visible {
        return;
    }
    st.visible = visible;
    // Masqué : la collecte se met en pause (session ETW comprise) pour ne rien consommer.
    if visible {
        st.control.resume();
    } else {
        st.control.pause();
    }
    unsafe {
        let _ = ShowWindow(hwnd, if visible { SW_SHOWNOACTIVATE } else { SW_HIDE });
    }
}

fn on_hotkey(hwnd: HWND, id: i32) {
    let mut quit = false;
    STATE.with(|cell| {
        let mut guard = cell.borrow_mut();
        let Some(st) = guard.as_mut() else { return };
        match id {
            HK_DETAILS => st.detailed = !st.detailed,
            HK_TOGGLE => {
                let visible = !st.visible;
                set_visible(hwnd, st, visible);
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
            tick();
            refresh(hwnd);
            LRESULT(0)
        }
        WM_PAINT => {
            paint(hwnd);
            LRESULT(0)
        }
        WM_ERASEBKGND => LRESULT(1),
        WM_DPICHANGED => {
            // refresh() calcule déjà la taille au DPI de l'écran visé avant d'y déplacer l'overlay,
            // et suit dans la seconde un changement d'échelle : le rectangle proposé ne sert pas.
            LRESULT(0)
        }
        WM_HOTKEY => {
            on_hotkey(hwnd, wparam.0 as i32);
            LRESULT(0)
        }
        WM_APP_SHOW => {
            STATE.with(|cell| {
                if let Some(st) = cell.borrow_mut().as_mut() {
                    set_visible(hwnd, st, true);
                    st.hint_until = Instant::now() + HINT;
                }
            });
            refresh(hwnd);
            LRESULT(0)
        }
        WM_ENDSESSION => {
            // Fermeture de session ou arrêt de Windows : le processus sera tué juste après,
            // sans repasser par main. On ferme la session ETW tout de suite.
            if wparam.0 != 0 {
                metrics::emergency_stop();
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            // main arrête ensuite proprement la collecte (et la session ETW).
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn corners_of_the_screen() {
        let screen = RECT { left: 0, top: 0, right: 1920, bottom: 1080 };
        assert_eq!(corner_position(screen, (300, 200), 10, 0), (10, 10));
        assert_eq!(corner_position(screen, (300, 200), 10, 1), (1610, 10));
        assert_eq!(corner_position(screen, (300, 200), 10, 2), (1610, 870));
        assert_eq!(corner_position(screen, (300, 200), 10, 3), (10, 870));
    }

    #[test]
    fn screen_left_of_or_above_the_primary_one() {
        // Écran à gauche de l'écran principal, et plus haut que lui : coordonnées négatives.
        let left = RECT { left: -2560, top: -360, right: 0, bottom: 1080 };
        assert_eq!(corner_position(left, (300, 200), 15, 0), (-2545, -345));
        assert_eq!(corner_position(left, (300, 200), 15, 2), (-315, 865));
        let above = RECT { left: 0, top: -1080, right: 1920, bottom: 0 };
        assert_eq!(corner_position(above, (300, 200), 10, 1), (1610, -1070));
        assert_eq!(corner_position(above, (300, 200), 10, 3), (10, -210));
    }

    #[test]
    fn desktop_and_taskbars_keep_the_screen() {
        for class in ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"] {
            assert!(is_shell_class(class), "{class}");
        }
        for class in ["UnrealWindow", "UnityWndClass", "SDL_app", ""] {
            assert!(!is_shell_class(class), "{class}");
        }
    }
}

