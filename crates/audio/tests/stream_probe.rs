use mdc_audio::{
    CaptureFlags, CaptureRecord, DevicePosition, Hns, QpcTicks, RenderRecord, spsc,
    stream_report::*,
};

#[test]
fn ring_boundaries_and_repeated_wrap() {
    assert!(spsc::channel::<u32>(0).is_none());
    assert!(spsc::channel::<u32>(3).is_none());
    for capacity in [1, 2, 4, 128] {
        let (mut p, mut c) = spsc::channel(capacity).unwrap();
        assert_eq!(c.pop(), None);
        for round in 0..1000 {
            for i in 0..capacity {
                assert!(p.push((round, i)));
            }
            assert!(!p.push((9999, 9999)));
            for i in 0..capacity {
                assert_eq!(c.pop(), Some((round, i)));
            }
            assert_eq!(c.pop(), None);
        }
    }
}
#[test]
fn ring_two_thread_content_stress() {
    let (mut p, mut c) = spsc::channel(128).unwrap();
    let thread = std::thread::spawn(move || {
        for i in 0..500_000u64 {
            while !p.push((i, !i, i.wrapping_mul(17))) {
                std::hint::spin_loop();
            }
        }
    });
    for i in 0..500_000u64 {
        let value = loop {
            if let Some(v) = c.pop() {
                break v;
            }
            std::hint::spin_loop();
        };
        assert_eq!(value, (i, !i, i.wrapping_mul(17)));
    }
    thread.join().unwrap();
    assert_eq!(c.pop(), None);
}
fn wake(time: u64, packets: u32) -> ProbeEvent {
    ProbeEvent::Wake(WakeRecord {
        wake_seq: time,
        wait_start: time.saturating_sub(1),
        wait_return: time,
        result: WaitResult::Event,
        packets,
        frames: u64::from(packets) * 48,
        done: time + 1,
        failure_api: Api::None,
        hresult: 0,
        buffer_empty_count: 0,
        buffer_error_count: 0,
        zero_frame_count: 0,
        last_capture_status: 0,
        statuses: CallStatuses::default(),
    })
}
fn capture(position: u64, wake: u64) -> ProbeEvent {
    capture_with_frames(position, wake, 48)
}
fn capture_with_frames(position: u64, wake: u64, frames: u32) -> ProbeEvent {
    ProbeEvent::Capture(CaptureRecord {
        seq: position,
        wake: QpcTicks(wake),
        after_get_buffer: QpcTicks(wake + 1),
        device_position: DevicePosition(position),
        first_frame_qpc: Hns(100),
        frames,
        flags: CaptureFlags {
            data_discontinuity: true,
            silent: false,
            timestamp_error: true,
        },
        done: QpcTicks(wake + 2),
    })
}
#[test]
fn summary_empty_wakes_multiple_packets_continuity_and_flags() {
    let records = [
        capture(0, 10),
        capture(48, 10),
        wake(10, 2),
        wake(20, 0),
        wake(30, 0),
        capture(144, 40),
        wake(40, 1),
    ];
    let s = summarize(&records, 1000, 3);
    assert_eq!(s.all_intervals_ms.as_ref().unwrap().p50, 10.0);
    assert_eq!(s.packet_intervals_ms.as_ref().unwrap().p50, 30.0);
    assert_eq!(s.empty_wakes, 2);
    assert_eq!(s.position_plus_frames_mismatches, 1);
    assert_eq!(s.position_deltas, [(48, 1), (96, 1)].into());
    assert_eq!(s.packet_frames.get(&48), Some(&3));
    assert_eq!(s.packets_per_wake.get(&2), Some(&1));
    assert_eq!(s.discontinuity_flags, 3);
    assert_eq!(s.timestamp_error_flags, 3);
    assert!(s.text().contains("기록 유실 있음"));
}
#[test]
fn empty_and_nearest_rank() {
    assert_eq!(summarize(&[], 1000, 0), Summary::default());
    assert_eq!(distribution(vec![]), None);
    let d = distribution((1..=100).rev().map(f64::from).collect()).unwrap();
    assert_eq!(
        (d.min, d.p50, d.p95, d.p99, d.max),
        (1.0, 50.0, 95.0, 99.0, 100.0)
    );
    assert_eq!(distribution(vec![1.0, 2.0]).unwrap().p50, 1.0);
    assert_eq!(distribution(vec![9.0]).unwrap().p99, 9.0);
    assert_eq!(
        summarize(&[wake(10, 1), wake(20, 1)], 0, 0).all_intervals_ms,
        None
    );
}
#[test]
fn timeout_render_and_csv_rectangle_escape() {
    let mut w = match wake(10, 0) {
        ProbeEvent::Wake(w) => w,
        _ => unreachable!(),
    };
    w.result = WaitResult::Timeout;
    w.buffer_error_count = 2;
    w.zero_frame_count = 3;
    let r = ProbeEvent::Render(RenderRecord {
        seq: 0,
        wake: QpcTicks(10),
        padding_before: 0,
        frames_written: 48,
        after_release: QpcTicks(12),
        clock_position: 42,
        clock_qpc: Hns(100),
        source: None,
        work: QpcTicks(3),
        deadline_missed: false,
    });
    let events = [ProbeEvent::Wake(w), capture(0, 10), r];
    let s = summarize(&events, 1000, 0);
    assert_eq!(s.timeouts, 1);
    assert_eq!(s.buffer_error_count, 2);
    assert_eq!(s.zero_frame_count, 3);
    assert!(s.text().contains("buffer_error_count=2\n"));
    assert!(s.text().contains("zero_frame_count=3\n"));
    assert_eq!(s.empty_wakes, 0);
    assert_eq!(s.zero_padding, 1);
    assert_eq!(s.frames_written.get(&48), Some(&1));
    let text = csv(&events);
    let header: Vec<_> = text.lines().next().unwrap().split(',').collect();
    let row: Vec<_> = text.lines().nth(1).unwrap().split(',').collect();
    for (name, value) in [("buffer_error_count", "2"), ("zero_frame_count", "3")] {
        let column = header.iter().position(|c| *c == name).unwrap();
        assert_eq!(row[column], value);
    }
    let columns = header.len();
    assert!(text.lines().all(|line| line.split(',').count() == columns));
    assert_eq!(csv_field("a,\"b\"\r\nc"), "\"a,\"\"b\"\"\r\nc\"");
    assert_eq!(csv_field("simple"), "simple");
    assert_eq!(
        metadata(&[("key".into(), "a\\b\r\nc=d".into())]),
        "key=a\\\\b\\r\\nc=d\n"
    );
}

