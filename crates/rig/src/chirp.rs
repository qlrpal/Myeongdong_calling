use std::{
    fmt,
    io::{self, Write},
};

pub const SAMPLE_RATE_HZ: u32 = 48_000;

#[derive(Debug)]
pub enum RigError {
    Invalid(&'static str),
    Io(io::Error),
    Allocation(std::collections::TryReserveError),
}
impl fmt::Display for RigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Invalid(s) => write!(f, "invalid chirp configuration: {s}"),
            Self::Io(e) => e.fmt(f),
            Self::Allocation(e) => e.fmt(f),
        }
    }
}
impl std::error::Error for RigError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(e) => Some(e),
            Self::Allocation(e) => Some(e),
            _ => None,
        }
    }
}
impl From<io::Error> for RigError {
    fn from(e: io::Error) -> Self {
        Self::Io(e)
    }
}

/// All lengths are mono sample frames, including for stereo output.
#[derive(Clone, Debug)]
pub struct ChirpConfig {
    pub f_low_hz: f64,
    pub f_high_hz: f64,
    pub sync_len: usize,
    pub bit_len: usize,
    pub gap_len: usize,
    pub taper_fraction: f64,
    pub peak_dbfs: f64,
    pub interval_len: usize,
    pub marker_count: usize,
    pub lead_silence: usize,
    pub tail_silence: usize,
    pub channels: u16,
}
impl Default for ChirpConfig {
    fn default() -> Self {
        Self {
            f_low_hz: 500.0,
            f_high_hz: 4000.0,
            sync_len: 960,
            bit_len: 384,
            gap_len: 96,
            taper_fraction: 0.1,
            peak_dbfs: -12.0,
            interval_len: 24000,
            marker_count: 600,
            lead_silence: 48000,
            tail_silence: 48000,
            channels: 1,
        }
    }
}
const OVERFLOW: RigError = RigError::Invalid("length or RIFF size overflow");
impl ChirpConfig {
    /// Saturates on invalid overflowing configurations; validate before use.
    pub fn marker_len(&self) -> usize {
        self.sync_len
            .saturating_add(self.gap_len)
            .saturating_add(16usize.saturating_mul(self.bit_len.saturating_add(self.gap_len)))
    }
    fn checked_marker_len(&self) -> Result<usize, RigError> {
        self.bit_len
            .checked_add(self.gap_len)
            .and_then(|n| n.checked_mul(16))
            .and_then(|n| n.checked_add(self.sync_len))
            .and_then(|n| n.checked_add(self.gap_len))
            .ok_or(OVERFLOW)
    }
    pub fn total_len(&self) -> Result<usize, RigError> {
        self.marker_count
            .checked_sub(1)
            .and_then(|n| n.checked_mul(self.interval_len))
            .and_then(|n| n.checked_add(self.lead_silence))
            .and_then(|n| n.checked_add(self.tail_silence))
            .and_then(|n| {
                self.checked_marker_len()
                    .ok()
                    .and_then(|m| n.checked_add(m))
            })
            .ok_or(OVERFLOW)
    }
    fn data_bytes(&self) -> Result<u32, RigError> {
        let n = self
            .total_len()?
            .checked_mul(usize::from(self.channels))
            .and_then(|n| n.checked_mul(2))
            .ok_or(OVERFLOW)?;
        let n = u32::try_from(n).map_err(|_| OVERFLOW)?;
        n.checked_add(36).ok_or(OVERFLOW)?;
        Ok(n)
    }
    pub fn validate(&self) -> Result<(), RigError> {
        if !(self.f_low_hz.is_finite()
            && self.f_high_hz.is_finite()
            && 0.0 < self.f_low_hz
            && self.f_low_hz < self.f_high_hz
            && self.f_high_hz <= 0.45 * f64::from(SAMPLE_RATE_HZ))
        {
            return Err(RigError::Invalid("frequency range"));
        }
        if [
            self.sync_len,
            self.bit_len,
            self.gap_len,
            self.interval_len,
            self.lead_silence,
            self.tail_silence,
        ]
        .contains(&0)
        {
            return Err(RigError::Invalid("zero length"));
        }
        if !self.taper_fraction.is_finite() || !(0.0..=0.5).contains(&self.taper_fraction) {
            return Err(RigError::Invalid("taper"));
        }
        if !self.peak_dbfs.is_finite() || !(-60.0..=-1.0).contains(&self.peak_dbfs) {
            return Err(RigError::Invalid("peak"));
        }
        if !(1..=4096).contains(&self.marker_count) || ![1, 2].contains(&self.channels) {
            return Err(RigError::Invalid("count or channels"));
        }
        if self.interval_len
            < self
                .checked_marker_len()?
                .checked_add(self.gap_len)
                .ok_or(OVERFLOW)?
        {
            return Err(RigError::Invalid("interval"));
        }
        self.data_bytes()?;
        Ok(())
    }
}
pub fn encode_marker_id(index: u16) -> Option<u16> {
    if index >= 4096 {
        return None;
    }
    Some((index << 4) | ((index ^ (index >> 4) ^ (index >> 8)) & 15))
}
pub fn decode_marker_id(word: u16) -> Option<u16> {
    let index = word >> 4;
    (encode_marker_id(index) == Some(word)).then_some(index)
}
fn chirp(cfg: &ChirpConfig, len: usize, down: bool) -> Vec<f64> {
    if cfg.validate().is_err() {
        return Vec::new();
    }
    let (f0, f1) = if down {
        (cfg.f_high_hz, cfg.f_low_hz)
    } else {
        (cfg.f_low_hz, cfg.f_high_hz)
    };
    let fs = f64::from(SAMPLE_RATE_HZ);
    (0..len)
        .map(|n| {
            let t = n as f64 / fs;
            let edge = n.min(len.saturating_sub(1).saturating_sub(n)) as f64;
            let taper = cfg.taper_fraction * len.saturating_sub(1) as f64;
            let window = if taper > 0.0 && edge < taper {
                0.5 * (1.0 - (std::f64::consts::PI * edge / taper).cos())
            } else {
                1.0
            };
            (std::f64::consts::TAU * (f0 * t + (f1 - f0) * t * t / (2.0 * len as f64 / fs))).sin()
                * window
        })
        .collect()
}
/// Unit-amplitude windowed template. Invalid configurations return an empty vector.
pub fn sync_template(cfg: &ChirpConfig) -> Vec<f64> {
    chirp(cfg, cfg.sync_len, false)
}
/// false = up, true = down. Invalid configurations return an empty vector.
pub fn bit_template(cfg: &ChirpConfig, bit: bool) -> Vec<f64> {
    chirp(cfg, cfg.bit_len, bit)
}
fn zeros<T: Default + Clone>(len: usize) -> Result<Vec<T>, RigError> {
    let mut v = Vec::new();
    v.try_reserve_exact(len).map_err(RigError::Allocation)?;
    v.resize(len, T::default());
    Ok(v)
}
pub fn marker_template(cfg: &ChirpConfig, index: u16) -> Result<Vec<f64>, RigError> {
    cfg.validate()?;
    let word = encode_marker_id(index).ok_or(RigError::Invalid("marker ID"))?;
    let mut out = zeros(cfg.marker_len())?;
    for (dst, src) in out.iter_mut().zip(sync_template(cfg)) {
        *dst = src;
    }
    let mut start = cfg.sync_len.checked_add(cfg.gap_len).ok_or(OVERFLOW)?;
    for shift in (0..16).rev() {
        let end = start.checked_add(cfg.bit_len).ok_or(OVERFLOW)?;
        let dst = out.get_mut(start..end).ok_or(OVERFLOW)?;
        for (d, s) in dst
            .iter_mut()
            .zip(bit_template(cfg, word & (1 << shift) != 0))
        {
            *d = s;
        }
        start = end.checked_add(cfg.gap_len).ok_or(OVERFLOW)?;
    }
    Ok(out)
}
/// (ID, mono start frame, code word). Validates before computing positions.
pub fn marker_schedule(cfg: &ChirpConfig) -> Result<Vec<(u16, usize, u16)>, RigError> {
    cfg.validate()?;
    (0..cfg.marker_count)
        .map(|k| {
            let id = u16::try_from(k).map_err(|_| OVERFLOW)?;
            let start = k
                .checked_mul(cfg.interval_len)
                .and_then(|n| n.checked_add(cfg.lead_silence))
                .ok_or(OVERFLOW)?;
            Ok((id, start, encode_marker_id(id).ok_or(OVERFLOW)?))
        })
        .collect()
}
pub fn generate_samples(cfg: &ChirpConfig) -> Result<Vec<i16>, RigError> {
    cfg.validate()?;
    let mut out = zeros(cfg.total_len()?)?;
    let amplitude = 10f64.powf(cfg.peak_dbfs / 20.0) * 32767.0;
    for (id, start, _) in marker_schedule(cfg)? {
        let end = start.checked_add(cfg.marker_len()).ok_or(OVERFLOW)?;
        for (dst, src) in out
            .get_mut(start..end)
            .ok_or(OVERFLOW)?
            .iter_mut()
            .zip(marker_template(cfg, id)?)
        {
            *dst = (src * amplitude)
                .round()
                .clamp(f64::from(i16::MIN), f64::from(i16::MAX)) as i16;
        }
    }
    Ok(out)
}
pub fn write_wav<W: Write>(cfg: &ChirpConfig, mut w: W) -> Result<(), RigError> {
    cfg.validate()?;
    let size = cfg.data_bytes()?;
    let samples = generate_samples(cfg)?;
    w.write_all(b"RIFF")?;
    w.write_all(&size.checked_add(36).ok_or(OVERFLOW)?.to_le_bytes())?;
    w.write_all(b"WAVEfmt ")?;
    w.write_all(&16u32.to_le_bytes())?;
    w.write_all(&1u16.to_le_bytes())?;
    w.write_all(&cfg.channels.to_le_bytes())?;
    w.write_all(&SAMPLE_RATE_HZ.to_le_bytes())?;
    let align = cfg.channels.checked_mul(2).ok_or(OVERFLOW)?;
    w.write_all(
        &SAMPLE_RATE_HZ
            .checked_mul(u32::from(align))
            .ok_or(OVERFLOW)?
            .to_le_bytes(),
    )?;
    w.write_all(&align.to_le_bytes())?;
    w.write_all(&16u16.to_le_bytes())?;
    w.write_all(b"data")?;
    w.write_all(&size.to_le_bytes())?;
    for s in samples {
        for _ in 0..cfg.channels {
            w.write_all(&s.to_le_bytes())?;
        }
    }
    Ok(())
}
