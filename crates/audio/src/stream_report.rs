//! Probe-only values and control-thread reporting. No Windows types.
use crate::{CaptureRecord, RenderRecord};
use std::collections::BTreeMap;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WaitResult {
    Event,
    Timeout,
    Failed,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Default)]
pub enum Api {
    #[default]
    None,
    Qpc,
    Wait,
    NextPacket,
    CaptureGet,
    CaptureRelease,
    Padding,
    RenderGet,
    RenderRelease,
    ClockPosition,
    Arithmetic,
    PacketLimit,
}
#[derive(Clone, Copy, Debug)]
pub struct WakeRecord {
    pub wake_seq: u64,
    pub wait_start: u64,
    pub wait_return: u64,
    pub result: WaitResult,
    pub packets: u32,
    pub frames: u64,
    pub done: u64,
    pub failure_api: Api,
    pub hresult: i32,
    /// Preserve AUDCLNT_S_BUFFER_EMPTY rather than folding it into S_OK.
    pub buffer_empty_count: u32,
    pub buffer_error_count: u32,
    pub zero_frame_count: u32,
    pub last_capture_status: i32,
    pub statuses: CallStatuses,
}
/// Last raw HRESULT per API in a wake, including nonnegative success statuses.
/// None means the API was not called. Repeated BUFFER_EMPTY is counted separately.
#[derive(Clone, Copy, Debug, Default)]
pub struct CallStatuses {
    pub next_packet: Option<i32>,
    pub capture_get: Option<i32>,
    pub capture_release: Option<i32>,
    pub padding: Option<i32>,
    pub render_get: Option<i32>,
    pub render_release: Option<i32>,
    pub clock_position: Option<i32>,
}
#[derive(Clone, Copy, Debug)]
pub enum ProbeEvent {
    Wake(WakeRecord),
    Capture(CaptureRecord),
    Render(RenderRecord),
}

