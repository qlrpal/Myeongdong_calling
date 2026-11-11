use mdc_audio::{loopback_report::*, spsc};

#[test]
fn ring_len_and_keeper_workers() {
    assert!(spsc::channel_with_keeper::<u8>(0).is_none());
    assert!(spsc::channel_with_keeper::<u8>(3).is_none());
    let (mut p, mut c, keeper) = spsc::channel_with_keeper(4).unwrap();
    assert_eq!(c.len(), 0);
    for _ in 0..4 {
        assert!(p.push([0.25_f32, -0.5]));
    }
    assert_eq!(c.len(), 4);
    assert!(!p.push([0.0, 0.0]));
    assert_eq!(c.pop(), Some([0.25, -0.5]));
    assert_eq!(c.len(), 3);
    std::thread::spawn(move || drop(p)).join().unwrap();
    std::thread::spawn(move || {
        for _ in 0..3 {
            assert_eq!(c.pop(), Some([0.25, -0.5]));
        }
        assert_eq!(c.len(), 0);
    })
    .join()
    .unwrap();
    drop(keeper);
}

#[test]
fn len_concurrent_publication_is_bounded_and_monotone() {
    use std::sync::{Arc, Barrier};
    let capacity = 65536;
    let (mut p, c) = spsc::channel(capacity).unwrap();
    let barrier = Arc::new(Barrier::new(2));
    let start = Arc::clone(&barrier);
    let worker = std::thread::spawn(move || {
        start.wait();
        for n in 0..capacity {
            assert!(p.push(n));
            if n % 64 == 0 {
                std::thread::yield_now();
            }
        }
    });
    barrier.wait();
    let mut previous = 0;
    loop {
        let now = c.len();
        assert!(now <= capacity && now >= previous);
        previous = now;
        if now == capacity {
            break;
        }
        std::thread::yield_now();
    }
    worker.join().unwrap();
    assert_eq!(c.len(), capacity);
}

#[test]
fn gains_and_protection() {
    assert_eq!(gain_linear(0.0), Some(1.0));
    assert!((gain_linear(-20.0).unwrap() - 0.1).abs() < 1e-6);
    assert!(gain_linear(-60.0).is_some());
    for db in [-60.1, 0.1, f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
        assert_eq!(gain_linear(db), None);
    }
    for sample in [f32::NAN, f32::INFINITY, f32::NEG_INFINITY] {
        assert_eq!(protect(sample, 1.0), (0.0, SampleFault::NonFinite));
    }
    assert_eq!(protect(2.0, 1.0), (1.0, SampleFault::Clamped));
    assert_eq!(protect(-2.0, 1.0), (-1.0, SampleFault::Clamped));
    assert_eq!(protect(0.5, 0.1), (0.05, SampleFault::None));
    for sample in [f32::MIN, -2.0, 0.0, 1.0, f32::MAX, f32::NAN, f32::INFINITY] {
        for gain in [f32::MIN, 0.0, 1.0, f32::MAX, f32::NAN, f32::INFINITY] {
            let (value, _) = protect(sample, gain);
            assert!(value.is_finite() && (-1.0..=1.0).contains(&value));
        }
    }
}

#[test]
fn frame_limits_and_priming_latch() {
    for (b, p, n, expected) in [
        (280, 0, 128, Some(128)),
        (280, 200, 128, Some(80)),
        (280, 280, 128, Some(0)),
        (280, 281, 128, None),
        (280, 0, 0, None),
    ] {
        assert_eq!(render_frames(b, p, n), expected);
    }
    assert!(Priming::new(0).observe(0));
    let mut p = Priming::new(480);
    for _ in 0..1000 {
        assert!(!p.observe(479));
    }
    assert!(p.observe(480));
    assert!(p.observe(0));
    assert!(Priming::new(u32::MAX).observe(usize::MAX));
}

