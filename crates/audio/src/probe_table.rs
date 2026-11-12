//! Platform-independent, lossless query records and CSV/table presentation.
use crate::{DataFlow, ExclusivePeriods, Format, SharedPeriods};

/// Missing properties are distinct from a failed query (including its HRESULT).
pub type Query<T> = Result<Option<T>, String>;

#[derive(Clone, Debug)]
pub struct EffectRegistration {
    pub source: String,
    pub key: String,
    pub endpoint: bool,
    pub clsids: Query<String>,
}

#[derive(Clone, Debug)]
pub struct DeviceProbe {
    pub flow: DataFlow,
    pub endpoint_id: Query<String>,
    pub friendly_name: Query<String>,
    pub state: Query<String>,
    pub default_console: Query<bool>,
    pub default_communications: Query<bool>,
    pub driver: Query<String>,
    pub mix_format: Query<Format>,
    /// Raw WAVEFORMATEX details, including extensible subtype and valid bits.
    pub mix_details: Query<String>,
    pub device_format: Query<Format>,
    /// Raw default-device WAVEFORMATEX details, including extensible fields.
    pub device_format_details: Query<String>,
    pub shared: Query<SharedPeriods>,
    pub exclusive: Query<ExclusivePeriods>,
    pub raw_supported: Query<bool>,
    pub effects: Vec<EffectRegistration>,
}

#[derive(Clone, Debug)]
pub struct Metadata {
    pub executed_at: String,
    pub os_version: String,
    pub tool_version: String,
    pub commit: String,
    pub all_states: bool,
}

pub fn frames_ms(frames: u32, sample_rate: u32) -> Option<f64> {
    (sample_rate != 0).then(|| f64::from(frames) * 1000.0 / f64::from(sample_rate))
}

pub fn hns_ms(hns: crate::Hns) -> f64 {
    hns.0 as f64 / 10_000.0
}

pub fn cell<T>(q: &Query<T>, value: impl FnOnce(&T) -> String) -> String {
    match q {
        Ok(Some(v)) => value(v),
        Ok(None) => "None".into(),
        Err(e) => format!("ERROR: {e}"),
    }
}

pub fn csv_field(value: &str) -> String {
    if value.contains([',', '"', '\r', '\n']) {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.into()
    }
}

fn row(out: &mut String, values: &[String]) {
    out.push_str(
        &values
            .iter()
            .map(|v| csv_field(v))
            .collect::<Vec<_>>()
            .join(","),
    );
    out.push_str("\r\n");
}

fn text(q: &Query<String>) -> String {
    cell(q, Clone::clone)
}
fn boolean(q: &Query<bool>) -> String {
    cell(q, ToString::to_string)
}
fn format(q: &Query<Format>, details_column: &str) -> String {
    cell(q, |f| {
        let kind = if f.float {
            "float".into()
        } else {
            format!("PCM/other (see {details_column})")
        };
        format!(
            "{} Hz / {} ch / {} bit / {}",
            f.sample_rate, f.channels, f.bits_per_sample, kind
        )
    })
}

pub fn endpoint_warning(d: &DeviceProbe) -> &'static str {
    if d.flow == DataFlow::Render
        && d.effects
            .iter()
            .any(|e| e.endpoint && matches!(&e.clsids, Ok(Some(v)) if !v.is_empty()))
    {
        "WARNING: endpoint FX registered"
    } else if d.effects.iter().any(|e| e.endpoint && e.clsids.is_err()) {
        "EFX unknown (query error)"
    } else {
        "No endpoint FX found in checked keys"
    }
}

fn shared_ms(d: &DeviceProbe, select: impl FnOnce(&SharedPeriods) -> u32) -> String {
    cell(&d.shared, |p| match &d.mix_format {
        Ok(Some(f)) => {
            frames_ms(select(p), f.sample_rate).map_or_else(|| "None".into(), |v| format!("{v:.6}"))
        }
        Err(e) => format!("ERROR: {e}"),
        _ => "None".into(),
    })
}

