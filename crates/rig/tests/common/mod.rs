#![allow(dead_code)]
use mdc_rig::analyze::AnalysisConfig;
use mdc_rig::calib::Calibration;
use mdc_rig::*;
pub fn config(n: usize) -> AnalysisConfig {
    AnalysisConfig {
        chirp: ChirpConfig {
            marker_count: n,
            lead_silence: 4800,
            tail_silence: 60000,
            ..Default::default()
        },
        ..Default::default()
    }
}
pub fn signal(c: &AnalysisConfig) -> Vec<f32> {
    generate_samples(&c.chirp)
        .unwrap()
        .into_iter()
        .map(|v| f32::from(v) / 32768.0)
        .collect()
}
pub fn calibration() -> Calibration {
    Calibration {
        channel_offset: 0.0,
        xtalk: None,
        source_files: "synthetic".into(),
    }
}
pub fn shifted(x: &[f32], delay: usize, gain: f32) -> Vec<f32> {
    let mut y = vec![0.0; x.len() + delay];
    for (i, v) in x.iter().enumerate() {
        y[i + delay] = v * gain;
    }
    y
}
pub fn recording(mut l: Vec<f32>, mut r: Vec<f32>) -> StereoRecording {
    let n = l.len().max(r.len());
    l.resize(n, 0.0);
    r.resize(n, 0.0);
    let peak = |x: &[f32]| {
        let p = x.iter().map(|v| f64::from(*v).abs()).fold(0.0, f64::max);
        (p > 0.0).then(|| 20.0 * p.log10())
    };
    let pl = peak(&l);
    let pr = peak(&r);
    StereoRecording {
        left: l,
        right: r,
        truncated: false,
        clipped_left: 0,
        clipped_right: 0,
        peak_left_dbfs: pl,
        peak_right_dbfs: pr,
    }
}
pub fn start(c: &AnalysisConfig, id: usize) -> usize {
    c.chirp.lead_silence + id * c.chirp.interval_len
}
pub fn erase_marker(c: &AnalysisConfig, x: &mut [f32], id: usize, delay: usize) {
    let t = start(c, id) + delay;
    x[t..t + c.chirp.marker_len()].fill(0.0);
}
pub fn replace_bits(
    c: &AnalysisConfig,
    x: &mut [f32],
    id: usize,
    delay: usize,
    bits: &[usize],
    silent: bool,
) {
    let word = encode_marker_id(id as u16).unwrap();
    for &bit in bits {
        let t = start(c, id)
            + delay
            + c.chirp.sync_len
            + c.chirp.gap_len
            + (15 - bit) * (c.chirp.bit_len + c.chirp.gap_len);
        for (v, t) in x[t..t + c.chirp.bit_len]
            .iter_mut()
            .zip(bit_template(&c.chirp, word & (1 << bit) == 0))
        {
            *v = if silent {
                0.0
            } else {
                (t * 10f64.powf(c.chirp.peak_dbfs / 20.0)) as f32
            };
        }
    }
}
// Independent windowed-sinc generator, y[n] = x[(n-delay)/ratio].
// Quiet 480-sample blocks skip work, with adjacent blocks included for kernel support.
pub fn resample(x: &[f32], delay: f64, ratio: f64) -> Vec<f32> {
    let active: Vec<_> = x.chunks(480).map(|b| b.iter().any(|v| *v != 0.0)).collect();
    let mut y = vec![0.0; (x.len() as f64 * ratio + delay).ceil() as usize + 64];
    for (n, v) in y.iter_mut().enumerate() {
        let t = (n as f64 - delay) / ratio;
        let base = t.floor() as isize;
        if base < -32 || base >= x.len() as isize + 32 {
            continue;
        }
        let block = (base.max(0) as usize) / 480;
        if !(block.saturating_sub(1)..=block + 1).any(|b| active.get(b) == Some(&true)) {
            continue;
        }
        let mut sum = 0.0;
        let mut norm = 0.0;
        for k in -31..=32 {
            let i = base + k;
            let d = t - i as f64;
            let sinc = if d.abs() < 1e-12 {
                1.0
            } else {
                (std::f64::consts::PI * d).sin() / (std::f64::consts::PI * d)
            };
            let w = sinc
                * (0.42
                    + 0.5 * (std::f64::consts::PI * d / 32.0).cos()
                    + 0.08 * (std::f64::consts::TAU * d / 32.0).cos());
            norm += w;
            if i >= 0 {
                sum += f64::from(x.get(i as usize).copied().unwrap_or(0.0)) * w;
            }
        }
        *v = (sum / norm) as f32;
    }
    y
}
pub fn wav(rec: &StereoRecording) -> Vec<u8> {
    let size = (rec.left.len() * 4) as u32;
    let mut b = b"RIFF".to_vec();
    b.extend((size + 36).to_le_bytes());
    b.extend(b"WAVEfmt ");
    b.extend(16u32.to_le_bytes());
    b.extend(1u16.to_le_bytes());
    b.extend(2u16.to_le_bytes());
    b.extend(48000u32.to_le_bytes());
    b.extend(192000u32.to_le_bytes());
    b.extend(4u16.to_le_bytes());
    b.extend(16u16.to_le_bytes());
    b.extend(b"data");
    b.extend(size.to_le_bytes());
    for (l, r) in rec.left.iter().zip(&rec.right) {
        for v in [l, r] {
            b.extend(((v * 32768.0).round().clamp(-32768.0, 32767.0) as i16).to_le_bytes());
        }
    }
    b
}
pub struct Temp(pub std::path::PathBuf);
impl Temp {
    pub fn new(name: &str) -> Self {
        let path = std::env::temp_dir().join(format!(
            "mdc-rig-{}-{}-{}",
            std::process::id(),
            name,
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}
