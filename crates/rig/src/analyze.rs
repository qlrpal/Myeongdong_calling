//! Offline channel alignment and conservative marker matching. Times are ADC samples.
use crate::calib::{Calibration, CalibrationState};
use crate::{
    ChirpConfig, DetectorConfig, IdOutcome, IdReading, RigError, StereoRecording, SyncCandidate,
    amplitude_gate, detect_sync, marker_of_codeword, marker_schedule, read_id,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AnalysisMode {
    Measurement,
    Synthetic,
}
#[derive(Clone, Debug)]
/// Explicit rig-validation thresholds. detection_limit is the maximum linear
/// amplitude gate allowed for a no-detection recording (not a dB value).
pub struct NoDetectionCriteria {
    pub min_left: usize,
    pub min_frames: usize,
    pub detection_limit: f64,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RejectReason {
    CalibrationNotPerformed,
    CalibrationInsufficient,
    ZBasisMissing,
    PpmOutOfRange,
    AlignmentFailed,
    CalibrationMismatch,
}
#[derive(Clone, Debug)]
pub struct AnalysisConfig {
    pub chirp: ChirpConfig,
    pub detector: DetectorConfig,
    pub max_delay: f64,
    pub mode: AnalysisMode,
    pub z: f64,
    pub x: f64,
    pub xtalk_min_candidates: usize,
    pub no_detection: Option<NoDetectionCriteria>,
}
impl Default for AnalysisConfig {
    fn default() -> Self {
        Self {
            chirp: ChirpConfig::default(),
            detector: DetectorConfig::default(),
            max_delay: 48000.0,
            mode: AnalysisMode::Measurement,
            z: 0.0,
            x: 0.0,
            xtalk_min_candidates: 10,
            no_detection: None,
        }
    }
}
impl AnalysisConfig {
    pub fn validate(&self) -> Result<(), RigError> {
        if !self.z.is_finite()
            || !(0.0..=u32::MAX as f64).contains(&self.z)
            || !self.x.is_finite()
            || !(0.0..=u32::MAX as f64).contains(&self.x)
            || self.xtalk_min_candidates == 0
            || self.no_detection.as_ref().is_some_and(|v| {
                v.min_left < 3
                    || v.min_frames == 0
                    || !v.detection_limit.is_finite()
                    || v.detection_limit <= 0.0
            })
        {
            return Err(RigError::Invalid("analysis safety settings"));
        }
        self.chirp.validate()?;
        self.detector.validate()?;
        if !self.max_delay.is_finite() || self.max_delay <= 0.0 || self.max_delay > u32::MAX as f64
        {
            return Err(RigError::Invalid("matching max_delay"));
        }
        Ok(())
    }
    /// Lossless settings identity, including all signal, detector and matching fields.
    pub fn identity(&self) -> String {
        format!(
            "{:?};{:?};max_delay={:?};overlap=marker_len;alignment=exact_ls;anchor_residual_limit=interval/4;l_search=interval/4;undecodable_search=(interval-marker_len)/2;mismatch_search=interval/2;xtalk_margin_samples=2;xtalk_ratio_margin_db=3;normal_selection=earliest;mode={:?};Z={:?};X={:?};N={};no_detection={:?}",
            self.chirp,
            self.detector,
            self.max_delay,
            self.mode,
            self.z,
            self.x,
            self.xtalk_min_candidates,
            self.no_detection
        )
    }
}
#[derive(Clone, Debug)]
pub struct Detection {
    pub sync: SyncCandidate,
    pub id: IdReading,
}
pub fn decoded_id(id: IdOutcome) -> Option<u16> {
    match id {
        IdOutcome::Exact(n)
        | IdOutcome::Corrected { index: n, .. }
        | IdOutcome::ErasureCorrected { index: n, .. } => marker_of_codeword(n),
        _ => None,
    }
}
pub fn detect_channel(c: &AnalysisConfig, x: &[f32]) -> Result<Vec<Detection>, RigError> {
    c.validate()?;
    let gate = amplitude_gate(&c.detector, x);
    Ok(detect_sync(&c.chirp, &c.detector, x)?
        .into_iter()
        .map(|sync| {
            let id = read_id(&c.chirp, &c.detector, x, gate, &sync);
            Detection { sync, id }
        })
        .collect())
}
#[derive(Clone, Debug)]
/// anchors == 0 denotes failed alignment in a rejected Analysis; fit fields are
/// placeholders and must not be interpreted as estimates.
pub struct Alignment {
    pub offset: f64,
    pub ratio: f64,
    pub ppm: f64,
    pub residual_rms: f64,
    pub residual_max: f64,
    pub anchors: usize,
    /// u and u_L = u*sum|h_i| are estimates derived from anchor residuals, not guaranteed upper bounds.
    pub u: f64,
    pub anchor_times: Vec<f64>,
}
impl Alignment {
    pub fn predict(&self, t: f64) -> f64 {
        if self.anchors == 0 {
            return f64::NAN;
        }
        self.offset + self.ratio * t
    }
    /// Least-squares h_i weights on scheduled anchor times.
    pub fn weights(&self, t: f64) -> Vec<f64> {
        let n = self.anchor_times.len() as f64;
        let mean = self.anchor_times.iter().sum::<f64>() / n;
        let xx = self
            .anchor_times
            .iter()
            .map(|x| (x - mean).powi(2))
            .sum::<f64>();
        self.anchor_times
            .iter()
            .map(|x| 1.0 / n + (t - mean) * (x - mean) / xx)
            .collect()
    }
    /// u and u_L = u*sum|h_i| are estimates derived from anchor residuals, not guaranteed upper bounds.
    pub fn uncertainty(&self, t: f64) -> f64 {
        if self.anchors == 0 {
            return f64::NAN;
        }
        self.u
            * self
                .weights(t)
                .iter()
                .map(|h| h.abs())
                .sum::<f64>()
                .max(1.0)
    }
}
pub(crate) fn median(v: &[f64]) -> Option<f64> {
    let mut v: Vec<_> = v.iter().copied().filter(|v| v.is_finite()).collect();
    v.sort_by(f64::total_cmp);
    let n = v.len();
    let hi = *v.get(n / 2)?;
    Some(if n.is_multiple_of(2) {
        (*v.get(n / 2 - 1)? + hi) / 2.0
    } else {
        hi
    })
}
const ALIGN: RigError = RigError::Invalid("정렬 불가: fewer than three consistent Exact anchors");
fn least_squares(points: &[(f64, f64)]) -> Result<(f64, f64), RigError> {
    if points.len() < 3 {
        return Err(ALIGN);
    }
    let distinct: std::collections::BTreeSet<_> = points.iter().map(|p| p.0.to_bits()).collect();
    if distinct.len() < 3 {
        return Err(ALIGN);
    }
    let n = points.len() as f64;
    let mx = points.iter().map(|p| p.0).sum::<f64>() / n;
    let my = points.iter().map(|p| p.1).sum::<f64>() / n;
    let xx = points.iter().map(|p| (p.0 - mx).powi(2)).sum::<f64>();
    let xy = points.iter().map(|p| (p.0 - mx) * (p.1 - my)).sum::<f64>();
    let r = xy / xx;
    let a = my - r * mx;
    if xx <= 0.0 || !a.is_finite() || !r.is_finite() || r <= 0.0 {
        return Err(ALIGN);
    }
    Ok((a, r))
}
pub fn align_left(c: &AnalysisConfig, left: &[Detection]) -> Result<Alignment, RigError> {
    c.validate()?;
    let schedule = marker_schedule(&c.chirp)?;
    let mut points: Vec<_> = left
        .iter()
        .filter_map(|p| {
            if let IdOutcome::Exact(id) = p.id.outcome {
                marker_of_codeword(id)
                    .and_then(|ordinal| schedule.get(usize::from(ordinal)))
                    .map(|(_, t, _)| (*t as f64, p.sync.time))
            } else {
                None
            }
        })
        .collect();
    let (a, r) = least_squares(&points)?;
    let residuals: Vec<_> = points.iter().map(|(x, y)| y - a - r * x).collect();
    let center = median(&residuals).ok_or(ALIGN)?;
    points.retain(|(x, y)| (y - a - r * x - center).abs() <= c.chirp.interval_len as f64 / 4.0);
    let (a, r) = least_squares(&points)?;
    let errors: Vec<_> = points.iter().map(|(x, y)| y - a - r * x).collect();
    let residual_max = errors.iter().map(|e| e.abs()).fold(0.0, f64::max);
    if residual_max > c.chirp.interval_len as f64 / 4.0 {
        return Err(ALIGN);
    }
    let min = points.iter().map(|p| p.0).fold(f64::INFINITY, f64::min);
    let max = points.iter().map(|p| p.0).fold(f64::NEG_INFINITY, f64::max);
    if max - min < 2.0 * c.chirp.interval_len as f64 {
        return Err(ALIGN);
    }
    let &(x_a, y_a) = points
        .iter()
        .min_by(|a, b| a.0.total_cmp(&b.0))
        .ok_or(ALIGN)?;
    let &(x_b, y_b) = points
        .iter()
        .max_by(|a, b| a.0.total_cmp(&b.0))
        .ok_or(ALIGN)?;
    let dx = x_b - x_a;
    let dy = y_b - y_a;
    let u = (residual_max + 1.0).max(2.0);
    // For a least-squares line, dy - r*dx = e_b - e_a, so the residual
    // bound automatically satisfies this explicit consistency check.
    // Effective defenses: constant range >= 2*interval, residual <= interval/4,
    // r > 0 (least_squares), and |ppm| > 100 rejected by match_detections.
    if dy <= 0.0 || (dy - r * dx).abs() > 2.0 * u {
        return Err(ALIGN);
    }
    Ok(Alignment {
        offset: a,
        ratio: r,
        ppm: (r - 1.0) * 1e6,
        residual_rms: (errors.iter().map(|e| e * e).sum::<f64>() / errors.len() as f64).sqrt(),
        residual_max,
        anchors: points.len(),
        u,
        anchor_times: points.iter().map(|p| p.0).collect(),
    })
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LeftClass {
    Ok,
    Ambiguous,
    IdErased,
    IdUndecodable,
    IdMismatch,
    Missing,
    Censored,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Class {
    Normal,
    IdUndecodable,
    IdMismatch,
    Ambiguous,
    Missing,
}
#[derive(Clone, Debug)]
pub struct MarkerResult {
    pub id: u16,
    pub predicted_left: f64,
    /// u and u_L = u*sum|h_i| are estimates derived from anchor residuals, not guaranteed upper bounds.
    pub left_uncertainty: f64,
    /// u and u_L = u*sum|h_i| are estimates derived from anchor residuals, not guaranteed upper bounds.
    pub left_interval: (f64, f64),
    pub left_class: LeftClass,
    pub left_extra: usize,
    pub left: Option<Detection>,
    pub class: Option<Class>,
    pub right: Option<Detection>,
    /// Window candidates remain visible even when an outside path is Normal.
    pub near_candidates: Vec<Detection>,
    /// Raw ADC R-L time, used for matching and the exclusion window.
    pub delay: Option<f64>,
    /// Channel-offset-corrected R-L time, reported alongside raw delay.
    pub corrected_delay: Option<f64>,
    pub reason: String,
}
#[derive(Debug)]
pub struct Analysis {
    pub config: AnalysisConfig,
    pub calibration: Calibration,
    pub alignment: Alignment,
    pub markers: Vec<MarkerResult>,
    pub r_duplicate: usize,
    pub r_orphan: usize,
    pub r_unassigned_undecodable: usize,
    pub warnings: Vec<String>,
    pub reject_reasons: Vec<RejectReason>,
    pub e5_unchecked: bool,
    pub z: f64,
    pub x: f64,
}
/// Analyze pre-detected channels, also used by independent validation harnesses.
pub fn match_detections(
    c: &AnalysisConfig,
    calibration: Option<&Calibration>,
    frames: usize,
    left: &[Detection],
    right: &[Detection],
) -> Result<Analysis, RigError> {
    c.validate()?;
    let absent = Calibration {
        channel_offset: 0.0,
        xtalk: CalibrationState::NotPerformed,
        z: 0.0,
        x: 0.0,
        z_basis: String::new(),
        source_files: String::new(),
    };
    let cal = calibration.unwrap_or(&absent);
    cal.validate()?;
    if left.iter().chain(right).any(|p| {
        !p.sync.time.is_finite()
            || p.sync.time < 0.0
            || !p.sync.amplitude.is_finite()
            || p.sync.amplitude <= 0.0
            || !p.sync.q.is_finite()
            || !p.sync.level_dbfs.is_finite()
            || p.id.segments_present > 17
    }) {
        return Err(RigError::Invalid("non-finite or invalid detection"));
    }
    let mut reject_reasons = Vec::new();
    match cal.xtalk {
        CalibrationState::NotPerformed => {
            reject_reasons.push(RejectReason::CalibrationNotPerformed)
        }
        CalibrationState::Insufficient => {
            reject_reasons.push(RejectReason::CalibrationInsufficient)
        }
        _ => {}
    }
    if c.mode == AnalysisMode::Measurement && cal.z_basis.trim().is_empty() {
        reject_reasons.push(RejectReason::ZBasisMissing);
    }
    let (z, x) = if c.mode == AnalysisMode::Synthetic {
        (c.z, c.x)
    } else {
        (cal.z, cal.x)
    };
    let alignment = match align_left(c, left) {
        Ok(a) => {
            // 1e-8 ppm only absorbs floating-point roundoff at the inclusive boundary.
            if a.ppm.abs() > 100.0 + 1e-8 {
                reject_reasons.push(RejectReason::PpmOutOfRange);
            }
            a
        }
        Err(_) => {
            reject_reasons.push(RejectReason::AlignmentFailed);
            return Ok(Analysis {
                config: c.clone(),
                calibration: cal.clone(),
                alignment: Alignment {
                    offset: 0.0,
                    ratio: 1.0,
                    ppm: 0.0,
                    residual_rms: 0.0,
                    residual_max: 0.0,
                    anchors: 0,
                    u: 2.0,
                    anchor_times: Vec::new(),
                },
                markers: Vec::new(),
                r_duplicate: 0,
                r_orphan: 0,
                r_unassigned_undecodable: 0,
                warnings: vec!["E5_unchecked".into()],
                reject_reasons,
                e5_unchecked: true,
                z,
                x,
            });
        }
    };
    let (wlo, whi) = cal.window();
    let mut rows = Vec::new();
    for (id, start, _) in marker_schedule(&c.chirp)? {
        let predicted = alignment.offset + alignment.ratio * start as f64;
        let end = predicted + alignment.ratio * c.chirp.marker_len() as f64 + c.max_delay;
        let censored = predicted < 0.0 || end > frames as f64;
        let candidates: Vec<_> = left
            .iter()
            .filter(|p| (p.sync.time - predicted).abs() <= c.chirp.interval_len as f64 / 4.0)
            .collect();
        let mut confirmed: Vec<&Detection> = left
            .iter()
            .filter(|p| decoded_id(p.id.outcome) == Some(id))
            .collect();
        confirmed.sort_by(|a, b| b.sync.amplitude.total_cmp(&a.sync.amplitude));
        let mut unique: Vec<&Detection> = Vec::new();
        for p in confirmed {
            if !unique
                .iter()
                .any(|q| (p.sync.time - q.sync.time).abs() <= 2.0)
            {
                unique.push(p);
            }
        }
        let ambiguous = unique.iter().any(|p| {
            unique
                .iter()
                .any(|q| (p.sync.time - q.sync.time).abs() > alignment.u)
        });
        let selected = if ambiguous {
            None
        } else {
            unique.first().copied().or_else(|| {
                candidates
                    .iter()
                    .min_by(|a, b| {
                        (a.sync.time - predicted)
                            .abs()
                            .total_cmp(&(b.sync.time - predicted).abs())
                    })
                    .copied()
            })
        };
        let lc = if censored {
            LeftClass::Censored
        } else if ambiguous {
            LeftClass::Ambiguous
        } else {
            match selected.map(|p| p.id.outcome) {
                Some(i) if decoded_id(i) == Some(id) => LeftClass::Ok,
                Some(IdOutcome::Erased { .. }) => LeftClass::IdErased,
                Some(IdOutcome::Uncorrectable) => LeftClass::IdUndecodable,
                Some(_) => LeftClass::IdMismatch,
                None => LeftClass::Missing,
            }
        };
        rows.push(MarkerResult {
            id,
            predicted_left: predicted,
            left_uncertainty: alignment.uncertainty(start as f64),
            left_interval: {
                let u = alignment.uncertainty(start as f64);
                let actual = if lc == LeftClass::Ok {
                    selected.map_or(predicted, |p| p.sync.time)
                } else {
                    predicted
                };
                (actual.min(predicted) - u, actual.max(predicted) + u)
            },
            left_class: lc,
            left_extra: candidates.len().saturating_sub(1),
            left: selected.cloned(),
            class: None,
            right: None,
            near_candidates: selected.map_or_else(Vec::new, |l| {
                right
                    .iter()
                    .filter(|p| {
                        let d = p.sync.time - l.sync.time;
                        d >= wlo
                            && d <= whi
                            && (decoded_id(p.id.outcome) == Some(id)
                                || decoded_id(p.id.outcome).is_none())
                    })
                    .cloned()
                    .collect()
            }),
            delay: None,
            corrected_delay: None,
            reason: if lc == LeftClass::Ambiguous {
                "l_ambiguous".into()
            } else {
                String::new()
            },
        });
    }
    // Temporary A6: A3-confirmed ordinal and L_ok only; no dist_ok/source confidence yet.
    let mut xtalk_count = 0;
    let mut outside = false;
    for row in rows.iter().filter(|r| r.left_class == LeftClass::Ok) {
        if let Some(l) = &row.left {
            for p in right
                .iter()
                .filter(|p| decoded_id(p.id.outcome) == Some(row.id))
            {
                let d = p.sync.time - l.sync.time;
                if d > wlo - x && d <= whi + z + x {
                    xtalk_count += 1;
                    outside |= d < wlo || d > whi + z;
                }
            }
        }
    }
    let e5_unchecked = xtalk_count < c.xtalk_min_candidates;
    if !e5_unchecked && outside {
        reject_reasons.push(RejectReason::CalibrationMismatch);
    }
    if !reject_reasons.is_empty() {
        return Ok(Analysis {
            config: c.clone(),
            calibration: cal.clone(),
            alignment,
            markers: Vec::new(),
            r_duplicate: 0,
            r_orphan: 0,
            r_unassigned_undecodable: 0,
            warnings: if e5_unchecked {
                vec!["E5_unchecked".into()]
            } else {
                Vec::new()
            },
            reject_reasons,
            e5_unchecked,
            z,
            x,
        });
    }
    // Every R candidate has one disposition: normal, duplicate, near, orphan, or undecoded.
    let mut used = vec![false; right.len()];
    let mut duplicate = 0;
    for row in rows.iter_mut().filter(|r| r.left_class == LeftClass::Ok) {
        let l = row.left.as_ref().ok_or(ALIGN)?;
        let mut candidates: Vec<_> = right
            .iter()
            .enumerate()
            .filter(|(_, p)| {
                let d = p.sync.time - l.sync.time;
                decoded_id(p.id.outcome) == Some(row.id) && d > whi && d <= c.max_delay
            })
            .collect();
        candidates.sort_by(|a, b| a.1.sync.time.total_cmp(&b.1.sync.time));
        if let Some((_, p)) = candidates.first() {
            row.class = Some(Class::Normal);
            row.right = Some((*p).clone());
            let d = p.sync.time - l.sync.time;
            row.delay = Some(d);
            row.corrected_delay = Some(d - cal.channel_offset);
            duplicate += candidates.len().saturating_sub(1);
            for (i, _) in candidates {
                if let Some(v) = used.get_mut(i) {
                    *v = true;
                }
            }
        }
    }
    let delays: Vec<_> = rows.iter().filter_map(|r| r.delay).collect();
    let dhat = median(&delays);
    let mut unassigned = 0;
    for (i, p) in right
        .iter()
        .enumerate()
        .filter(|(_, p)| decoded_id(p.id.outcome).is_none())
    {
        if !rows
            .iter()
            .filter(|r| r.left_class == LeftClass::Ok)
            .any(|r| {
                r.left.as_ref().is_some_and(|l| {
                    let d = p.sync.time - l.sync.time;
                    d > whi && d <= c.max_delay
                })
            })
        {
            continue;
        }
        let targets: Vec<_> = rows
            .iter()
            .enumerate()
            .filter(|(_, r)| {
                if r.left_class != LeftClass::Ok {
                    return false;
                }
                let Some(l) = r.left.as_ref() else {
                    return false;
                };
                let d = p.sync.time - l.sync.time;
                dhat.is_some_and(|center| {
                    d > whi
                        && d <= c.max_delay
                        && (d - center).abs()
                            < (c.chirp.interval_len - c.chirp.marker_len()) as f64 / 2.0
                })
            })
            .map(|(j, _)| j)
            .collect();
        if targets.len() == 1
            && let Some(row) = targets.first().and_then(|j| rows.get_mut(*j))
            && row.class.is_none()
        {
            row.class = Some(Class::IdUndecodable);
            row.right = Some(p.clone());
            if let Some(v) = used.get_mut(i) {
                *v = true;
            }
        } else {
            unassigned += 1;
        }
    }
    let mut orphan = 0;
    let mut orphan_indices = Vec::new();
    for (i, p) in right.iter().enumerate() {
        if used.get(i) == Some(&true) || decoded_id(p.id.outcome).is_none() {
            continue;
        }
        let near = rows
            .iter()
            .filter(|r| r.left_class == LeftClass::Ok)
            .any(|r| {
                r.left.as_ref().is_some_and(|l| {
                    let d = p.sync.time - l.sync.time;
                    decoded_id(p.id.outcome) == Some(r.id) && d >= wlo && d <= whi
                })
            });
        if !near {
            orphan += 1;
            orphan_indices.push(i);
        }
    }
    let mut warnings = Vec::new();
    if e5_unchecked {
        warnings.push("E5_unchecked".into());
    }
    if dhat.is_none() {
        warnings.push("no_latency_reference".into());
    }
    if unassigned > 0 {
        warnings.push(format!("unassigned_undecodable={unassigned}"));
    }
    for row in rows
        .iter_mut()
        .filter(|r| r.left_class == LeftClass::Ok && r.class.is_none())
    {
        let l = row.left.as_ref().ok_or(ALIGN)?;
        let mismatch = orphan_indices
            .iter()
            .filter_map(|i| right.get(*i))
            .find(|p| {
                let d = p.sync.time - l.sync.time;
                decoded_id(p.id.outcome) != Some(row.id)
                    && d > whi
                    && d <= c.max_delay
                    && dhat.is_none_or(|v| (d - v).abs() <= c.chirp.interval_len as f64 / 2.0)
            });
        let near = right.iter().find(|p| {
            let d = p.sync.time - l.sync.time;
            d >= wlo
                && d <= whi
                && (decoded_id(p.id.outcome) == Some(row.id) || decoded_id(p.id.outcome).is_none())
        });
        if let Some(p) = mismatch {
            row.class = Some(Class::IdMismatch);
            row.right = Some(p.clone());
        } else if let Some(p) = near {
            row.class = Some(Class::Ambiguous);
            row.right = Some(p.clone());
            let ratio = 20.0 * (p.sync.amplitude / l.sync.amplitude).log10();
            row.reason = if cal
                .xtalk
                .as_ref()
                .is_some_and(|x| ratio >= x.ratio_min_db - 3.0 && ratio <= x.ratio_max_db + 3.0)
            {
                "xtalk_like"
            } else {
                "abnormal_near"
            }
            .into();
        } else if let Some(p) = right.iter().enumerate().find_map(|(i, p)| {
            let d = p.sync.time - l.sync.time;
            (used.get(i) == Some(&false)
                && decoded_id(p.id.outcome).is_none()
                && d > whi
                && d <= c.max_delay)
                .then_some(p)
        }) {
            row.class = Some(Class::Ambiguous);
            row.right = Some(p.clone());
            row.reason = "unassigned_undecodable".into();
        } else {
            row.class = Some(Class::Missing);
        }
    }
    Ok(Analysis {
        config: c.clone(),
        calibration: cal.clone(),
        alignment,
        markers: rows,
        r_duplicate: duplicate,
        r_orphan: orphan,
        r_unassigned_undecodable: unassigned,
        warnings,
        reject_reasons,
        e5_unchecked,
        z,
        x,
    })
}
pub fn analyze(
    c: &AnalysisConfig,
    calibration: Option<&Calibration>,
    rec: &StereoRecording,
) -> Result<Analysis, RigError> {
    if rec.left.len() != rec.right.len() {
        return Err(RigError::Invalid("channel length mismatch"));
    }
    let left = detect_channel(c, &rec.left)?;
    let right = detect_channel(c, &rec.right)?;
    let mut result = match_detections(c, calibration, rec.left.len(), &left, &right)?;
    if rec.truncated {
        result.warnings.push("truncated".into());
    }
    if rec.clipped_left > 0 || rec.clipped_right > 0 {
        result.warnings.push("clipping".into());
    }
    if rec.peak_left_dbfs.is_some_and(|v| v > -6.0) || rec.peak_right_dbfs.is_some_and(|v| v > -6.0)
    {
        result.warnings.push("peak_above_minus_6_dbfs".into());
    }
    Ok(result)
}