pub fn table(devices: &[DeviceProbe]) -> String {
    let mut out = String::from(
        "FLOW | NAME | STATE | DEFAULT console/communications | DRIVER\n  MIX | DEVFMT | SHARED min/fundamental ms | EXCLUSIVE min ms | RAW | EFX\n",
    );
    for d in devices {
        let clean = |s: String| s.replace(['\r', '\n', '\t'], " ");
        out.push_str(&format!(
            "{:?} | {} | {} | {}/{} | {}\n  {} | {} | {}/{} | {} | {} | {}\n",
            d.flow,
            clean(text(&d.friendly_name)),
            text(&d.state),
            boolean(&d.default_console),
            boolean(&d.default_communications),
            clean(text(&d.driver)),
            format(&d.mix_format, "mix_details"),
            format(&d.device_format, "device_format_details"),
            shared_ms(d, |p| p.min_frames),
            shared_ms(d, |p| p.fundamental_frames),
            cell(&d.exclusive, |p| std::format!("{:.6}", hns_ms(p.minimum))),
            boolean(&d.raw_supported),
            endpoint_warning(d)
        ));
    }
    out
}

/// One rectangular CSV: metadata is repeated per row; a metadata-only row is
/// emitted for zero devices. Effects have separate rows to retain every result.
pub fn csv(meta: &Metadata, devices: &[DeviceProbe]) -> String {
    let headers = [
        "executed_at",
        "os_version",
        "tool_version",
        "commit",
        "all_states",
        "record",
        "flow",
        "endpoint_id",
        "friendly_name",
        "state",
        "default_console",
        "default_communications",
        "driver",
        "mix_format",
        "mix_details",
        "device_format",
        "device_format_details",
        "shared_default_frames",
        "shared_fundamental_frames",
        "shared_min_frames",
        "shared_max_frames",
        "shared_default_ms",
        "shared_fundamental_ms",
        "shared_min_ms",
        "shared_max_ms",
        "exclusive_default_hns",
        "exclusive_min_hns",
        "exclusive_default_ms",
        "exclusive_min_ms",
        "raw_supported",
        "efx_warning",
        "effect_source",
        "effect_key",
        "effect_endpoint",
        "effect_clsids",
    ];
    let mut out = String::new();
    row(&mut out, &headers.map(String::from));
    let base = || {
        vec![
            meta.executed_at.clone(),
            meta.os_version.clone(),
            meta.tool_version.clone(),
            meta.commit.clone(),
            meta.all_states.to_string(),
        ]
    };
    if devices.is_empty() {
        let mut v = base();
        v.push("metadata".into());
        v.resize(headers.len(), String::new());
        row(&mut out, &v);
    }
    for d in devices {
        let mut v = base();
        v.extend([
            "device".into(),
            std::format!("{:?}", d.flow),
            text(&d.endpoint_id),
            text(&d.friendly_name),
            text(&d.state),
            boolean(&d.default_console),
            boolean(&d.default_communications),
            text(&d.driver),
            format(&d.mix_format, "mix_details"),
            text(&d.mix_details),
            format(&d.device_format, "device_format_details"),
            text(&d.device_format_details),
        ]);
        for select in [
            |p: &SharedPeriods| p.default_frames,
            |p: &SharedPeriods| p.fundamental_frames,
            |p: &SharedPeriods| p.min_frames,
            |p: &SharedPeriods| p.max_frames,
        ] {
            v.push(cell(&d.shared, |p| select(p).to_string()));
        }
        for select in [
            |p: &SharedPeriods| p.default_frames,
            |p: &SharedPeriods| p.fundamental_frames,
            |p: &SharedPeriods| p.min_frames,
            |p: &SharedPeriods| p.max_frames,
        ] {
            v.push(shared_ms(d, select));
        }
        v.extend([
            cell(&d.exclusive, |p| p.default.0.to_string()),
            cell(&d.exclusive, |p| p.minimum.0.to_string()),
            cell(&d.exclusive, |p| std::format!("{:.6}", hns_ms(p.default))),
            cell(&d.exclusive, |p| std::format!("{:.6}", hns_ms(p.minimum))),
            boolean(&d.raw_supported),
            endpoint_warning(d).into(),
        ]);
        v.resize(headers.len(), String::new());
        row(&mut out, &v);
        for effect in &d.effects {
            let mut v = base();
            v.extend([
                "effect".into(),
                std::format!("{:?}", d.flow),
                text(&d.endpoint_id),
            ]);
            v.resize(headers.len(), String::new());
            let effect_source = headers
                .iter()
                .position(|header| *header == "effect_source")
                .expect("effect_source header");
            v[effect_source..effect_source + 4].clone_from_slice(&[
                effect.source.clone(),
                effect.key.clone(),
                effect.endpoint.to_string(),
                text(&effect.clsids),
            ]);
            row(&mut out, &v);
        }
    }
    out
}