fn simulation(capacity: usize, prefill: u32) -> LoopSummary {
    let (mut p, mut ring, _keeper) = spsc::channel_with_keeper(capacity).unwrap();
    let mut priming = Priming::new(prefill);
    let mut c = CaptureTotals::default();
    let mut r = RenderTotals::default();
    let mut events = Vec::new();
    // Integer sample clock: 480/48000 = 10ms; 128/48000 = 2.666...ms.
    for tick in 0..48000 {
        if tick % 480 == 0 {
            let mut w = CaptureWake {
                wait_return_qpc: tick,
                frames_received: 480,
                packets: 1,
                event: true,
                ..CaptureWake::default()
            };
            for _ in 0..480 {
                if p.push([0.5_f32, -0.5]) {
                    w.frames_pushed += 1;
                } else {
                    w.overflow_frames += 1;
                }
            }
            c.add(&w);
            events.push(LoopEvent::Capture(w));
        }
        if tick % 128 == 0 {
            let before = ring.len();
            let primed = priming.observe(before);
            let frames = render_frames(280, 0, 128).unwrap();
            let mut w = RenderWake {
                wait_return_qpc: tick,
                primed,
                padding_valid: true,
                ring_frames_before: before as u32,
                frames_written: frames,
                submitted: true,
                event: true,
                ..RenderWake::default()
            };
            for _ in 0..frames {
                if !primed {
                    w.priming_silence_frames += 1;
                } else if ring.pop().is_some() {
                    w.frames_from_ring += 1;
                } else {
                    w.underrun_frames += 1;
                }
            }
            r.add(&w);
            events.push(LoopEvent::Render(w));
        }
    }
    let summary = summarize(&events, 48000, &c, &r, ring.len() as u64);
    assert_eq!(
        summary.accounting,
        Accounting {
            capture_ok: true,
            ring_ok: true,
            render_ok: true
        }
    );
    assert_eq!(summary.capture.received, 48000);
    assert_eq!(summary.render.wakes, 375);
    assert_eq!(summary.underrun_frames, summary.render.underrun);
    summary
}
#[test]
fn synthetic_one_second_and_overflow_accounting() {
    let regular = simulation(1024, 480);
    assert_eq!(regular.overflow_frames, 0);
    assert_eq!(regular.render.primed_at_qpc, Some(0));
    assert_eq!(regular.render.submitted + regular.render.underrun, 48000);
    let small = simulation(256, 128);
    assert!(small.overflow_frames > 0);
    assert!(small.underrun_frames > 0);
    // Small ring can also fail to reach the prefill indefinitely.
    let unprimed = simulation(256, 480);
    assert_eq!(unprimed.render.primed_at_qpc, None);
    assert_eq!(unprimed.render.priming_silence, 48000);
    assert_eq!(unprimed.underrun_frames, 0);
}

#[test]
fn totals_failed_submission_first_priming_and_saturation() {
    let mut r = RenderTotals::default();
    let mut w = RenderWake {
        frames_from_ring: 100,
        frames_written: 128,
        wait_return_qpc: 10,
        ..RenderWake::default()
    };
    r.add(&w);
    assert_eq!(
        (r.popped, r.submitted, r.submission_failed, r.written),
        (100, 0, 100, 0)
    );
    assert_eq!(r.primed_at_qpc, None);
    w.primed = true;
    w.wait_return_qpc = 20;
    r.add(&w);
    w.submitted = true;
    w.wait_return_qpc = 30;
    r.add(&w);
    assert_eq!(
        (r.popped, r.submitted, r.submission_failed, r.written),
        (300, 100, 200, 128)
    );
    assert_eq!(r.primed_at_qpc, Some(20));
    let mut c = CaptureTotals {
        received: u64::MAX,
        pushed: u64::MAX,
        overflow: u64::MAX,
        invalid: u64::MAX,
        silent: u64::MAX,
        wakes: u64::MAX,
        dropped_records: 7,
    };
    c.add(&CaptureWake {
        frames_received: 1,
        frames_pushed: 1,
        overflow_frames: 1,
        invalid_frames: 1,
        silent_frames: 1,
        ..CaptureWake::default()
    });
    assert_eq!(c.received, u64::MAX);
    assert_eq!(c.invalid, u64::MAX);
    assert_eq!(
        (c.pushed, c.overflow, c.silent, c.wakes, c.dropped_records),
        (u64::MAX, u64::MAX, u64::MAX, u64::MAX, 7)
    );
    r = RenderTotals {
        popped: u64::MAX,
        submitted: u64::MAX,
        submission_failed: u64::MAX,
        underrun: u64::MAX,
        priming_silence: u64::MAX,
        written: u64::MAX,
        nonfinite: u64::MAX,
        clamped: u64::MAX,
        wakes: u64::MAX,
        dropped_records: 8,
        primed_at_qpc: Some(20),
    };
    w.underrun_frames = 1;
    w.priming_silence_frames = 1;
    w.nonfinite_samples = 1;
    w.clamped_samples = 1;
    r.add(&w);
    w.submitted = false;
    r.add(&w);
    assert_eq!(
        r,
        RenderTotals {
            popped: u64::MAX,
            submitted: u64::MAX,
            submission_failed: u64::MAX,
            underrun: u64::MAX,
            priming_silence: u64::MAX,
            written: u64::MAX,
            nonfinite: u64::MAX,
            clamped: u64::MAX,
            wakes: u64::MAX,
            dropped_records: 8,
            primed_at_qpc: Some(20)
        }
    );
    assert_eq!(
        accounting(&c, &r, 1),
        Accounting {
            capture_ok: false,
            ring_ok: false,
            render_ok: false
        }
    );
}

