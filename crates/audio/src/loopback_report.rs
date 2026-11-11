//! Platform-independent loopback logic and control-thread reports.
use crate::stream_report::{Distribution, distribution};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SampleFault {
    None,
    NonFinite,
    Clamped,
}

/// Control-thread gain validation.
pub fn gain_linear(db: f32) -> Option<f32> {
    if !db.is_finite() || !(-60.0..=0.0).contains(&db) {
        return None;
    }
    Some(10.0_f32.powf(db / 20.0))
}

/// Realtime-safe protection. Nonfinite sample or gain is silenced;
/// infinite products from finite operands are limited like other overloads.
pub fn protect(sample: f32, gain: f32) -> (f32, SampleFault) {
    if !sample.is_finite() || !gain.is_finite() {
        return (0.0, SampleFault::NonFinite);
    }
    let scaled = sample * gain;
    let limited = scaled.clamp(-1.0, 1.0);
    let fault = if scaled == limited {
        SampleFault::None
    } else {
        SampleFault::Clamped
    };
    (limited, fault)
}

pub fn render_frames(buffer_frames: u32, padding: u32, period_frames: u32) -> Option<u32> {
    if period_frames == 0 {
        return None;
    }
    Some(buffer_frames.checked_sub(padding)?.min(period_frames))
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Priming {
    threshold: u32,
    done: bool,
}
impl Priming {
    pub fn new(threshold: u32) -> Self {
        Self {
            threshold,
            done: false,
        }
    }
    pub fn observe(&mut self, ring_frames: usize) -> bool {
        self.done |= u64::try_from(ring_frames).unwrap_or(u64::MAX) >= u64::from(self.threshold);
        self.done
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CaptureWake {
    pub wake_seq: u64,
    pub wait_start_qpc: u64,
    pub wait_return_qpc: u64,
    pub done_qpc: u64,
    pub event: bool,
    pub packets: u32,
    pub frames_received: u32,
    pub frames_pushed: u32,
    pub overflow_frames: u32,
    pub invalid_frames: u32,
    pub silent_frames: u32,
    pub discontinuity_packets: u32,
    pub timestamp_error_packets: u32,
    pub first_device_position: u64,
    pub first_packet_qpc_hns: u64,
    pub failure_hresult: i32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RenderWake {
    pub wake_seq: u64,
    pub wait_start_qpc: u64,
    pub wait_return_qpc: u64,
    pub done_qpc: u64,
    pub event: bool,
    pub primed: bool,
    pub padding_before: u32,
    pub padding_valid: bool,
    pub ring_frames_before: u32,
    pub frames_written: u32,
    pub frames_from_ring: u32,
    pub underrun_frames: u32,
    pub priming_silence_frames: u32,
    pub nonfinite_samples: u32,
    pub clamped_samples: u32,
    pub submitted: bool,
    pub failure_hresult: i32,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum LoopEvent {
    Capture(CaptureWake),
    Render(RenderWake),
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CaptureTotals {
    pub received: u64,
    pub pushed: u64,
    pub overflow: u64,
    pub invalid: u64,
    pub silent: u64,
    pub wakes: u64,
    /// Increment separately when publishing a record fails.
    pub dropped_records: u64,
}
impl CaptureTotals {
    pub fn add(&mut self, wake: &CaptureWake) {
        self.received = self
            .received
            .saturating_add(u64::from(wake.frames_received));
        self.pushed = self.pushed.saturating_add(u64::from(wake.frames_pushed));
        self.overflow = self
            .overflow
            .saturating_add(u64::from(wake.overflow_frames));
        self.invalid = self.invalid.saturating_add(u64::from(wake.invalid_frames));
        self.silent = self.silent.saturating_add(u64::from(wake.silent_frames));
        self.wakes = self.wakes.saturating_add(1);
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RenderTotals {
    pub popped: u64,
    pub submitted: u64,
    pub submission_failed: u64,
    pub underrun: u64,
    pub priming_silence: u64,
    pub written: u64,
    pub nonfinite: u64,
    pub clamped: u64,
    pub wakes: u64,
    /// Increment separately when publishing a record fails.
    pub dropped_records: u64,
    pub primed_at_qpc: Option<u64>,
}
impl RenderTotals {
    pub fn add(&mut self, wake: &RenderWake) {
        let frames = u64::from(wake.frames_from_ring);
        self.popped = self.popped.saturating_add(frames);
        if wake.submitted {
            self.submitted = self.submitted.saturating_add(frames);
            self.written = self.written.saturating_add(u64::from(wake.frames_written));
        } else {
            self.submission_failed = self.submission_failed.saturating_add(frames);
        }
        self.underrun = self
            .underrun
            .saturating_add(u64::from(wake.underrun_frames));
        self.priming_silence = self
            .priming_silence
            .saturating_add(u64::from(wake.priming_silence_frames));
        self.nonfinite = self
            .nonfinite
            .saturating_add(u64::from(wake.nonfinite_samples));
        self.clamped = self.clamped.saturating_add(u64::from(wake.clamped_samples));
        self.wakes = self.wakes.saturating_add(1);
        if wake.primed && self.primed_at_qpc.is_none() {
            self.primed_at_qpc = Some(wake.wait_return_qpc);
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Accounting {
    pub capture_ok: bool,
    pub ring_ok: bool,
    pub render_ok: bool,
}
/// Overflow cannot establish a valid equality.
pub fn accounting(c: &CaptureTotals, r: &RenderTotals, final_ring_frames: u64) -> Accounting {
    Accounting {
        capture_ok: c
            .pushed
            .checked_add(c.overflow)
            .and_then(|frames| frames.checked_add(c.invalid))
            == Some(c.received),
        ring_ok: r.popped.checked_add(final_ring_frames) == Some(c.pushed),
        render_ok: r.submitted.checked_add(r.submission_failed) == Some(r.popped),
    }
}

#[derive(Clone, Debug, PartialEq)]
pub struct LoopSummary {
    pub capture_intervals_ms: Option<Distribution>,
    pub render_intervals_ms: Option<Distribution>,
    pub ring_frames_before: Option<Distribution>,
    /// Observed records only, after priming; incomplete if records were dropped.
    pub underrun_wakes: u64,
    pub underrun_frames: u64,
    pub overflow_frames: u64,
    /// All observed render wakes, including priming.
    pub zero_padding_wakes: u64,
    pub capture: CaptureTotals,
    pub render: RenderTotals,
    pub final_ring_frames: u64,
    pub accounting: Accounting,
    pub render_submitted_over_capture_received: Option<f64>,
    pub records_lost: bool,
}

/// All wait returns (events and timeouts), per side in supplied record order.
/// QPC is diagnostic only. Frequency zero yields no interval distribution.
pub fn summarize(
    events: &[LoopEvent],
    qpc_frequency: u64,
    c: &CaptureTotals,
    r: &RenderTotals,
    final_ring_frames: u64,
) -> LoopSummary {
    let mut capture_intervals = Vec::new();
    let mut render_intervals = Vec::new();
    let mut capture_previous = None;
    let mut render_previous = None;
    let mut levels = Vec::new();
    let mut s = LoopSummary {
        capture_intervals_ms: None,
        render_intervals_ms: None,
        ring_frames_before: None,
        underrun_wakes: 0,
        underrun_frames: 0,
        overflow_frames: c.overflow,
        zero_padding_wakes: 0,
        capture: *c,
        render: *r,
        final_ring_frames,
        accounting: accounting(c, r, final_ring_frames),
        render_submitted_over_capture_received: (c.received != 0)
            .then(|| r.submitted as f64 / c.received as f64),
        records_lost: c.dropped_records != 0 || r.dropped_records != 0,
    };
    for event in events {
        let (time, previous, intervals) = match event {
            LoopEvent::Capture(w) => (
                w.wait_return_qpc,
                &mut capture_previous,
                &mut capture_intervals,
            ),
            LoopEvent::Render(w) => {
                if w.padding_valid && w.padding_before == 0 {
                    s.zero_padding_wakes = s.zero_padding_wakes.saturating_add(1);
                }
                if w.primed {
                    levels.push(f64::from(w.ring_frames_before));
                    if w.underrun_frames != 0 {
                        s.underrun_wakes = s.underrun_wakes.saturating_add(1);
                    }
                    s.underrun_frames = s
                        .underrun_frames
                        .saturating_add(u64::from(w.underrun_frames));
                }
                (
                    w.wait_return_qpc,
                    &mut render_previous,
                    &mut render_intervals,
                )
            }
        };
        if qpc_frequency != 0
            && let Some(delta) = previous.and_then(|p| time.checked_sub(p))
        {
            intervals.push(delta as f64 * 1000.0 / qpc_frequency as f64);
        }
        *previous = Some(time);
    }
    s.capture_intervals_ms = distribution(capture_intervals);
    s.render_intervals_ms = distribution(render_intervals);
    s.ring_frames_before = distribution(levels);
    s
}
impl LoopSummary {
    pub fn text(&self) -> String {
        let dist = |d: &Option<Distribution>| match d {
            Some(d) => format!(
                "min={} p50={} p95={} p99={} max={}",
                d.min, d.p50, d.p95, d.p99, d.max
            ),
            None => "n/a".into(),
        };
        let ratio = self
            .render_submitted_over_capture_received
            .map_or_else(|| "n/a".into(), |v| v.to_string());
        format!(
            concat!(
                "capture_wait_return_intervals_ms={}\nrender_wait_return_intervals_ms={}\n",
                "primed_ring_frames_before={}\nunderrun_wakes={}\nunderrun_frames={}\noverflow_frames={}\n",
                "zero_padding_wakes={} (글리치로 단정하지 않음)\n",
                "capture_totals={:?}\nrender_totals={:?}\nfinal_ring_frames={}\n",
                "capture_ok={}\nring_ok={}\nrender_ok={}\n",
                "render_submitted_over_capture_received={} (링→WASAPI 제출 경계의 비율이며 원본 보존율 전체가 아님)\n{}\n"
            ),
            dist(&self.capture_intervals_ms),
            dist(&self.render_intervals_ms),
            dist(&self.ring_frames_before),
            self.underrun_wakes,
            self.underrun_frames,
            self.overflow_frames,
            self.zero_padding_wakes,
            self.capture,
            self.render,
            self.final_ring_frames,
            self.accounting.capture_ok,
            self.accounting.ring_ok,
            self.accounting.render_ok,
            ratio,
            if self.records_lost {
                "기록 유실 있음, 분포 불완전"
            } else {
                "기록 유실 없음"
            }
        )
    }
}

/// Rectangular union of all wake fields; unrelated cells are empty.
pub fn csv(events: &[LoopEvent]) -> String {
    let mut out = String::from(
        "side,wake_seq,wait_start_qpc,wait_return_qpc,done_qpc,event,packets,frames_received,frames_pushed,overflow_frames,silent_frames,discontinuity_packets,timestamp_error_packets,first_device_position,first_packet_qpc_hns,failure_hresult,primed,padding_before,ring_frames_before,frames_written,frames_from_ring,underrun_frames,priming_silence_frames,nonfinite_samples,clamped_samples,submitted,invalid_frames,padding_valid\n",
    );
    for event in events {
        let row = match event {
            LoopEvent::Capture(w) => [
                "capture".to_string(),
                w.wake_seq.to_string(),
                w.wait_start_qpc.to_string(),
                w.wait_return_qpc.to_string(),
                w.done_qpc.to_string(),
                w.event.to_string(),
                w.packets.to_string(),
                w.frames_received.to_string(),
                w.frames_pushed.to_string(),
                w.overflow_frames.to_string(),
                w.silent_frames.to_string(),
                w.discontinuity_packets.to_string(),
                w.timestamp_error_packets.to_string(),
                w.first_device_position.to_string(),
                w.first_packet_qpc_hns.to_string(),
                w.failure_hresult.to_string(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                w.invalid_frames.to_string(),
                String::new(),
            ],
            LoopEvent::Render(w) => [
                "render".to_string(),
                w.wake_seq.to_string(),
                w.wait_start_qpc.to_string(),
                w.wait_return_qpc.to_string(),
                w.done_qpc.to_string(),
                w.event.to_string(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                String::new(),
                w.failure_hresult.to_string(),
                w.primed.to_string(),
                w.padding_before.to_string(),
                w.ring_frames_before.to_string(),
                w.frames_written.to_string(),
                w.frames_from_ring.to_string(),
                w.underrun_frames.to_string(),
                w.priming_silence_frames.to_string(),
                w.nonfinite_samples.to_string(),
                w.clamped_samples.to_string(),
                w.submitted.to_string(),
                String::new(),
                w.padding_valid.to_string(),
            ],
        };
        out.push_str(&row.join(","));
        out.push('\n');
    }
    out
}

/// Strict WAVEFORMATEXTENSIBLE float stereo 48 kHz check.
pub fn is_f32_stereo_48k(bytes: &[u8]) -> bool {
    let word = |offset: usize| {
        bytes
            .get(offset..offset.saturating_add(2))
            .and_then(|b| <[u8; 2]>::try_from(b).ok())
            .map(u16::from_le_bytes)
    };
    let dword = |offset: usize| {
        bytes
            .get(offset..offset.saturating_add(4))
            .and_then(|b| <[u8; 4]>::try_from(b).ok())
            .map(u32::from_le_bytes)
    };
    bytes.len() >= 40
        && word(0) == Some(0xfffe)
        && word(2) == Some(2)
        && dword(4) == Some(48000)
        && dword(8) == Some(384000)
        && word(12) == Some(8)
        && word(14) == Some(32)
        && word(16).is_some_and(|n| n >= 22)
        && word(18) == Some(32)
        && bytes.get(24..40) == Some(&[3, 0, 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113])
}
