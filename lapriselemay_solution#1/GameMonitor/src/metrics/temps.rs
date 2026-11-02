//! Température CPU via WMI.
//!
//! Windows n'expose pas la température des cœurs sans pilote noyau. On essaie donc :
//! 1. LibreHardwareMonitor s'il tourne (lecture précise, il publie ses capteurs dans WMI) ;
//! 2. la zone thermique ACPI de la carte mère (admin requis, pas disponible partout).

use std::collections::HashMap;
use std::time::{Duration, Instant};

use wmi::{Variant, WMIConnection};

type Row = HashMap<String, Variant>;

const RETRY: Duration = Duration::from_secs(30);

pub struct Temps {
    lhm: Option<WMIConnection>,
    acpi: Option<WMIConnection>,
    next_retry: Instant,
}

fn as_f64(v: Option<&Variant>) -> Option<f64> {
    match v? {
        Variant::R4(x) => Some(*x as f64),
        Variant::R8(x) => Some(*x),
        Variant::UI4(x) => Some(*x as f64),
        Variant::I4(x) => Some(*x as f64),
        _ => None,
    }
}

fn as_str(v: Option<&Variant>) -> &str {
    match v {
        Some(Variant::String(s)) => s,
        _ => "",
    }
}

impl Temps {
    pub fn new() -> Self {
        let mut t = Self { lhm: None, acpi: None, next_retry: Instant::now() };
        t.connect();
        t
    }

    fn connect(&mut self) {
        if self.lhm.is_none() {
            self.lhm = WMIConnection::with_namespace_path(r"ROOT\LibreHardwareMonitor").ok();
        }
        if self.acpi.is_none() {
            self.acpi = WMIConnection::with_namespace_path(r"ROOT\WMI").ok();
        }
        self.next_retry = Instant::now() + RETRY;
    }

    /// Renvoie (température °C, source).
    pub fn cpu(&mut self) -> (Option<f32>, &'static str) {
        // Réessai périodique même quand l'ACPI répond : LibreHardwareMonitor, plus précis,
        // a pu être lancé après GameMonitor (ou relancé après avoir été fermé).
        if (self.lhm.is_none() || self.acpi.is_none()) && Instant::now() >= self.next_retry {
            self.connect();
        }
        if let Some(t) = self.read_lhm() {
            return (Some(t), "LHM");
        }
        if let Some(t) = self.read_acpi() {
            return (Some(t), "ACPI");
        }
        (None, "")
    }

    fn read_lhm(&mut self) -> Option<f32> {
        let con = self.lhm.as_ref()?;
        let rows: Vec<Row> = match con.raw_query(
            "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'",
        ) {
            Ok(r) => r,
            Err(_) => {
                self.lhm = None;
                return None;
            }
        };
        let cpu: Vec<&Row> = rows
            .iter()
            .filter(|r| as_str(r.get("Identifier")).contains("cpu"))
            .collect();
        // « CPU Package » en priorité, sinon le capteur CPU le plus chaud.
        cpu.iter()
            .find(|r| as_str(r.get("Name")).contains("Package"))
            .and_then(|r| as_f64(r.get("Value")))
            .or_else(|| {
                cpu.iter()
                    .filter_map(|r| as_f64(r.get("Value")))
                    .reduce(f64::max)
            })
            .map(|t| t as f32)
    }

    fn read_acpi(&mut self) -> Option<f32> {
        let con = self.acpi.as_ref()?;
        let rows: Vec<Row> =
            match con.raw_query("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature") {
                Ok(r) => r,
                Err(_) => {
                    // Pas disponible sur ce PC (ou pas admin) : inutile de réessayer à chaque seconde.
                    self.acpi = None;
                    return None;
                }
            };
        rows.iter()
            .filter_map(|r| as_f64(r.get("CurrentTemperature")))
            .map(|dk| dk / 10.0 - 273.15) // dixièmes de kelvin → °C
            .filter(|c| (1.0..125.0).contains(c))
            .reduce(f64::max)
            .map(|t| t as f32)
    }
}