#[derive(Clone, Debug, PartialEq)]
pub struct Distribution {
    pub min: f64,
    pub p50: f64,
    pub p95: f64,
    pub p99: f64,
    pub max: f64,
}
/// Nearest-rank: rank = ceil(p * n), one-based, including for even n.
pub fn distribution(mut values: Vec<f64>) -> Option<Distribution> {
    if values.is_empty() {
        return None;
    }
    values.sort_by(f64::total_cmp);
    let rank = |p: usize| values[(values.len() * p).div_ceil(100).saturating_sub(1)];
    Some(Distribution {
        min: values[0],
        p50: rank(50),
        p95: rank(95),
        p99: rank(99),
        max: values[values.len() - 1],
    })
}
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Summary {
    pub all_intervals_ms: Option<Distribution>,
    pub event_intervals_ms: Option<Distribution>,
    pub packet_intervals_ms: Option<Distribution>,
    pub empty_wakes: u64,
    pub buffer_error_count: u64,
    pub zero_frame_count: u64,
    pub timeouts: u64,
    pub packet_frames: BTreeMap<u32, u64>,
    pub packets_per_wake: BTreeMap<u32, u64>,
    pub continuity_violations: u64,
    pub discontinuity_flags: u64,
    pub silent_flags: u64,
    pub timestamp_error_flags: u64,
    pub zero_padding: u64,
    pub frames_written: BTreeMap<u32, u64>,
    pub records_lost: bool,
}
fn count(map: &mut BTreeMap<u32, u64>, value: u32) {
    *map.entry(value).or_default() += 1;
}
pub fn summarize(events: &[ProbeEvent], frequency: u64, dropped: u64) -> Summary {
    let mut s = Summary {
        records_lost: dropped != 0,
        ..Summary::default()
    };
    let mut all = Vec::new();
    let mut event_intervals = Vec::new();
    let mut previous_event = None;
    let mut packets = Vec::new();
    let mut previous = None;
    let mut previous_packet = None;
    let mut expected_position = None;
    let interval = |before: Option<u64>, now: u64, into: &mut Vec<f64>| {
        if frequency != 0
            && let Some(delta) = before.and_then(|b| now.checked_sub(b))
        {
            into.push(delta as f64 * 1000.0 / frequency as f64);
        }
    };
    for event in events {
        match event {
            ProbeEvent::Wake(w) => {
                interval(previous, w.wait_return, &mut all);
                previous = Some(w.wait_return);
                if w.result == WaitResult::Event {
                    interval(previous_event, w.wait_return, &mut event_intervals);
                    previous_event = Some(w.wait_return);
                }
                s.buffer_error_count += u64::from(w.buffer_error_count);
                s.zero_frame_count += u64::from(w.zero_frame_count);
                if w.packets != 0 {
                    interval(previous_packet, w.wait_return, &mut packets);
                    previous_packet = Some(w.wait_return);
                }
                if w.result == WaitResult::Event && w.packets == 0 {
                    s.empty_wakes += 1;
                }
                if w.result == WaitResult::Timeout {
                    s.timeouts += 1;
                }
                count(&mut s.packets_per_wake, w.packets);
            }
            ProbeEvent::Capture(c) => {
                count(&mut s.packet_frames, c.frames);
                if expected_position.is_some_and(|p| p != c.device_position.0) {
                    s.continuity_violations += 1;
                }
                expected_position = c.device_position.0.checked_add(u64::from(c.frames));
                s.discontinuity_flags += u64::from(c.flags.data_discontinuity);
                s.silent_flags += u64::from(c.flags.silent);
                s.timestamp_error_flags += u64::from(c.flags.timestamp_error);
            }
            ProbeEvent::Render(r) => {
                s.zero_padding += u64::from(r.padding_before == 0);
                count(&mut s.frames_written, r.frames_written);
            }
        }
    }
    s.all_intervals_ms = distribution(all);
    s.event_intervals_ms = distribution(event_intervals);
    s.packet_intervals_ms = distribution(packets);
    s
}
impl Summary {
    pub fn text(&self) -> String {
        let mut out = format!(
            "wait_return_intervals_ms={:?}\nevent_wake_intervals_ms={:?}\npacket_wake_intervals_ms={:?}\nbuffer_error_count={}\nzero_frame_count={}\nempty_wakes={}\ntimeouts={}\npacket_frames={:?}\npackets_per_wake={:?}\ncontinuity_violations={}\nflags.data_discontinuity={}\nflags.silent={}\nflags.timestamp_error={}\nrender.zero_padding={}\nrender.frames_written={:?}\n",
            self.all_intervals_ms,
            self.event_intervals_ms,
            self.packet_intervals_ms,
            self.buffer_error_count,
            self.zero_frame_count,
            self.empty_wakes,
            self.timeouts,
            self.packet_frames,
            self.packets_per_wake,
            self.continuity_violations,
            self.discontinuity_flags,
            self.silent_flags,
            self.timestamp_error_flags,
            self.zero_padding,
            self.frames_written
        );
        out.push_str(if self.records_lost {
            "record_integrity=기록 유실 있음, 분위수·연속성 불완전\n"
        } else {
            "record_integrity=complete\n"
        });
        out
    }
}
pub fn csv_field(value: &str) -> String {
    if value.contains([',', '"', '\r', '\n']) {
        format!("\"{}\"", value.replace('"', "\"\""))
    } else {
        value.into()
    }
}
const COLUMNS: &[&str] = &[
    "kind",
    "seq",
    "wake_qpc",
    "wait_start_qpc",
    "wait_result",
    "packets",
    "total_frames",
    "done_qpc",
    "failure_api",
    "hresult",
    "buffer_empty_count",
    "last_capture_status",
    "after_get_buffer_qpc",
    "device_position",
    "first_frame_qpc_hns",
    "frames",
    "data_discontinuity",
    "silent",
    "timestamp_error",
    "padding_before",
    "frames_written",
    "after_release_qpc",
    "clock_position",
    "clock_qpc_hns",
    "source",
    "work_qpc",
    "deadline_missed",
    "next_packet_hresult",
    "capture_get_hresult",
    "capture_release_hresult",
    "padding_hresult",
    "render_get_hresult",
    "render_release_hresult",
    "clock_position_hresult",
    "buffer_error_count",
    "zero_frame_count",
];
pub fn csv(events: &[ProbeEvent]) -> String {
    let mut out = COLUMNS.join(",");
    out.push('\n');
    for e in events {
        let mut row = vec![String::new(); COLUMNS.len()];
        match e {
            ProbeEvent::Wake(w) => {
                row[0] = "wake".into();
                row[1] = w.wake_seq.to_string();
                row[2] = w.wait_return.to_string();
                row[3] = w.wait_start.to_string();
                row[4] = format!("{:?}", w.result);
                row[5] = w.packets.to_string();
                row[6] = w.frames.to_string();
                row[7] = w.done.to_string();
                row[8] = format!("{:?}", w.failure_api);
                row[9] = w.hresult.to_string();
                row[10] = w.buffer_empty_count.to_string();
                row[11] = w.last_capture_status.to_string();
                row[34] = w.buffer_error_count.to_string();
                row[35] = w.zero_frame_count.to_string();
                for (column, status) in row.iter_mut().skip(27).zip([
                    w.statuses.next_packet,
                    w.statuses.capture_get,
                    w.statuses.capture_release,
                    w.statuses.padding,
                    w.statuses.render_get,
                    w.statuses.render_release,
                    w.statuses.clock_position,
                ]) {
                    *column = status.map(|hr| hr.to_string()).unwrap_or_default();
                }
            }
            ProbeEvent::Capture(c) => {
                row[0] = "capture".into();
                row[1] = c.seq.to_string();
                row[2] = c.wake.0.to_string();
                row[7] = c.done.0.to_string();
                row[12] = c.after_get_buffer.0.to_string();
                row[13] = c.device_position.0.to_string();
                row[14] = c.first_frame_qpc.0.to_string();
                row[15] = c.frames.to_string();
                row[16] = c.flags.data_discontinuity.to_string();
                row[17] = c.flags.silent.to_string();
                row[18] = c.flags.timestamp_error.to_string();
            }
            ProbeEvent::Render(r) => {
                row[0] = "render".into();
                row[1] = r.seq.to_string();
                row[2] = r.wake.0.to_string();
                row[19] = r.padding_before.to_string();
                row[20] = r.frames_written.to_string();
                row[21] = r.after_release.0.to_string();
                row[22] = r.clock_position.to_string();
                row[23] = r.clock_qpc.0.to_string();
                row[24] = format!("{:?}", r.source);
                row[25] = r.work.0.to_string();
                row[26] = r.deadline_missed.to_string();
            }
        }
        out.push_str(
            &row.iter()
                .map(|v| csv_field(v))
                .collect::<Vec<_>>()
                .join(","),
        );
        out.push('\n');
    }
    out
}
/// Metadata is one key=value per physical line; escapes are reversible.
pub fn metadata(fields: &[(String, String)]) -> String {
    let escape = |s: &str| {
        s.replace('\\', "\\\\")
            .replace('\r', "\\r")
            .replace('\n', "\\n")
    };
    fields
        .iter()
        .map(|(k, v)| format!("{}={}\n", escape(k), escape(v)))
        .collect()
}