#[test]
fn event_intervals_skip_timeout_wait_returns() {
    let ProbeEvent::Wake(mut timeout) = wake(200, 0) else {
        unreachable!();
    };
    timeout.result = WaitResult::Timeout;
    let s = summarize(
        &[wake(0, 1), ProbeEvent::Wake(timeout), wake(203, 1)],
        1000,
        0,
    );
    assert_eq!(s.event_intervals_ms, distribution(vec![203.0]));
    assert_eq!(s.all_intervals_ms, distribution(vec![200.0, 3.0]));
    assert_eq!(s.packet_intervals_ms, distribution(vec![203.0]));
    let text = s.text();
    assert!(text.contains("wait_return_intervals_ms="));
    assert!(text.contains("event_wake_intervals_ms="));
    assert!(text.contains("packet_wake_intervals_ms="));
}

#[test]
fn position_deltas_empty_single_and_interleaved_events() {
    assert!(summarize(&[], 1000, 0).position_deltas.is_empty());
    assert!(
        summarize(&[capture(12, 0)], 1000, 0)
            .position_deltas
            .is_empty()
    );
    let render = ProbeEvent::Render(RenderRecord {
        seq: 0,
        wake: QpcTicks(1),
        padding_before: 0,
        frames_written: 48,
        after_release: QpcTicks(2),
        clock_position: 0,
        clock_qpc: Hns(0),
        source: None,
        work: QpcTicks(1),
        deadline_missed: false,
    });
    let s = summarize(
        &[capture(12, 0), wake(1, 0), render, capture(60, 2)],
        1000,
        0,
    );
    assert_eq!(s.position_deltas, [(48, 1)].into());
    assert_eq!(s.position_plus_frames_mismatches, 0);
}

#[test]
fn position_deltas_repeated_backward_and_expected_position_overflow() {
    let s = summarize(&[capture(100, 0), capture(100, 1), capture(90, 2)], 1000, 0);
    assert_eq!(
        s.position_deltas,
        [(0, 1), (90u64.wrapping_sub(100), 1)].into()
    );
    assert_eq!(s.position_plus_frames_mismatches, 2);
    let s = summarize(
        &[capture(u64::MAX, 0), capture(47, 1), capture(95, 2)],
        1000,
        0,
    );
    assert_eq!(s.position_plus_frames_mismatches, 0);
    assert_eq!(s.position_deltas, [(48, 2)].into());
}

#[test]
fn position_deltas_preserve_position_units_independently_of_frames() {
    let packets = 10u64;
    let events: Vec<_> = (0..packets)
        .map(|i| capture_with_frames(i * 448, i, 488))
        .collect();
    let s = summarize(&events, 1000, 0);
    assert_eq!(s.position_plus_frames_mismatches, packets - 1);
    assert_eq!(s.position_deltas, [(448, packets - 1)].into());
}

#[test]
fn position_delta_text_top8_sorted_and_limited() {
    let mut events = vec![capture(0, 0)];
    let mut position = 0;
    for delta in [9, 8, 7, 6, 5, 4, 3, 2, 1, 9, 9, 8] {
        position += delta;
        events.push(capture(position, 0));
    }
    let s = summarize(&events, 1000, 0);
    assert_eq!(s.position_deltas.len(), 9);
    let text = s.text();
    assert!(text.contains(&format!(
        "position_plus_frames_mismatches={}\nposition_delta_distinct=9\nposition_deltas_top8=[(9, 3), (8, 2), (1, 1), (2, 1), (3, 1), (4, 1), (5, 1), (6, 1)]\n",
        s.position_plus_frames_mismatches
    )));
    assert!(!text.contains("continuity_violations"));
}