#[test]
fn summary_filters_counts_and_text() {
    let c = CaptureTotals::default();
    let r = RenderTotals::default();
    let empty = summarize(&[], 0, &c, &r, 0);
    assert!(
        empty
            .text()
            .contains("render_submitted_over_capture_received=n/a")
    );
    assert_eq!(empty.capture_intervals_ms, None);
    assert_eq!(empty.render_intervals_ms, None);
    let events = [
        LoopEvent::Capture(CaptureWake {
            wait_return_qpc: 10,
            ..CaptureWake::default()
        }),
        LoopEvent::Render(RenderWake {
            wait_return_qpc: 10,
            padding_valid: true,
            ring_frames_before: 999,
            underrun_frames: 999,
            ..RenderWake::default()
        }),
        LoopEvent::Render(RenderWake {
            wait_return_qpc: 12,
            primed: true,
            padding_valid: true,
            ring_frames_before: 48,
            underrun_frames: 80,
            ..RenderWake::default()
        }),
        LoopEvent::Capture(CaptureWake {
            wait_return_qpc: 20,
            ..CaptureWake::default()
        }),
        LoopEvent::Render(RenderWake {
            wait_return_qpc: 14,
            primed: true,
            padding_before: 1,
            padding_valid: true,
            ring_frames_before: 96,
            ..RenderWake::default()
        }),
    ];
    let c = CaptureTotals {
        received: 200,
        overflow: 25,
        dropped_records: 1,
        ..c
    };
    let r = RenderTotals {
        submitted: 100,
        ..r
    };
    let s = summarize(&events, 1000, &c, &r, 0);
    assert_eq!(s.capture_intervals_ms.as_ref().unwrap().p50, 10.0);
    assert_eq!(s.render_intervals_ms.as_ref().unwrap().p50, 2.0);
    let d = s.ring_frames_before.as_ref().unwrap();
    assert_eq!(
        (d.min, d.p50, d.p95, d.p99, d.max),
        (48.0, 48.0, 96.0, 96.0, 96.0)
    );
    assert_eq!(
        (
            s.underrun_wakes,
            s.underrun_frames,
            s.zero_padding_wakes,
            s.overflow_frames
        ),
        (1, 80, 2, 25)
    );
    assert_eq!(s.render_submitted_over_capture_received, Some(0.5));
    assert!(s.text().contains("기록 유실 있음, 분포 불완전"));
    assert!(s.text().contains("글리치로 단정하지 않음"));
    assert!(
        s.text()
            .contains("링→WASAPI 제출 경계의 비율이며 원본 보존율 전체가 아님")
    );
    assert_eq!(summarize(&events, 0, &c, &r, 0).render_intervals_ms, None);
    assert!(
        summarize(
            &[],
            1000,
            &CaptureTotals::default(),
            &RenderTotals {
                dropped_records: 1,
                ..r
            },
            0
        )
        .records_lost
    );
}

