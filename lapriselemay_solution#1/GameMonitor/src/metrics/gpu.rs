//! GPU : NVML (fourni avec le pilote NVIDIA) si disponible,
//! sinon compteurs Windows génériques (marche aussi pour Intel et AMD).

use nvml_wrapper::Nvml;
use nvml_wrapper::enum_wrappers::device::{Clock, TemperatureSensor};

use super::pdh::{Counter, Pdh};

#[derive(Clone, Default)]
pub struct GpuInfo {
    pub name: String,
    pub usage: Option<f32>,
    pub vram_used: Option<u64>,
    pub vram_total: Option<u64>,
    pub temp: Option<u32>,
    pub power_w: Option<f32>,
    pub clock_mhz: Option<u32>,
    pub mem_clock_mhz: Option<u32>,
    pub fan: Option<u32>,
}

pub struct Gpu {
    nvml: Option<Nvml>,
    name: String,
    engine: Option<Counter>,
    dedicated: Option<Counter>,
}

impl Gpu {
    pub fn new(pdh: Option<&Pdh>, fallback_name: String) -> Self {
        let nvml = Nvml::init()
            .ok()
            .filter(|n| n.device_count().map(|c| c > 0).unwrap_or(false));
        let name = nvml
            .as_ref()
            .and_then(|n| n.device_by_index(0).ok()?.name().ok())
            .unwrap_or(fallback_name);
        // Les compteurs génériques ne servent que sans NVIDIA.
        let pdh = pdh.filter(|_| nvml.is_none());
        let engine = pdh.and_then(|p| p.add(r"\GPU Engine(*engtype_3D)\Utilization Percentage"));
        let dedicated = pdh.and_then(|p| p.add(r"\GPU Adapter Memory(*)\Dedicated Usage"));
        Self { nvml, name, engine, dedicated }
    }

    pub fn read(&self) -> GpuInfo {
        let mut info = GpuInfo { name: self.name.clone(), ..Default::default() };
        if let Some(nvml) = &self.nvml {
            if let Ok(dev) = nvml.device_by_index(0) {
                info.usage = dev.utilization_rates().ok().map(|u| u.gpu as f32);
                if let Ok(mem) = dev.memory_info() {
                    info.vram_used = Some(mem.total.saturating_sub(mem.free));
                    info.vram_total = Some(mem.total);
                }
                info.temp = dev.temperature(TemperatureSensor::Gpu).ok();
                info.power_w = dev.power_usage().ok().map(|mw| mw as f32 / 1000.0);
                info.clock_mhz = dev.clock_info(Clock::Graphics).ok();
                info.mem_clock_mhz = dev.clock_info(Clock::Memory).ok();
                info.fan = dev.fan_speed(0).ok();
            }
            return info;
        }
        if let Some(engine) = &self.engine {
            let total: f64 = engine.values().iter().map(|(_, v)| v).sum();
            info.usage = Some(total.min(100.0) as f32);
        }
        if let Some(dedicated) = &self.dedicated {
            let max = dedicated.values().iter().map(|(_, v)| *v).fold(0.0, f64::max);
            if max > 0.0 {
                info.vram_used = Some(max as u64);
            }
        }
        info
    }
}
