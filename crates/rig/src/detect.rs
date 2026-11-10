use crate::fft::correlation_blocks;
use crate::{
    ChirpConfig, Complex, MarkerIdDecode, RigError, bit_template, bit_template_quadrature,
    decode_marker_id, sync_template, sync_template_quadrature,
};

#[derive(Clone, Debug)]
pub struct DetectorConfig {
    pub q_min: f64,
    pub abs_floor_dbfs: f64,
    pub snr_min_db: f64,
    pub dedup_radius: usize,
    pub bit_search: usize,
    pub bit_q_min: f64,
    /// Require quality and amplitude to pass at the same template and start.
    pub joint_bit_gate: bool,
    pub bit_margin_db: f64,
}
impl Default for DetectorConfig {
    fn default() -> Self {
        Self {
            q_min: 0.4,
            abs_floor_dbfs: -60.0,
            snr_min_db: 10.0,
            dedup_radius: 48,
            bit_search: 2,
            bit_q_min: 0.5,
            joint_bit_gate: true,
            bit_margin_db: 6.0,
        }
    }
}
impl DetectorConfig {
    pub fn validate(&self) -> Result<(), RigError> {
        if !self.q_min.is_finite()
            || !(0.0..=1.0).contains(&self.q_min)
            || !self.bit_q_min.is_finite()
            || !(0.0..=1.0).contains(&self.bit_q_min)
            || !self.abs_floor_dbfs.is_finite()
            || !(-300.0..=0.0).contains(&self.abs_floor_dbfs)
            || !self.snr_min_db.is_finite()
            || !(0.0..=300.0).contains(&self.snr_min_db)
            || !self.bit_margin_db.is_finite()
            || !(0.0..=300.0).contains(&self.bit_margin_db)
            || self.dedup_radius == 0
            || self.dedup_radius > isize::MAX as usize
            || self.bit_search > isize::MAX as usize
        {
            return Err(RigError::Invalid("detector configuration"));
        }
        Ok(())
    }
}
/// Tenth percentile (nearest rank) of complete 480-sample block RMS values.
/// Non-finite blocks are excluded; sync detection rejects non-finite input.
pub fn noise_rms(x: &[f32]) -> f64 {
    let mut blocks: Vec<f64> = x
        .chunks_exact(480)
        .filter_map(|b| {
            let e = b.iter().map(|v| f64::from(*v).powi(2)).sum::<f64>();
            if e.is_finite() {
                Some((e / 480.0).sqrt())
            } else {
                None
            }
        })
        .collect();
    blocks.sort_unstable_by(f64::total_cmp);
    let rank = blocks.len().div_ceil(10);
    rank.checked_sub(1)
        .and_then(|n| blocks.get(n))
        .copied()
        .unwrap_or(0.0)
}
/// Computes the recording-wide amplitude gate once for reuse by ID readers.
pub fn amplitude_gate(det: &DetectorConfig, x: &[f32]) -> f64 {
    let noise = noise_rms(x);
    10f64
        .powf(det.abs_floor_dbfs / 20.0)
        .max(noise * 10f64.powf(det.snr_min_db / 20.0))
}
fn analytic(s: Vec<f64>, h: Vec<f64>) -> Vec<Complex> {
    s.into_iter()
        .zip(h)
        .map(|(re, im)| Complex { re, im })
        .collect()
}
fn template_energy(t: &[Complex]) -> f64 {
    t.iter().map(|z| z.re * z.re).sum()
}
fn quality(e: f64, ts: f64, xs: f64) -> f64 {
    if ts <= 1e-20 || xs <= 1e-20 {
        0.0
    } else {
        e / (ts * xs).sqrt()
    }
}
/// Tests paired (quality, amplitude) observations. In joint mode each pair must
/// describe one template at one start; split mode permits separate witnesses.
/// Window validity and positive peak correlation are checked by the caller.
pub fn bit_gate_exists(
    observations: &[(f64, f64)],
    bit_q_min: f64,
    gate: f64,
    joint: bool,
) -> bool {
    if joint {
        observations
            .iter()
            .any(|&(q, a)| q >= bit_q_min && a >= gate)
    } else {
        observations.iter().any(|&(q, _)| q >= bit_q_min)
            && observations.iter().any(|&(_, a)| a >= gate)
    }
}
#[derive(Clone, Debug)]
pub struct SyncCandidate {
    /// Another retained candidate lies within +/- marker_len in this channel.
    pub overlap: bool,
    pub peak_index: usize,
    pub time: f64,
    pub q: f64,
    pub amplitude: f64,
    pub level_dbfs: f64,
    pub polarity: i8,
}
const BAD: RigError = RigError::Invalid("detector length or input");
#[derive(Clone, Copy)]
struct Point {
    index: usize,
    c: Complex,
    energy: f64,
}
/// Blockwise complex-envelope peaks, with bounded three-point parabolic timing.
pub fn detect_sync(
    cfg: &ChirpConfig,
    det: &DetectorConfig,
    x: &[f32],
) -> Result<Vec<SyncCandidate>, RigError> {
    cfg.validate()?;
    det.validate()?;
    if x.iter().any(|v| !v.is_finite()) {
        return Err(BAD);
    }
    let t = analytic(sync_template(cfg), sync_template_quadrature(cfg));
    let ts = template_energy(&t);
    if ts <= 1e-20 {
        return Err(BAD);
    }
    let threshold = amplitude_gate(det, x);
    let mut out: Vec<SyncCandidate> = Vec::new();
    let mut previous: Option<Point> = None;
    let mut before = 0.0;
    let mut energy = x
        .get(..t.len())
        .map(|s| s.iter().map(|v| f64::from(*v).powi(2)).sum::<f64>())
        .unwrap_or(0.0);
    let mut pending: Vec<SyncCandidate> = Vec::new();
    let mut consider = |p: Point, left: f64, right: f64| -> Result<(), RigError> {
        let e = p.c.magnitude();
        let q = quality(e, ts, p.energy);
        let amplitude = e / ts;
        if e > left && e >= right && q >= det.q_min && amplitude >= threshold && amplitude > 0.0 {
            let denom = left - 2.0 * e + right;
            let delta = if denom.abs() > 1e-30 {
                (0.5 * (left - right) / denom).clamp(-0.5, 0.5)
            } else {
                0.0
            };
            pending.push(SyncCandidate {
                overlap: false,
                peak_index: p.index,
                time: p.index as f64 + delta,
                q,
                amplitude,
                level_dbfs: 20.0 * amplitude.log10(),
                polarity: if p.c.re < 0.0 { -1 } else { 1 },
            });
        }
        Ok(())
    };
    correlation_blocks(
        |start, block| {
            for (offset, c) in block.iter().enumerate() {
                let index = start.checked_add(offset).ok_or(BAD)?;
                if index > 0 {
                    let old = index.checked_sub(1).ok_or(BAD)?;
                    let entering = old.checked_add(t.len()).ok_or(BAD)?;
                    energy = (energy - f64::from(*x.get(old).ok_or(BAD)?).powi(2)
                        + f64::from(*x.get(entering).ok_or(BAD)?).powi(2))
                    .max(0.0);
                }
                let point = Point {
                    index,
                    c: *c,
                    energy,
                };
                if let Some(p) = previous {
                    consider(p, before, c.magnitude())?;
                    before = p.c.magnitude();
                }
                previous = Some(point);
            }
            Ok(())
        },
        x,
        &t,
    )?;
    if let Some(p) = previous {
        consider(p, before, 0.0)?;
    }
    // Greedy strongest-first suppression retains paths beyond the radius.
    pending.sort_unstable_by(|a, b| {
        b.amplitude
            .total_cmp(&a.amplitude)
            .then(a.peak_index.cmp(&b.peak_index))
    });
    for candidate in pending {
        if !out
            .iter()
            .any(|p| p.peak_index.abs_diff(candidate.peak_index) <= det.dedup_radius)
        {
            out.push(candidate);
        }
    }
    out.sort_unstable_by(|a, b| a.time.total_cmp(&b.time));
    let times: Vec<_> = out.iter().map(|p| p.time).collect();
    for (i, p) in out.iter_mut().enumerate() {
        p.overlap = times
            .iter()
            .enumerate()
            .any(|(j, t)| i != j && (*t - p.time).abs() <= cfg.marker_len() as f64);
    }
    Ok(out)
}
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum IdOutcome {
    Exact(u16),
    Corrected { index: u16, flipped_bit: u8 },
    ErasureCorrected { index: u16, erased_bit: u8 },
    Uncorrectable,
    Erased { count: u8 },
}
#[derive(Debug, Clone)]
pub struct IdReading {
    pub outcome: IdOutcome,
    pub word: Option<u16>,
    /// MSB-first slot decisions; erased positions are zero.
    pub bits: u16,
    /// One at each erased word bit (slot k corresponds to bit 15-k).
    pub erasure_mask: u16,
    pub segments_present: u8,
    pub min_margin_db: Option<f64>,
}
/// Hamming distance restricted to positions that were not erased.
pub fn nonerased_distance(bits: u16, erasure_mask: u16, codeword_word: u16) -> u32 {
    ((bits ^ codeword_word) & !erasure_mask).count_ones()
}