#[test]
fn csv_rectangle_union_and_side_values() {
    let events = [
        LoopEvent::Capture(CaptureWake {
            packets: 3,
            invalid_frames: 480,
            failure_hresult: -1,
            ..CaptureWake::default()
        }),
        LoopEvent::Render(RenderWake {
            submitted: true,
            frames_written: 128,
            padding_valid: true,
            ..RenderWake::default()
        }),
    ];
    let text = csv(&events);
    let rows: Vec<Vec<_>> = text.lines().map(|l| l.split(',').collect()).collect();
    let header = &rows[0];
    assert_eq!(header[0], "side");
    assert_eq!(rows[1][0], "capture");
    assert_eq!(rows[2][0], "render");
    assert!(rows.iter().all(|r| r.len() == header.len()));
    for (name, capture, render) in [
        ("packets", "3", ""),
        ("invalid_frames", "480", ""),
        ("padding_valid", "", "true"),
        ("submitted", "", "true"),
        ("failure_hresult", "-1", "0"),
        ("frames_written", "", "128"),
    ] {
        let col = header.iter().position(|h| *h == name).unwrap();
        assert_eq!(rows[1][col], capture);
        assert_eq!(rows[2][col], render);
    }
    assert_eq!(csv(&[]).lines().count(), 1);
}

#[test]
fn strict_mix_format() {
    let mut b = [0u8; 40];
    for (offset, value) in [
        (0, 0xfffe_u16),
        (2, 2),
        (12, 8),
        (14, 32),
        (16, 22),
        (18, 32),
    ] {
        b[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
    }
    b[4..8].copy_from_slice(&48000_u32.to_le_bytes());
    b[8..12].copy_from_slice(&384000_u32.to_le_bytes());
    b[24..40].copy_from_slice(&[3, 0, 0, 0, 0, 0, 16, 0, 128, 0, 0, 170, 0, 56, 155, 113]);
    assert!(is_f32_stereo_48k(&b));
    for (offset, value) in [(2, 1_u16), (12, 4), (14, 16), (16, 0), (18, 24), (24, 1)] {
        let mut bad = b;
        bad[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
        assert!(!is_f32_stereo_48k(&bad));
    }
    let mut bad = b;
    bad[4..8].copy_from_slice(&44100_u32.to_le_bytes());
    assert!(!is_f32_stereo_48k(&bad));
    assert!(!is_f32_stereo_48k(&b[..39]));
    bad[16..18].copy_from_slice(&0_u16.to_le_bytes());
    assert!(!is_f32_stereo_48k(&bad[..18]));
}

#[test]
fn invalid_capture_frames_are_accounted() {
    let mut c = CaptureTotals::default();
    for _ in 0..2 {
        c.add(&CaptureWake {
            frames_received: 240,
            invalid_frames: 240,
            ..CaptureWake::default()
        });
    }
    assert_eq!(
        (c.received, c.pushed, c.overflow, c.invalid),
        (480, 0, 0, 480)
    );
    let r = RenderTotals::default();
    assert!(accounting(&c, &r, 0).capture_ok);
    assert!(summarize(&[], 0, &c, &r, 0).text().contains("invalid: 480"));
    c.invalid = 0;
    assert!(!accounting(&c, &r, 0).capture_ok);
}

#[test]
fn zero_padding_requires_successful_query() {
    let c = CaptureTotals::default();
    let r = RenderTotals::default();
    let invalid = LoopEvent::Render(RenderWake::default());
    assert_eq!(summarize(&[invalid], 0, &c, &r, 0).zero_padding_wakes, 0);
    let valid = LoopEvent::Render(RenderWake {
        padding_valid: true,
        ..RenderWake::default()
    });
    assert_eq!(
        summarize(&[invalid, valid], 0, &c, &r, 0).zero_padding_wakes,
        1
    );
}
