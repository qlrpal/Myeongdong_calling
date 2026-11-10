//! Offline channel alignment and conservative marker matching. Times are ADC samples.
const T_P: u32 = 3;
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
    pub source_max: f64,
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
            source_max: 96000.0,
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
        if !self.source_max.is_finite()
            || self.source_max <= 0.0
            || self.source_max < self.max_delay
            || !self.z.is_finite()
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
            "{:?};{:?};max_delay={:?};overlap=marker_len;alignment=exact_ls;anchor_residual_limit=interval/4;l_search=interval/4;source_matching=interval_A1;xtalk_margin_samples=2;xtalk_ratio_margin_db=3;normal_selection=earliest_unresolved;source_max={:?};T_p=3;mode={:?};Z={:?};X={:?};N={};no_detection={:?}",
            self.chirp,
            self.detector,
            self.max_delay,
            self.source_max,
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
    /// 7 denotes both step 7 and 7a (distinguished by reason).
    pub decision_step: u8,
    pub c0: Option<Detection>,
    pub possible_sources: Vec<u16>,
    pub distance_violations: Vec<(u16, u16)>,
    pub later_confirmed: bool,
}
#[derive(Debug)]
pub struct Analysis {
    pub config: AnalysisConfig,
    pub calibration: Calibration,
    pub alignment: Alignment,
    pub markers: Vec<MarkerResult>,
    pub r_duplicate: usize,
    pub r_shared: usize,
    pub blocked_self_id: usize,
    pub blocked_other: usize,
    pub blocked_earlier: usize,
    pub blocked_boundary: usize,
    pub outside_ids: Vec<(f64, u16)>,
    pub e1_suspect: usize,
    pub xtalk_candidates: usize,
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
                r_shared: 0,
                blocked_self_id: 0,
                blocked_other: 0,
                blocked_earlier: 0,
                blocked_boundary: 0,
                outside_ids: Vec::new(),
                e1_suspect: 0,
                xtalk_candidates: 0,
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
            decision_step: if lc == LeftClass::Ambiguous { 2 } else { 1 },
            c0: None,
            possible_sources: Vec::new(),
            distance_violations: Vec::new(),
            later_confirmed: false,
            reason: if lc == LeftClass::Ambiguous {
                "l_ambiguous".into()
            } else {
                String::new()
            },
        });
    }
    let words = rows
        .iter()
        .map(|r| {
            crate::codeword_of_marker(r.id)
                .and_then(crate::encode_marker_id)
                .ok_or(RigError::Invalid("marker codeword"))
        })
        .collect::<Result<Vec<_>, _>>()?;
    let interval = |p: &Detection, r: &MarkerResult| {
        (
            p.sync.time - r.left_interval.1,
            p.sync.time - r.left_interval.0,
        )
    };
    let overlaps = |d: (f64, f64), lo: f64, hi: f64| d.1 > lo && d.0 <= hi;
    let violations = |set: &[usize]| {
        let mut bad = Vec::new();
        for (i, &a) in set.iter().enumerate() {
            for &b in set.iter().skip(i + 1) {
                if let (Some(wa), Some(wb), Some(ra), Some(rb)) =
                    (words.get(a), words.get(b), rows.get(a), rows.get(b))
                    && (wa ^ wb).count_ones() < 8
                {
                    bad.push((ra.id, rb.id));
                }
            }
        }
        bad
    };
    struct Candidate<'a> {
        p: &'a Detection,
        xtalk: Option<u16>,
        explained: Option<u16>,
        possible: Vec<u16>,
        bad: Vec<(u16, u16)>,
    }
    let mut sorted = right.iter().collect::<Vec<_>>();
    sorted.sort_by(|a, b| a.sync.time.total_cmp(&b.sync.time));
    let mut candidates = Vec::new();
    let mut outside_ids = Vec::new();
    let mut e1_suspect = 0;
    let mut outside = false;
    let mut xtalk_count = 0;
    for p in sorted {
        let mx = rows
            .iter()
            .enumerate()
            .filter(|(_, r)| overlaps(interval(p, r), wlo - x, whi + z + x))
            .map(|(i, _)| i)
            .collect::<Vec<_>>();
        let mc = rows
            .iter()
            .enumerate()
            .filter(|(_, r)| overlaps(interval(p, r), whi + z, c.source_max))
            .map(|(i, _)| i)
            .collect::<Vec<_>>();
        let confirmed_word = match p.id.outcome {
            IdOutcome::Exact(n)
            | IdOutcome::Corrected { index: n, .. }
            | IdOutcome::ErasureCorrected { index: n, .. } => Some(n),
            _ => None,
        };
        let confirmed = confirmed_word
            .and_then(marker_of_codeword)
            .filter(|id| usize::from(*id) < rows.len());
        if let Some(n) = confirmed_word
            && confirmed.is_none()
        {
            outside_ids.push((p.sync.time, n));
        }
        if let Some(id) = confirmed
            && !mc.contains(&usize::from(id))
            && !mx.contains(&usize::from(id))
        {
            e1_suspect += 1;
        }
        let xtalk =
            confirmed.filter(|id| mx.contains(&usize::from(*id)) && violations(&mx).is_empty());
        if let Some(id) = xtalk {
            xtalk_count += 1;
            if let Some(r) = rows.get(usize::from(id)) {
                let t = if r.left_class == LeftClass::Ok {
                    r.left.as_ref().map_or(r.predicted_left, |l| l.sync.time)
                } else {
                    r.predicted_left
                };
                let d = p.sync.time - t;
                outside |= d < wlo || d > whi + z;
            }
        }
        let bad = violations(&mc);
        let possible = mc
            .iter()
            .filter_map(|i| {
                let word = words.get(*i)?;
                (crate::detect::nonerased_distance(p.id.bits, p.id.erasure_mask, *word) <= T_P)
                    .then(|| rows.get(*i).map(|r| r.id))
                    .flatten()
            })
            .collect::<Vec<_>>();
        let explained = confirmed
            .filter(|id| xtalk.is_none() && bad.is_empty() && possible.as_slice() == [*id]);
        candidates.push(Candidate {
            p,
            xtalk,
            explained,
            possible,
            bad,
        });
    }
    let e5_unchecked = xtalk_count < c.xtalk_min_candidates;
    if !e5_unchecked && outside {
        reject_reasons.push(RejectReason::CalibrationMismatch);
    }
    let mut warnings = Vec::new();
    if e5_unchecked {
        warnings.push("E5_unchecked".into());
    }
    if e1_suspect > 0 {
        warnings.push("E1_suspect".into());
    }
    let mut shared = vec![0usize; candidates.len()];
    let mut duplicate = 0;
    let (mut blocked_self_id, mut blocked_other, mut blocked_earlier, mut blocked_boundary) =
        (0, 0, 0, 0);
    if reject_reasons.is_empty() {
        for row in rows.iter_mut().filter(|r| r.left_class == LeftClass::Ok) {
            let rk = candidates
                .iter()
                .enumerate()
                .filter(|(_, q)| overlaps(interval(q.p, row), wlo, c.source_max))
                .collect::<Vec<_>>();
            row.decision_step = 3;
            row.class = Some(Class::Missing);
            row.reason = "no_candidate".into();
            if rk.is_empty() {
                continue;
            }
            let unresolved = rk
                .iter()
                .copied()
                .filter(|(_, q)| q.xtalk.is_none() && q.explained.is_none_or(|id| id == row.id))
                .collect::<Vec<_>>();
            for (i, _) in &unresolved {
                if let Some(n) = shared.get_mut(*i) {
                    *n += 1;
                }
            }
            row.decision_step = 4;
            row.reason = "explained_elsewhere".into();
            if unresolved.is_empty() {
                if rk.iter().any(|(_, q)| q.xtalk == Some(row.id)) {
                    row.class = Some(Class::Ambiguous);
                    row.reason = "xtalk_like".into();
                }
                continue;
            }
            // Step 4a uses decision_step 4; the reason distinguishes this boundary hold.
            if let Some((_, q)) = rk
                .iter()
                .find(|(_, q)| q.xtalk == Some(row.id) && interval(q.p, row).1 > whi + z)
            {
                row.class = Some(Class::Ambiguous);
                row.reason = "xtalk_boundary".into();
                row.c0 = Some(q.p.clone());
                row.right = Some(q.p.clone());
                blocked_boundary += 1;
                continue;
            }
            let (_, q) = unresolved.first().copied().ok_or(ALIGN)?;
            row.c0 = Some(q.p.clone());
            row.right = Some(q.p.clone());
            row.possible_sources = q.possible.clone();
            row.distance_violations = q.bad.clone();
            row.later_confirmed = rk.iter().any(|(_, later)| {
                let d = interval(later.p, row);
                later.p.sync.time > q.p.sync.time
                    && later.explained == Some(row.id)
                    && d.0 > whi + z
                    && d.1 <= c.max_delay
            });
            let d = interval(q.p, row);
            row.class = Some(Class::Ambiguous);
            if unresolved
                .iter()
                .skip(1)
                .any(|(_, p)| p.p.sync.time - q.p.sync.time <= 2.0)
            {
                row.decision_step = 5;
                row.reason = "tie".into();
            } else if d.1 >= wlo && d.0 <= whi + z {
                row.decision_step = 6;
                row.reason = if d.0 >= wlo && d.1 <= whi + z {
                    let ratio = row
                        .left
                        .as_ref()
                        .map(|l| 20.0 * (q.p.sync.amplitude / l.sync.amplitude).log10());
                    if ratio.is_some_and(|ratio| {
                        cal.xtalk.as_ref().is_some_and(|x| {
                            ratio >= x.ratio_min_db - 3.0 && ratio <= x.ratio_max_db + 3.0
                        })
                    }) {
                        "xtalk_like"
                    } else {
                        "abnormal_near"
                    }
                } else {
                    "boundary"
                }
                .into();
            } else if !q.bad.is_empty() {
                row.decision_step = 7;
                row.reason = "source_set_distance".into();
                blocked_self_id += 1;
            } else if q.explained != Some(row.id) {
                row.decision_step = 7;
                row.reason = if q.possible.contains(&row.id) {
                    blocked_self_id += 1;
                    if q.possible.len() == 1 {
                        row.class = Some(Class::IdUndecodable);
                        "id_undecodable"
                    } else {
                        "correspondence"
                    }
                } else {
                    "unassigned_candidate"
                }
                .into();
            } else if d.0 <= whi + z || d.1 > c.max_delay {
                row.decision_step = 8;
                row.reason = if d.1 > c.max_delay {
                    "out_of_support"
                } else {
                    "boundary"
                }
                .into();
            } else {
                row.decision_step = 9;
                row.class = Some(Class::Normal);
                row.reason.clear();
                let delay = q.p.sync.time - row.left.as_ref().ok_or(ALIGN)?.sync.time;
                row.delay = Some(delay);
                row.corrected_delay = Some(delay - cal.channel_offset);
                duplicate += rk
                    .iter()
                    .filter(|(_, p)| p.p.sync.time > q.p.sync.time && p.explained == Some(row.id))
                    .count();
            }
            if matches!(
                row.reason.as_str(),
                "xtalk_like" | "abnormal_near" | "unassigned_candidate"
            ) {
                blocked_other += 1;
            }
            if matches!(row.reason.as_str(), "boundary" | "out_of_support") {
                blocked_boundary += 1;
            }
            if (5..=7).contains(&row.decision_step) && row.later_confirmed {
                blocked_earlier += 1;
            }
        }
    } else {
        rows.clear();
    }
    Ok(Analysis {
        config: c.clone(),
        calibration: cal.clone(),
        alignment,
        markers: rows,
        r_duplicate: duplicate,
        r_shared: shared.iter().filter(|n| **n > 1).count(),
        blocked_self_id,
        blocked_other,
        blocked_earlier,
        blocked_boundary,
        outside_ids,
        e1_suspect,
        xtalk_candidates: xtalk_count,
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
