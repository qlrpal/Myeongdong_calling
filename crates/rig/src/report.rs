//! CSV reporting. Each summary is one recording/restart; aggregation never pools markers.
use crate::analyze::{Analysis, Class, LeftClass, median};
use crate::{RigError, StereoRecording};
use std::{
    collections::BTreeMap,
    io::{Read, Write},
};
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
pub const DETECTION_RATE: &str =
    "marker_detection_rate_proxy_for_original_sample_preservation (원본 샘플 보존율의 대리 지표)";
/// Marker detection and segment presence are proxies, not original sample preservation.
/// 마커 검출률은 원본 샘플 보존율의 대리 지표다.
pub const SUMMARY_HEADER: &[&str] = &[
    "analyzer_version",
    "settings",
    "calibration",
    "window_min_samples",
    "window_max_samples",
    "label",
    "input",
    "frames",
    "duration_seconds",
    "truncated",
    "peak_left_dbfs",
    "peak_right_dbfs",
    "clipped_left",
    "clipped_right",
    "warnings",
    "alignment_offset_samples",
    "alignment_ratio",
    "alignment_ppm",
    "alignment_residual_rms_samples",
    "alignment_residual_max_samples",
    "alignment_anchors",
    "observable",
    "censored",
    "l_ok",
    "l_id_erased",
    "l_id_undecodable",
    "l_id_mismatch",
    "l_missing",
    "l_extra",
    "l_evaluable_rate",
    "normal",
    "id_undecodable",
    "id_mismatch",
    "ambiguous",
    "missing",
    "r_duplicate",
    "r_orphan",
    "r_unassigned_undecodable",
    "r_corrected",
    DETECTION_RATE,
    "latency_inclusion_rate",
    "segment_preservation_rate_proxy",
    "confirmed_missing_rate",
    "unresolved_rate",
    "p50_samples",
    "p95_samples",
    "p99_samples",
    "min_samples",
    "max_samples",
    "p50_ms",
    "p95_ms",
    "p99_ms",
    "min_ms",
    "max_ms",
];
pub const MARKER_HEADER: &[&str] = &[
    "id",
    "predicted_left_samples",
    "left_class",
    "left_extra",
    "left_time_samples",
    "left_id",
    "left_q",
    "left_amplitude",
    "left_overlap",
    "class",
    "reason",
    "right_time_samples",
    "right_id",
    "right_q",
    "right_amplitude",
    "right_segments_present",
    "right_overlap",
    "delay_raw_samples",
    "delay_channel_corrected_samples",
    "delay_raw_ms",
    "delay_channel_corrected_ms",
    "near_count",
    "near_times_samples",
    "near_ids",
    "near_amplitudes",
    "near_overlaps",
];
const BAD: RigError = RigError::Invalid("CSV schema or value");
pub fn number(v: Option<f64>) -> String {
    v.filter(|v| v.is_finite())
        .map_or_else(|| "NA".into(), |v| format!("{v:.12}"))
}
pub fn percentile(values: &[f64], percent: usize) -> Option<f64> {
    if !(1..=100).contains(&percent) {
        return None;
    }
    let mut v: Vec<_> = values.iter().copied().filter(|v| v.is_finite()).collect();
    v.sort_by(f64::total_cmp);
    let rank = v.len().checked_mul(percent)?.div_ceil(100).checked_sub(1)?;
    v.get(rank).copied()
}
pub fn sample_stddev(v: &[f64]) -> Option<f64> {
    if v.len() < 2 || v.iter().any(|x| !x.is_finite()) {
        return None;
    }
    let mean = v.iter().sum::<f64>() / v.len() as f64;
    Some((v.iter().map(|x| (x - mean).powi(2)).sum::<f64>() / (v.len() - 1) as f64).sqrt())
}
fn rate(n: usize, d: usize) -> String {
    number((d > 0).then(|| n as f64 / d as f64))
}
fn row(w: &mut impl Write, cells: &[String]) -> Result<(), RigError> {
    for (i, s) in cells.iter().enumerate() {
        if i > 0 {
            w.write_all(b",")?;
        }
        if s.contains([',', '"', '\r', '\n']) {
            write!(w, "\"{}\"", s.replace('"', "\"\""))?;
        } else {
            w.write_all(s.as_bytes())?;
        }
    }
    w.write_all(b"\n")?;
    Ok(())
}
fn header(w: &mut impl Write, cols: &[&str]) -> Result<(), RigError> {
    row(w, &cols.iter().map(|s| (*s).into()).collect::<Vec<_>>())
}
/// Strict RFC-style quoting, including embedded newlines and doubled quotes.
fn parse_csv(s: &str) -> Result<Vec<Vec<String>>, RigError> {
    let mut rows = Vec::new();
    let mut cells = Vec::new();
    let mut field = String::new();
    let mut chars = s.chars().peekable();
    let mut quoted = false;
    let mut closed = false;
    while let Some(ch) = chars.next() {
        if quoted {
            if ch == '"' {
                if chars.peek() == Some(&'"') {
                    chars.next();
                    field.push('"');
                } else {
                    quoted = false;
                    closed = true;
                }
            } else {
                field.push(ch);
            }
            continue;
        }
        match ch {
            '"' if field.is_empty() && !closed => quoted = true,
            ',' => {
                cells.push(std::mem::take(&mut field));
                closed = false;
            }
            '\n' => {
                cells.push(std::mem::take(&mut field));
                rows.push(std::mem::take(&mut cells));
                closed = false;
            }
            '\r' if chars.peek() == Some(&'\n') => {}
            _ if closed || ch == '"' => return Err(BAD),
            _ => field.push(ch),
        }
    }
    if quoted {
        return Err(BAD);
    }
    if !field.is_empty() || !cells.is_empty() || closed {
        cells.push(field);
        rows.push(cells);
    }
    Ok(rows)
}
#[derive(Clone, Debug)]
pub struct RunSummary {
    pub fields: BTreeMap<String, String>,
}
impl RunSummary {
    pub fn get(&self, k: &str) -> Result<&str, RigError> {
        self.fields.get(k).map(String::as_str).ok_or(BAD)
    }
    pub fn value(&self, k: &str) -> Result<Option<f64>, RigError> {
        let s = self.get(k)?;
        if s == "NA" {
            return Ok(None);
        }
        let n: f64 = s.parse().map_err(|_| BAD)?;
        if !n.is_finite() {
            return Err(BAD);
        }
        Ok(Some(n))
    }
    pub fn count(&self, k: &str) -> Result<usize, RigError> {
        self.get(k)?.parse().map_err(|_| BAD)
    }
}
pub fn summarize(a: &Analysis, rec: &StereoRecording, label: &str, input: &str) -> RunSummary {
    let mut m = BTreeMap::new();
    let mut put = |k: &str, v: String| {
        m.insert(k.into(), v);
    };
    let observable = a
        .markers
        .iter()
        .filter(|r| r.left_class != LeftClass::Censored)
        .count();
    let lok = a
        .markers
        .iter()
        .filter(|r| r.left_class == LeftClass::Ok)
        .count();
    let count = |class| a.markers.iter().filter(|r| r.class == Some(class)).count();
    let n = count(Class::Normal);
    let u = count(Class::IdUndecodable);
    let mismatch = count(Class::IdMismatch);
    let amb = count(Class::Ambiguous);
    let missing = count(Class::Missing);
    let (lo, hi) = a.calibration.window();
    for (k, v) in [
        ("analyzer_version", VERSION.into()),
        ("settings", a.config.identity()),
        ("calibration", a.calibration.identity()),
        ("label", label.into()),
        ("input", input.into()),
        ("warnings", a.warnings.join(";")),
        ("truncated", rec.truncated.to_string()),
    ] {
        put(k, v);
    }
    for (k, v) in [
        ("window_min_samples", Some(lo)),
        ("window_max_samples", Some(hi)),
        ("duration_seconds", Some(rec.left.len() as f64 / 48000.0)),
        ("peak_left_dbfs", rec.peak_left_dbfs),
        ("peak_right_dbfs", rec.peak_right_dbfs),
        ("alignment_offset_samples", Some(a.alignment.offset)),
        ("alignment_ratio", Some(a.alignment.ratio)),
        ("alignment_ppm", Some(a.alignment.ppm)),
        (
            "alignment_residual_rms_samples",
            Some(a.alignment.residual_rms),
        ),
        (
            "alignment_residual_max_samples",
            Some(a.alignment.residual_max),
        ),
    ] {
        put(k, number(v));
    }
    for (k, v) in [
        ("frames", rec.left.len()),
        ("clipped_left", rec.clipped_left),
        ("clipped_right", rec.clipped_right),
        ("alignment_anchors", a.alignment.anchors),
        ("observable", observable),
        ("censored", a.markers.len() - observable),
        ("l_ok", lok),
        (
            "l_extra",
            a.markers
                .iter()
                .filter(|r| r.left_class != LeftClass::Censored)
                .map(|r| r.left_extra)
                .sum(),
        ),
        ("normal", n),
        ("id_undecodable", u),
        ("id_mismatch", mismatch),
        ("ambiguous", amb),
        ("missing", missing),
        ("r_duplicate", a.r_duplicate),
        ("r_orphan", a.r_orphan),
        ("r_unassigned_undecodable", a.r_unassigned_undecodable),
        (
            "r_corrected",
            a.markers
                .iter()
                .filter(|r| {
                    r.right
                        .as_ref()
                        .is_some_and(|p| matches!(p.id.outcome, crate::IdOutcome::Corrected { .. }))
                })
                .count(),
        ),
    ] {
        put(k, v.to_string());
    }
    for (k, lc) in [
        ("l_id_erased", LeftClass::IdErased),
        ("l_id_undecodable", LeftClass::IdUndecodable),
        ("l_id_mismatch", LeftClass::IdMismatch),
        ("l_missing", LeftClass::Missing),
    ] {
        put(
            k,
            a.markers
                .iter()
                .filter(|r| r.left_class == lc)
                .count()
                .to_string(),
        );
    }
    put("l_evaluable_rate", rate(lok, observable));
    put(DETECTION_RATE, rate(n + u, lok));
    put("latency_inclusion_rate", rate(n, lok));
    put("confirmed_missing_rate", rate(missing, lok));
    put("unresolved_rate", rate(mismatch + amb, lok));
    let segments: usize = a
        .markers
        .iter()
        .filter(|r| matches!(r.class, Some(Class::Normal | Class::IdUndecodable)))
        .filter_map(|r| r.right.as_ref())
        .map(|p| usize::from(p.id.segments_present))
        .sum();
    put(
        "segment_preservation_rate_proxy",
        rate(segments, lok.saturating_mul(17)),
    );
    let delays: Vec<_> = a
        .markers
        .iter()
        .filter(|r| r.class == Some(Class::Normal))
        .filter_map(|r| r.delay)
        .collect();
    let stats = [
        percentile(&delays, 50),
        percentile(&delays, 95),
        percentile(&delays, 99),
        delays.iter().copied().reduce(f64::min),
        delays.iter().copied().reduce(f64::max),
    ];
    for (k, v) in ["p50", "p95", "p99", "min", "max"].iter().zip(stats) {
        put(&format!("{k}_samples"), number(v));
        put(&format!("{k}_ms"), number(v.map(|x| x / 48.0)));
    }
    RunSummary { fields: m }
}
pub fn write_summary(mut w: impl Write, s: &RunSummary) -> Result<(), RigError> {
    header(&mut w, SUMMARY_HEADER)?;
    let cells = SUMMARY_HEADER
        .iter()
        .map(|k| s.get(k).map(str::to_owned))
        .collect::<Result<Vec<_>, _>>()?;
    row(&mut w, &cells)
}
pub fn read_summary(mut r: impl Read) -> Result<RunSummary, RigError> {
    let mut s = String::new();
    r.read_to_string(&mut s)?;
    let rows = parse_csv(&s)?;
    let h = rows.first().ok_or(BAD)?;
    if rows.len() != 2 || h.iter().map(String::as_str).collect::<Vec<_>>() != SUMMARY_HEADER {
        return Err(BAD);
    }
    let values = rows.get(1).ok_or(BAD)?;
    if values.len() != h.len() {
        return Err(BAD);
    }
    let result = RunSummary {
        fields: h.iter().cloned().zip(values.iter().cloned()).collect(),
    };
    for k in SUMMARY_HEADER
        .iter()
        .skip(7)
        .filter(|k| !["truncated", "warnings"].contains(k))
    {
        result.value(k)?;
    }
    Ok(result)
}
pub fn write_markers(mut w: impl Write, a: &Analysis) -> Result<(), RigError> {
    header(&mut w, MARKER_HEADER)?;
    for r in &a.markers {
        let l = r.left.as_ref();
        let p = r.right.as_ref();
        row(
            &mut w,
            &[
                r.id.to_string(),
                number(Some(r.predicted_left)),
                format!("{:?}", r.left_class),
                r.left_extra.to_string(),
                number(l.map(|p| p.sync.time)),
                l.map_or_else(|| "NA".into(), |p| format!("{:?}", p.id.outcome)),
                number(l.map(|p| p.sync.q)),
                number(l.map(|p| p.sync.amplitude)),
                l.map_or_else(|| "NA".into(), |p| p.sync.overlap.to_string()),
                r.class.map_or_else(|| "NA".into(), |c| format!("{c:?}")),
                r.reason.clone(),
                number(p.map(|p| p.sync.time)),
                p.map_or_else(|| "NA".into(), |p| format!("{:?}", p.id.outcome)),
                number(p.map(|p| p.sync.q)),
                number(p.map(|p| p.sync.amplitude)),
                p.map_or_else(|| "NA".into(), |p| p.id.segments_present.to_string()),
                p.map_or_else(|| "NA".into(), |p| p.sync.overlap.to_string()),
                number(r.delay),
                number(r.corrected_delay),
                number(r.delay.map(|d| d / 48.0)),
                number(r.corrected_delay.map(|d| d / 48.0)),
                r.near_candidates.len().to_string(),
                r.near_candidates
                    .iter()
                    .map(|p| number(Some(p.sync.time)))
                    .collect::<Vec<_>>()
                    .join(";"),
                r.near_candidates
                    .iter()
                    .map(|p| format!("{:?}", p.id.outcome))
                    .collect::<Vec<_>>()
                    .join(";"),
                r.near_candidates
                    .iter()
                    .map(|p| number(Some(p.sync.amplitude)))
                    .collect::<Vec<_>>()
                    .join(";"),
                r.near_candidates
                    .iter()
                    .map(|p| p.sync.overlap.to_string())
                    .collect::<Vec<_>>()
                    .join(";"),
            ],
        )?;
    }
    Ok(())
}
#[derive(Debug)]
pub struct Aggregate {
    pub label: String,
    pub fields: BTreeMap<String, String>,
}
pub const AGGREGATE_HEADER: &[&str] = &[
    "label",
    "runs",
    "latency_runs",
    "p50_mean_ms",
    "p50_sample_stddev_ms",
    "p50_median_ms",
    "p50_min_ms",
    "p50_max_ms",
    "p95_median_ms",
    "p95_max_ms",
    "p99_median_ms",
    "p99_max_ms",
    "detection_rate_mean_proxy_for_original_sample_preservation_원본_샘플_보존율의_대리_지표",
    "detection_rate_min_proxy",
    "latency_inclusion_rate_mean",
    "latency_inclusion_rate_min",
    "l_evaluable_rate_mean",
    "l_evaluable_rate_min",
    "normal",
    "id_undecodable",
    "id_mismatch",
    "ambiguous",
    "missing",
    "warning_runs",
];
pub fn aggregate(summaries: &[RunSummary]) -> Result<Vec<Aggregate>, RigError> {
    let first = summaries.first().ok_or(RigError::Invalid("no summaries"))?;
    let mut groups: BTreeMap<String, Vec<&RunSummary>> = BTreeMap::new();
    for s in summaries {
        for k in [
            "analyzer_version",
            "settings",
            "calibration",
            "window_min_samples",
            "window_max_samples",
        ] {
            if s.get(k)? != first.get(k)? {
                return Err(RigError::Invalid("aggregate settings/calibration mismatch"));
            }
        }
        groups.entry(s.get("label")?.into()).or_default().push(s);
    }
    let mut result = Vec::new();
    for (label, group) in groups {
        let mut fields = BTreeMap::new();
        fields.insert("label".into(), label.clone());
        fields.insert("runs".into(), group.len().to_string());
        let collect = |k: &str| -> Result<Vec<f64>, RigError> {
            Ok(group
                .iter()
                .map(|s| s.value(k))
                .collect::<Result<Vec<_>, _>>()?
                .into_iter()
                .flatten()
                .collect())
        };
        let p50 = collect("p50_ms")?;
        fields.insert("latency_runs".into(), p50.len().to_string());
        for (k, v) in [
            ("p50_mean_ms", mean(&p50)),
            ("p50_sample_stddev_ms", sample_stddev(&p50)),
            ("p50_median_ms", median(&p50)),
            ("p50_min_ms", p50.iter().copied().reduce(f64::min)),
            ("p50_max_ms", p50.iter().copied().reduce(f64::max)),
        ] {
            fields.insert(k.into(), number(v));
        }
        for k in ["p95", "p99"] {
            let v = collect(&format!("{k}_ms"))?;
            fields.insert(format!("{k}_median_ms"), number(median(&v)));
            fields.insert(
                format!("{k}_max_ms"),
                number(v.iter().copied().reduce(f64::max)),
            );
        }
        for (source, avg, min) in [
            (
                DETECTION_RATE,
                AGGREGATE_HEADER.get(12).copied().ok_or(BAD)?,
                "detection_rate_min_proxy",
            ),
            (
                "latency_inclusion_rate",
                "latency_inclusion_rate_mean",
                "latency_inclusion_rate_min",
            ),
            (
                "l_evaluable_rate",
                "l_evaluable_rate_mean",
                "l_evaluable_rate_min",
            ),
        ] {
            let v = collect(source)?;
            fields.insert(avg.into(), number(mean(&v)));
            fields.insert(min.into(), number(v.iter().copied().reduce(f64::min)));
        }
        for k in [
            "normal",
            "id_undecodable",
            "id_mismatch",
            "ambiguous",
            "missing",
        ] {
            let total = group
                .iter()
                .try_fold(0usize, |n, s| n.checked_add(s.count(k)?).ok_or(BAD))?;
            fields.insert(k.into(), total.to_string());
        }
        let warning_runs = group.iter().try_fold(0usize, |n, s| {
            Ok::<_, RigError>(n + usize::from(!s.get("warnings")?.is_empty()))
        })?;
        fields.insert("warning_runs".into(), warning_runs.to_string());
        result.push(Aggregate { label, fields });
    }
    Ok(result)
}
fn mean(v: &[f64]) -> Option<f64> {
    (!v.is_empty()).then(|| v.iter().sum::<f64>() / v.len() as f64)
}
pub fn write_aggregate(mut w: impl Write, a: &[Aggregate]) -> Result<(), RigError> {
    header(&mut w, AGGREGATE_HEADER)?;
    for group in a {
        let values = AGGREGATE_HEADER
            .iter()
            .map(|k| group.fields.get(*k).cloned().ok_or(BAD))
            .collect::<Result<Vec<_>, _>>()?;
        row(&mut w, &values)?;
    }
    Ok(())
}