/// A3: SECDED without erasures; one erasure permits no nonerased errors.
/// Results contain codeword indices, not marker ordinals.
pub fn decode_id(bits: u16, erasure_mask: u16) -> IdOutcome {
    let count = erasure_mask.count_ones() as u8;
    if count >= 2 {
        return IdOutcome::Erased { count };
    }
    if count == 1 {
        let known = bits & !erasure_mask;
        for word in [known, known | erasure_mask] {
            if let MarkerIdDecode::Exact(index) = decode_marker_id(word) {
                return IdOutcome::ErasureCorrected {
                    index,
                    erased_bit: erasure_mask.trailing_zeros() as u8,
                };
            }
        }
        return IdOutcome::Erased { count };
    }
    match decode_marker_id(bits) {
        MarkerIdDecode::Exact(index) => IdOutcome::Exact(index),
        MarkerIdDecode::Corrected { index, flipped_bit } => {
            IdOutcome::Corrected { index, flipped_bit }
        }
        MarkerIdDecode::Uncorrectable => IdOutcome::Uncorrectable,
    }
}

fn erased() -> IdReading {
    IdReading {
        outcome: IdOutcome::Erased { count: 16 },
        word: None,
        bits: 0,
        erasure_mask: u16::MAX,
        segments_present: 1,
        min_margin_db: None,
    }
}
fn dot(x: &[f32], t: &[Complex]) -> (f64, f64) {
    let mut c = Complex::default();
    let mut energy = 0.0;
    for (v, z) in x.iter().zip(t) {
        let v = f64::from(*v);
        c.re += v * z.re;
        c.im -= v * z.im;
        energy += v * v;
    }
    (c.magnitude(), energy)
}
/// Reads MSB-first bits using a precomputed recording amplitude gate.
/// Only the bit search windows are inspected; non-finite windows are erased.
/// One erased bit uses A3 exact punctured decoding; two or more are rejected.
pub fn read_id(
    cfg: &ChirpConfig,
    det: &DetectorConfig,
    x: &[f32],
    gate: f64,
    sync: &SyncCandidate,
) -> IdReading {
    if cfg.validate().is_err()
        || det.validate().is_err()
        || !sync.time.is_finite()
        || sync.time < 0.0
        || sync.time >= usize::MAX as f64
        || !gate.is_finite()
        || gate < 0.0
    {
        return erased();
    }
    let templates = [
        analytic(
            bit_template(cfg, false),
            bit_template_quadrature(cfg, false),
        ),
        analytic(bit_template(cfg, true), bit_template_quadrature(cfg, true)),
    ];
    let threshold = gate;
    let mut word = 0u16;
    let mut erasure_mask = 0u16;
    let mut present = 1u8;
    let mut min_margin: Option<f64> = None;
    for k in 0usize..16 {
        let offset = cfg
            .bit_len
            .checked_add(cfg.gap_len)
            .and_then(|v| v.checked_mul(k))
            .and_then(|v| v.checked_add(cfg.sync_len))
            .and_then(|v| v.checked_add(cfg.gap_len));
        let center = offset.map(|o| sync.time + o as f64);
        let mut peaks = [0.0f64; 2];
        let mut qs = [0.0f64; 2];
        let mut amplitudes = [0.0f64; 2];
        let mut joint_exists = false;
        let valid = (|| -> Option<()> {
            let center = center?;
            if !center.is_finite() || center < 0.0 || center >= usize::MAX as f64 {
                return None;
            }
            let center = center.round() as usize;
            // A clipped nominal segment is erased even if a shifted window fits.
            x.get(center..center.checked_add(cfg.bit_len)?)?;
            let lo = center.saturating_sub(det.bit_search);
            let hi = center
                .checked_add(det.bit_search)?
                .min(x.len().checked_sub(cfg.bit_len)?);
            for ((t, e), (q, a)) in templates
                .iter()
                .zip(peaks.iter_mut())
                .zip(qs.iter_mut().zip(amplitudes.iter_mut()))
            {
                let ts = template_energy(t);
                for start in lo..=hi {
                    let segment = x.get(start..start.checked_add(cfg.bit_len)?)?;
                    if segment.iter().any(|v| !v.is_finite()) {
                        return None;
                    }
                    let (v, xs) = dot(segment, t);
                    joint_exists |= bit_gate_exists(
                        &[(quality(v, ts, xs), if ts > 1e-20 { v / ts } else { 0.0 })],
                        det.bit_q_min,
                        threshold,
                        true,
                    );
                    *q = q.max(quality(v, ts, xs));
                    if v > *e {
                        *e = v;
                        *a = if ts > 1e-20 { v / ts } else { 0.0 };
                    }
                }
            }
            Some(())
        })()
        .is_some();
        let emax = peaks.iter().copied().fold(0.0, f64::max);
        let emin = peaks.iter().copied().fold(f64::MAX, f64::min);
        // A finite ceiling represents a zero competing correlation (no NaN/inf).
        let margin = if emax > 0.0 {
            20.0 * (emax / emin.max(emax * 1e-15)).log10()
        } else {
            0.0
        };
        let exists = valid
            && if det.joint_bit_gate {
                joint_exists
            } else {
                // Preserve the original summaries and comparisons exactly.
                qs.iter().any(|q| *q >= det.bit_q_min) && amplitudes.iter().any(|a| *a >= threshold)
            }
            && emax > 0.0;
        if exists {
            present = match present.checked_add(1) {
                Some(v) => v,
                None => return erased(),
            };
            min_margin = Some(min_margin.map_or(margin, |v| v.min(margin)));
        }
        if !exists || margin < det.bit_margin_db {
            let Some(mask) = 15usize
                .checked_sub(k)
                .and_then(|shift| 1u16.checked_shl(shift as u32))
            else {
                return erased();
            };
            erasure_mask |= mask;
        } else if peaks.get(1) > peaks.first()
            && let Some(shift) = 15usize.checked_sub(k).and_then(|v| u32::try_from(v).ok())
        {
            word |= 1u16.checked_shl(shift).unwrap_or(0);
        }
    }
    let outcome = decode_id(word, erasure_mask);
    IdReading {
        outcome,
        word: if erasure_mask == 0 { Some(word) } else { None },
        bits: word,
        erasure_mask,
        segments_present: present,
        min_margin_db: min_margin,
    }
}
