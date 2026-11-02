//! Transforme les mesures en lignes de texte colorées pour l'overlay.

use std::time::{Duration, Instant};

use windows::Win32::Foundation::COLORREF;

use crate::metrics::{MAX_AGE, Snapshot};

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

/// Durée : « 12 s », « 2 min 05 s », « 1 h 05 ».
fn duration(d: Duration) -> String {
    let secs = d.as_secs();
    match secs {
        0..60 => format!("{secs} s"),
        60..3600 => format!("{} min {:02} s", secs / 60, secs % 60),
        _ => format!("{} h {:02}", secs / 3600, secs / 60 % 60),
    }
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

/// Ligne d'avertissement quand Windows a refusé certains raccourcis (déjà pris ailleurs).
pub fn hotkey_warning(keys: &str) -> Line {
    Line::new("").add(format!("Raccourci indisponible : Ctrl+Maj+{keys}"), WARN)
}

/// Ligne d'avertissement quand la dernière mesure date de plus de [`MAX_AGE`].
fn frozen_warning(age: Duration) -> Line {
    Line::new("").add(format!("Mesures figées depuis {}", duration(age)), WARN)
}

/// Lignes de l'overlay pour la mesure `s`, affichée à l'heure `now`.
pub fn build_lines(s: &Snapshot, now: Instant, detailed: bool, hint: bool) -> Vec<Line> {
    let mut lines = Vec::new();
    let Some(taken) = s.taken else {
        // Au lancement, et à la réapparition après une pause : la première mesure prend 1 s.
        // Il n'y a encore rien de mesuré, donc rien de figé.
        lines.push(Line::new("").add("GameMonitor : mesure en cours…", DIM));
        return lines;
    };

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
        // La zone ACPI est un capteur de carte mère, pas celui du CPU : on dit d'où vient la mesure.
        let cpu = match s.cpu_temp_source {
            "" => s.cpu_name.clone(),
            source => format!("{} · temp. {source}", s.cpu_name),
        };
        lines.push(Line::new("").add(cpu, DIM));
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

    // Plus de nouvelle mesure alors que l'overlay est affiché : la collecte est bloquée.
    // Les valeurs passent en gris pour ne pas les faire passer pour actuelles, et on dit depuis quand.
    let age = now.saturating_duration_since(taken);
    if age > MAX_AGE {
        for (_, color) in lines.iter_mut().flat_map(|l| &mut l.parts) {
            *color = DIM;
        }
        lines.insert(0, frozen_warning(age));
    }
    lines
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::metrics::FpsInfo;

    fn text(line: &Line) -> String {
        line.parts.iter().map(|(t, _)| t.as_str()).collect()
    }

    fn find<'a>(lines: &'a [Line], label: &str) -> &'a Line {
        lines.iter().find(|l| l.label == label).expect(label)
    }

    /// Une mesure qui vient d'être prise.
    fn measured() -> Snapshot {
        Snapshot { taken: Some(Instant::now()), ..Default::default() }
    }

    /// Lignes affichées à l'instant même de la mesure.
    fn fresh(s: &Snapshot, detailed: bool, hint: bool) -> Vec<Line> {
        build_lines(s, s.taken.unwrap_or_else(Instant::now), detailed, hint)
    }

    #[test]
    fn french_decimals_and_units() {
        assert_eq!(dec(12.3456, 2), "12,35");
        assert_eq!(bytes(999.0 * 1024.0), "999 Ko");
        // Jamais « 1000 Ko » : on passe aux Mo dès 1000 Ko.
        assert_eq!(bytes(1000.0 * 1024.0), "1,0 Mo");
        assert_eq!(bytes(8.0 * 1024.0 * 1024.0 * 1024.0), "8,0 Go");
        assert_eq!(rate(2.5 * 1024.0 * 1024.0), "2,5 Mo/s");
    }

    #[test]
    fn waiting_for_the_first_measure() {
        // Au lancement et après une pause, rien n'est encore mesuré : jamais signalé comme figé.
        let lines = build_lines(&Snapshot::default(), Instant::now() + Duration::from_secs(60), true, true);
        assert_eq!(lines.len(), 1);
        assert!(text(&lines[0]).contains("mesure en cours"));
    }

    #[test]
    fn fps_line() {
        let mut s = measured();
        assert_eq!(text(find(&fresh(&s, false, false), "FPS")), "lancer en admin");

        s.fps_available = true;
        assert_eq!(text(find(&fresh(&s, false, false), "FPS")), "–");

        s.fps = Some(FpsInfo { fps: 143.6, frametime_ms: 6.96, worst_ms: 12.04 });
        s.foreground = "Game".into();
        assert_eq!(text(find(&fresh(&s, false, false), "FPS")), "144  7,0 ms  pire 12,0 ms  Game");
    }

    #[test]
    fn cpu_line_and_temperature_source() {
        let mut s = Snapshot { cpu_name: "Intel Core i5-14400F".into(), ..measured() };
        s.cpu_usage = 42.4;
        s.cpu_mhz = Some(4_650.0);
        assert_eq!(text(find(&fresh(&s, false, false), "CPU")), "42 %  4,65 GHz  temp. n/d");

        s.cpu_temp = Some(61.6);
        s.cpu_temp_source = "ACPI";
        let lines = fresh(&s, true, false);
        assert_eq!(text(find(&lines, "CPU")), "42 %  4,65 GHz  62 °C");
        assert!(lines.iter().any(|l| text(l) == "Intel Core i5-14400F · temp. ACPI"));
    }

    #[test]
    fn hint_and_hotkey_warning() {
        let s = measured();
        let hint = |lines: &[Line]| lines.iter().any(|l| text(l).starts_with("Ctrl+Maj+F9"));
        assert!(hint(&fresh(&s, false, true)));
        assert!(!hint(&fresh(&s, false, false)));
        assert_eq!(text(&hotkey_warning("F9 F12")), "Raccourci indisponible : Ctrl+Maj+F9 F12");
    }

    #[test]
    fn frozen_measures_are_greyed_with_a_warning() {
        let s = Snapshot { cpu_usage: 95.0, cpu_temp: Some(88.0), ..measured() };
        let taken = s.taken.unwrap();

        // Jusqu'à MAX_AGE, la mesure est actuelle : pas d'avertissement, couleurs habituelles.
        let lines = build_lines(&s, taken + MAX_AGE, false, false);
        assert_eq!(lines[0].label, "FPS");
        assert!(find(&lines, "CPU").parts.iter().any(|(_, c)| *c == BAD));

        // Au-delà : avertissement en tête, et toutes les valeurs en gris.
        let lines = build_lines(&s, taken + Duration::from_secs(12), false, true);
        assert_eq!(text(&lines[0]), "Mesures figées depuis 12 s");
        assert!(lines[0].parts.iter().all(|(_, c)| *c == WARN));
        assert_eq!(text(find(&lines, "CPU")), "95 %  88 °C");
        assert!(lines[1..].iter().flat_map(|l| &l.parts).all(|(_, c)| *c == DIM));
    }

    #[test]
    fn frozen_warning_duration() {
        assert_eq!(text(&frozen_warning(Duration::from_millis(12_900))), "Mesures figées depuis 12 s");
        assert_eq!(text(&frozen_warning(Duration::from_secs(125))), "Mesures figées depuis 2 min 05 s");
        assert_eq!(text(&frozen_warning(Duration::from_secs(3_900))), "Mesures figées depuis 1 h 05");
    }
}
