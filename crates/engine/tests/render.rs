use mdc_audio::{RenderTarget, spsc};
use mdc_codec::{Pcm16Decoder, pcm16_encode};
use mdc_engine::render::{
    CycleProbe, FRAME_SAMPLES, MAX_ADMIT_PER_CYCLE, MAX_CYCLE_FRAMES, MAX_INSPECT_PER_CYCLE,
    MAX_PEERS, NoProbe, PLAYOUT_WINDOW_SAMPLES, PeerRender, RenderCore, RenderCounters, Stage,
};
use mdc_timeline::{
    ClockDomainId, Codec, Epoch, FrameFlags, MediaFrame, PeerId, SampleIndex, StreamId, StreamKey,
};

fn media_frame(start: u64, samples: &[i16], epoch: u32) -> MediaFrame {
    MediaFrame::new(
        StreamKey {
            peer: PeerId(1),
            stream: StreamId(0),
            epoch: Epoch(epoch),
        },
        ClockDomainId(0),
        SampleIndex::new(start),
        samples.len() as u16,
        Codec::Pcm16,
        FrameFlags::empty(),
        pcm16_encode(samples).expect("PCM payload"),
    )
    .expect("valid media frame")
}

fn classified_pops(counters: RenderCounters) -> u64 {
    counters
        .admitted_new
        .saturating_add(counters.duplicate)
        .saturating_add(counters.conflict)
        .saturating_add(counters.late)
        .saturating_add(counters.stale_epoch)
        .saturating_add(counters.newer_epoch)
        .saturating_add(counters.beyond_window)
}

fn maximum_silent_run(samples: &[i16]) -> usize {
    let mut maximum = 0usize;
    let mut current = 0usize;
    for sample in samples {
        if *sample == 0 {
            current = current.saturating_add(1);
            maximum = maximum.max(current);
        } else {
            current = 0;
        }
    }
    maximum
}

fn one_peer(
    capacity: usize,
) -> (
    spsc::Producer<MediaFrame>,
    RenderCore<Pcm16Decoder>,
    spsc::Keeper<MediaFrame>,
) {
    let (producer, consumer, keeper) =
        spsc::channel_with_keeper(capacity).expect("valid power-of-two capacity");
    let mut peers = std::array::from_fn(|_| None);
    peers[0] = Some(PeerRender::new(
        consumer,
        Pcm16Decoder,
        Epoch(0),
        SampleIndex::ZERO,
    ));
    (producer, RenderCore::new(peers), keeper)
}

#[test]
fn varying_periods_preserve_continuous_samples() {
    let (mut producer, mut core, _keeper) = one_peer(16);
    let original: Vec<i16> = (0..896).map(|v| v as i16 - 400).collect();
    for (index, block) in original.chunks_exact(FRAME_SAMPLES).enumerate() {
        assert!(producer.push(media_frame((index * FRAME_SAMPLES) as u64, block, 0)));
    }
    let mut actual = Vec::new();
    for frames in [96usize, 128, 192, 480] {
        let mut output = vec![0i16; frames];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        actual.extend(output);
    }
    assert_eq!(actual, original);
    assert_eq!(
        core.peer(0).expect("peer").counters().played_samples,
        original.len() as u64
    );
}

#[test]
fn five_stream_mix_saturates() {
    let mut producers = Vec::new();
    let mut keepers = Vec::new();
    let mut peers = std::array::from_fn(|_| None);
    for peer in peers.iter_mut().take(MAX_PEERS) {
        let (mut producer, consumer, keeper) = spsc::channel_with_keeper(2).expect("ring");
        assert!(producer.push(media_frame(0, &[30_000; FRAME_SAMPLES], 0)));
        *peer = Some(PeerRender::new(
            consumer,
            Pcm16Decoder,
            Epoch(0),
            SampleIndex::ZERO,
        ));
        producers.push(producer);
        keepers.push(keeper);
    }
    let mut core = RenderCore::new(peers);
    let mut output = [0i16; FRAME_SAMPLES];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output.iter().all(|sample| *sample == i16::MAX));
    drop(producers);
    drop(keepers);
}

#[test]
fn missing_frame_is_exactly_one_silent_block() {
    let (mut producer, mut core, _keeper) = one_peer(4);
    assert!(producer.push(media_frame(0, &[11; FRAME_SAMPLES], 0)));
    assert!(producer.push(media_frame(256, &[33; FRAME_SAMPLES], 0)));
    let mut output = [0i16; FRAME_SAMPLES * 3];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output[..128].iter().all(|v| *v == 11));
    assert!(output[128..256].iter().all(|v| *v == 0));
    assert!(output[256..].iter().all(|v| *v == 33));
    assert_eq!(core.peer(0).expect("peer").counters().missing_samples, 128);
}

#[test]
fn admission_counters_are_classified() {
    let (mut producer, mut core, _keeper) = one_peer(32);
    let base = [1; FRAME_SAMPLES];
    assert!(producer.push(media_frame(896, &base, 0)));
    assert!(producer.push(media_frame(896, &base, 0)));
    assert!(producer.push(media_frame(960, &[2; 64], 0)));
    assert!(producer.push(media_frame(0, &base, 1)));
    assert!(producer.push(media_frame(128, &base, 2)));
    let mut empty: [i16; 0] = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    assert_eq!(core.peer(0).expect("peer").counters().popped, 5);
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.duplicate, 1);
    assert_eq!(counters.conflict, 1);
    assert_eq!(counters.newer_epoch, 2);
    assert_eq!(counters.popped, classified_pops(counters));

    let (mut stale_producer, stale_consumer, _stale_keeper) =
        spsc::channel_with_keeper(2).expect("ring");
    assert!(stale_producer.push(media_frame(0, &base, 0)));
    let mut stale_peers = std::array::from_fn(|_| None);
    stale_peers[0] = Some(PeerRender::new(
        stale_consumer,
        Pcm16Decoder,
        Epoch(1),
        SampleIndex::ZERO,
    ));
    let mut stale_core = RenderCore::new(stale_peers);
    stale_core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    assert_eq!(stale_core.peer(0).expect("peer").counters().stale_epoch, 1);
}

#[test]
fn late_frame_is_counted_and_never_output() {
    let (mut producer, mut core, _keeper) = one_peer(4);
    core.skip(128);
    assert!(producer.push(media_frame(0, &[9; FRAME_SAMPLES], 0)));
    let mut output = [1i16; FRAME_SAMPLES];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output.iter().all(|sample| *sample == 0));
    assert_eq!(core.peer(0).expect("peer").counters().late, 1);
}

#[test]
fn skip_resumes_inside_an_admitted_frame() {
    let (mut producer, mut core, _keeper) = one_peer(8);
    for (index, value) in [10i16, 20, 30].into_iter().enumerate() {
        assert!(producer.push(media_frame(
            (index * FRAME_SAMPLES) as u64,
            &[value; FRAME_SAMPLES],
            0,
        )));
    }
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    core.skip(64);
    let mut output = [0i16; FRAME_SAMPLES];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output[..64].iter().all(|sample| *sample == 10));
    assert!(output[64..].iter().all(|sample| *sample == 20));
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.played_samples, 128);
    assert_eq!(counters.skipped_samples, 64);

    let (mut producer, mut core, _keeper) = one_peer(8);
    for (index, value) in [10i16, 20, 30].into_iter().enumerate() {
        assert!(producer.push(media_frame(
            (index * FRAME_SAMPLES) as u64,
            &[value; FRAME_SAMPLES],
            0,
        )));
    }
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    core.skip(200);
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output[..56].iter().all(|sample| *sample == 20));
    assert!(output[56..].iter().all(|sample| *sample == 30));
    assert_eq!(core.peer(0).expect("peer").counters().skipped_samples, 200);
}

#[test]
fn skip_admits_a_ring_frame_before_advancing_played() {
    let (mut producer, mut core, _keeper) = one_peer(4);
    let samples: [i16; FRAME_SAMPLES] = std::array::from_fn(|index| index as i16);
    assert!(producer.push(media_frame(0, &samples, 0)));

    core.skip(64);
    let mut output = [0i16; 64];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );

    assert_eq!(output, samples[64..]);
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.late, 0);
    assert_eq!(counters.missing_samples, 0);
    assert_eq!(counters.skipped_samples, 64);
    assert_eq!(counters.played_samples, 64);
}

#[test]
fn skip_without_overlapping_frame_outputs_and_counts_silence() {
    let (_producer, mut core, _keeper) = one_peer(4);
    core.skip(64);
    let mut output = [1i16; FRAME_SAMPLES];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output.iter().all(|sample| *sample == 0));
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.missing_samples, 128);
    assert_eq!(counters.played_samples, 128);
    assert_eq!(counters.skipped_samples, 64);
}

#[test]
fn full_slots_leave_frames_in_ring_without_loss() {
    let (mut producer, mut core, _keeper) = one_peer(32);
    for index in 0..20 {
        assert!(producer.push(media_frame(
            index * FRAME_SAMPLES as u64,
            &[index as i16 + 1; FRAME_SAMPLES],
            0,
        )));
    }
    for expected in 1..=20i16 {
        let mut output = [0i16; FRAME_SAMPLES];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        assert!(output.iter().all(|sample| *sample == expected));
    }
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.slot_full, 0);
    assert!(counters.slots_full_stops > 0);
    assert_eq!(counters.admitted_new, 20);
    assert_eq!(counters.beyond_window, 0);
}

#[test]
fn beyond_window_frames_do_not_block_current_frames() {
    let (mut producer, mut core, _keeper) = one_peer(32);
    for index in 0..8 {
        assert!(producer.push(media_frame(
            1_048_576 + index * FRAME_SAMPLES as u64,
            &[99; FRAME_SAMPLES],
            0,
        )));
    }
    let mut empty = [];
    for _ in 0..2 {
        core.render(
            RenderTarget::i16(&mut empty, 1).expect("target"),
            &mut NoProbe,
        );
    }
    for index in 0..8 {
        assert!(producer.push(media_frame(
            index * FRAME_SAMPLES as u64,
            &[index as i16 + 1; FRAME_SAMPLES],
            0,
        )));
    }
    for expected in 1..=8i16 {
        let mut output = [0i16; FRAME_SAMPLES];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        assert!(output.iter().all(|sample| *sample == expected));
    }
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.beyond_window, 8);
    assert_eq!(counters.slot_full, 0);
}

#[test]
fn epoch_is_classified_before_playout_window() {
    let base = [1; FRAME_SAMPLES];

    let (mut stale_producer, stale_consumer, _stale_keeper) =
        spsc::channel_with_keeper(2).expect("ring");
    assert!(stale_producer.push(media_frame(1_048_576, &base, 0)));
    let mut stale_peers = std::array::from_fn(|_| None);
    stale_peers[0] = Some(PeerRender::new(
        stale_consumer,
        Pcm16Decoder,
        Epoch(1),
        SampleIndex::ZERO,
    ));
    let mut stale_core = RenderCore::new(stale_peers);
    let mut empty = [];
    stale_core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let stale = stale_core.peer(0).expect("peer").counters();
    assert_eq!(stale.stale_epoch, 1);
    assert_eq!(stale.beyond_window, 0);

    let (mut newer_producer, newer_consumer, _newer_keeper) =
        spsc::channel_with_keeper(2).expect("ring");
    assert!(newer_producer.push(media_frame(1_048_576, &base, 2)));
    let mut newer_peers = std::array::from_fn(|_| None);
    newer_peers[0] = Some(PeerRender::new(
        newer_consumer,
        Pcm16Decoder,
        Epoch(1),
        SampleIndex::ZERO,
    ));
    let mut newer_core = RenderCore::new(newer_peers);
    newer_core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let newer = newer_core.peer(0).expect("peer").counters();
    assert_eq!(newer.newer_epoch, 1);
    assert_eq!(newer.beyond_window, 0);

    let (mut current_producer, current_consumer, _current_keeper) =
        spsc::channel_with_keeper(2).expect("ring");
    assert!(current_producer.push(media_frame(1_048_576, &base, 1)));
    let mut current_peers = std::array::from_fn(|_| None);
    current_peers[0] = Some(PeerRender::new(
        current_consumer,
        Pcm16Decoder,
        Epoch(1),
        SampleIndex::ZERO,
    ));
    let mut current_core = RenderCore::new(current_peers);
    current_core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let current = current_core.peer(0).expect("peer").counters();
    assert_eq!(current.beyond_window, 1);
    assert_eq!(current.stale_epoch, 0);
    assert_eq!(current.newer_epoch, 0);
}

#[test]
fn playout_window_boundary_is_exclusive() {
    let (mut producer, mut core, _keeper) = one_peer(4);
    assert!(producer.push(media_frame(
        PLAYOUT_WINDOW_SAMPLES as u64,
        &[10; FRAME_SAMPLES],
        0,
    )));
    assert!(producer.push(media_frame(
        (PLAYOUT_WINDOW_SAMPLES - FRAME_SAMPLES) as u64,
        &[20; FRAME_SAMPLES],
        0,
    )));
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.beyond_window, 1);
    assert_eq!(counters.admitted_new, 1);
    assert_eq!(counters.slot_full, 0);
}

#[test]
fn decode_error_outputs_and_counts_one_silent_block() {
    let (mut producer, mut core, _keeper) = one_peer(4);
    assert!(producer.push(media_frame(0, &[7; 64], 0)));
    let mut output = [1i16; FRAME_SAMPLES];
    core.render(
        RenderTarget::i16(&mut output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(output.iter().all(|sample| *sample == 0));
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.missing_samples, 128);
    assert_eq!(counters.decode_errors, 1);
}

#[test]
fn targets_channels_skip_probe_and_oversize() {
    let (mut producer, mut core, _keeper) = one_peer(8);
    assert!(producer.push(media_frame(0, &[1000; FRAME_SAMPLES], 0)));
    assert!(producer.push(media_frame(128, &[2000; FRAME_SAMPLES], 0)));
    core.skip(128);

    // This allocating probe is test-only and is never used on a real-time thread.
    #[derive(Default)]
    struct Probe(Vec<Stage>);
    impl CycleProbe for Probe {
        fn mark(&mut self, stage: Stage) {
            self.0.push(stage);
        }
    }
    let mut probe = Probe::default();
    let mut i16_stereo = [0i16; FRAME_SAMPLES * 2];
    core.render(
        RenderTarget::i16(&mut i16_stereo, 2).expect("target"),
        &mut probe,
    );
    assert!(i16_stereo.chunks_exact(2).all(|v| v == [2000, 2000]));
    assert_eq!(
        probe.0,
        [Stage::AfterPop, Stage::AfterDecode, Stage::AfterMix]
    );

    let (mut producer, mut core, _keeper) = one_peer(4);
    assert!(producer.push(media_frame(0, &[16_384; FRAME_SAMPLES], 0)));
    let mut i32_output = [0i32; FRAME_SAMPLES];
    core.render(
        RenderTarget::i32(&mut i32_output, 1).expect("target"),
        &mut NoProbe,
    );
    assert!(i32_output.iter().all(|v| *v == 16_384i32 << 16));

    let (mut producer, mut core, _keeper) = one_peer(4);
    assert!(producer.push(media_frame(0, &[16_384; FRAME_SAMPLES], 0)));
    let mut f32_output = [0.0f32; FRAME_SAMPLES * 2];
    core.render(
        RenderTarget::f32(&mut f32_output, 2).expect("target"),
        &mut NoProbe,
    );
    assert!(f32_output.iter().all(|v| *v == 0.5));

    let (mut producer, mut core, _keeper) = one_peer(8);
    for index in 0..4 {
        assert!(producer.push(media_frame(
            index * FRAME_SAMPLES as u64,
            &[7; FRAME_SAMPLES],
            0
        )));
    }
    let mut oversized = vec![1i16; MAX_CYCLE_FRAMES + 1];
    core.render(
        RenderTarget::i16(&mut oversized, 1).expect("target"),
        &mut NoProbe,
    );
    assert_eq!(oversized[MAX_CYCLE_FRAMES], 0);
    assert_eq!(core.peer(0).expect("peer").cursor().get(), 4097);
    assert_eq!(core.oversize_cycles(), 1);
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.decode_errors, 0);
    assert_eq!(counters.skipped_samples, 1);
}

#[test]
fn malformed_public_targets_zero_partial_frame_and_zero_channel_output() {
    let (_producer, mut core, _keeper) = one_peer(4);
    let mut partial = [9i16; 3];
    core.render(
        RenderTarget::I16 {
            samples: &mut partial,
            channels: 2,
        },
        &mut NoProbe,
    );
    assert_eq!(partial, [0, 0, 0]);

    let mut zero_channels = [9i16; 3];
    core.render(
        RenderTarget::I16 {
            samples: &mut zero_channels,
            channels: 0,
        },
        &mut NoProbe,
    );
    assert_eq!(zero_channels, [0, 0, 0]);
}

#[test]
fn skip_preserves_straddling_fifth_frame_before_advancing_played() {
    let (mut producer, mut core, _keeper) = one_peer(8);
    for start in [128u64, 256, 384, 512] {
        assert!(producer.push(media_frame(start, &[start as i16; FRAME_SAMPLES], 0,)));
    }
    let first: [i16; FRAME_SAMPLES] = std::array::from_fn(|index| index as i16);
    assert!(producer.push(media_frame(0, &first, 0)));

    core.skip(64);
    let mut partial = [0i16; 64];
    core.render(
        RenderTarget::i16(&mut partial, 1).expect("target"),
        &mut NoProbe,
    );
    assert_eq!(partial, first[64..]);
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.late, 0);
    assert_eq!(counters.missing_samples, 0);
    assert_eq!(counters.skipped_samples, 64);
    assert_eq!(counters.played_samples, 64);

    for expected in [128i16, 256, 384, 512] {
        let mut output = [0i16; FRAME_SAMPLES];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        assert!(output.iter().all(|sample| *sample == expected));
    }
}

#[test]
fn sustained_out_of_window_burst_preserves_every_current_frame() {
    const CYCLES: usize = 300;
    let (mut producer, mut core, _keeper) = one_peer(16);
    let mut actual = Vec::with_capacity(CYCLES * FRAME_SAMPLES);
    let mut expected = Vec::with_capacity(CYCLES * FRAME_SAMPLES);
    for cycle in 0..CYCLES {
        let cursor = cycle * FRAME_SAMPLES;
        for offset in 0..4usize {
            let far = cursor
                .saturating_add(PLAYOUT_WINDOW_SAMPLES)
                .saturating_add(offset.saturating_mul(FRAME_SAMPLES));
            assert!(producer.push(media_frame(far as u64, &[30_000; FRAME_SAMPLES], 0,)));
        }
        let value = (cycle % 30_000 + 1) as i16;
        assert!(producer.push(media_frame(cursor as u64, &[value; FRAME_SAMPLES], 0,)));
        let mut output = [0i16; FRAME_SAMPLES];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        actual.extend_from_slice(&output);
        expected.extend_from_slice(&[value; FRAME_SAMPLES]);
    }
    assert_eq!(actual, expected);
    assert_eq!(maximum_silent_run(&actual), 0);
    let counters = core.peer(0).expect("peer").counters();
    assert_eq!(counters.missing_samples, 0);
    assert_eq!(counters.beyond_window, 1_200);
    assert_eq!(counters.duplicate, 0);
    assert_eq!(counters.conflict, 0);
    assert_eq!(counters.late, 0);
    assert_eq!(counters.stale_epoch, 0);
    assert_eq!(counters.newer_epoch, 0);
    assert_eq!(counters.slot_full, 0);
    assert_eq!(counters.discarded_late, 0);
    assert_eq!(counters.decode_errors, 0);
    assert_eq!(counters.inspect_budget_exhausted, 0);
    assert_eq!(counters.admit_budget_exhausted, 0);
    assert_eq!(counters.slots_full_stops, 0);
    assert_eq!(counters.popped, classified_pops(counters));
}

#[test]
fn sustained_overload_respects_inspect_bound_and_reports_quality() {
    const CYCLES: usize = 200;
    let (mut producer, mut core, _keeper) = one_peer(16);
    let mut outputs = Vec::with_capacity(CYCLES * FRAME_SAMPLES);
    let mut expected = Vec::with_capacity(CYCLES * FRAME_SAMPLES);
    let mut push_failures = 0usize;
    for cycle in 0..CYCLES {
        let cursor = cycle * FRAME_SAMPLES;
        for offset in 0..8usize {
            let far = cursor
                .saturating_add(PLAYOUT_WINDOW_SAMPLES)
                .saturating_add(offset.saturating_mul(FRAME_SAMPLES));
            if !producer.push(media_frame(far as u64, &[30_000; FRAME_SAMPLES], 0)) {
                push_failures = push_failures.saturating_add(1);
            }
        }
        let value = (cycle % 30_000 + 1) as i16;
        if !producer.push(media_frame(cursor as u64, &[value; FRAME_SAMPLES], 0)) {
            push_failures = push_failures.saturating_add(1);
        }
        expected.extend_from_slice(&[value; FRAME_SAMPLES]);
        let before = core.peer(0).expect("peer").counters().popped;
        let mut output = [0i16; FRAME_SAMPLES];
        core.render(
            RenderTarget::i16(&mut output, 1).expect("target"),
            &mut NoProbe,
        );
        let after = core.peer(0).expect("peer").counters().popped;
        assert!(after.saturating_sub(before) <= MAX_INSPECT_PER_CYCLE as u64);
        outputs.extend_from_slice(&output);
    }
    let counters = core.peer(0).expect("peer").counters();
    assert!(counters.inspect_budget_exhausted > 0);
    assert_eq!(counters.popped, classified_pops(counters));
    let preserved = outputs
        .iter()
        .zip(expected.iter())
        .filter(|(actual, wanted)| actual == wanted)
        .count();
    let maximum_silence = maximum_silent_run(&outputs);
    // Bursts enqueue eight far frames before the current frame. The fixed 8-inspection budget and
    // 16-slot ring therefore produce these exact 200-cycle loss and silence totals.
    assert_eq!(preserved, 0);
    assert_eq!(maximum_silence, 2_048);
    assert_eq!(push_failures, 1_221);
    assert_eq!(counters.beyond_window, 113);
    assert_eq!(counters.inspect_budget_exhausted, 52);
}

#[test]
fn skip_and_render_share_admit_budget_until_render_resets_it() {
    let (mut producer, mut core, _keeper) = one_peer(16);
    for index in 0..12usize {
        assert!(producer.push(media_frame(
            (index * FRAME_SAMPLES) as u64,
            &[index as i16 + 1; FRAME_SAMPLES],
            0,
        )));
    }
    core.skip(0);
    core.skip(0);
    core.skip(0);
    let before_cycle = RenderCounters::default();
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let after_render = core.peer(0).expect("peer").counters();
    assert_eq!(after_render.popped - before_cycle.popped, 6);
    assert_eq!(after_render.admitted_new - before_cycle.admitted_new, 6);
    assert_eq!(
        after_render.admit_budget_exhausted - before_cycle.admit_budget_exhausted,
        1
    );
}

#[test]
fn skip_and_render_share_inspect_budget_until_render_resets_it() {
    let (mut producer, mut core, _keeper) = one_peer(16);
    for index in 0..12usize {
        assert!(producer.push(media_frame(
            PLAYOUT_WINDOW_SAMPLES as u64 + (index * FRAME_SAMPLES) as u64,
            &[1; FRAME_SAMPLES],
            0,
        )));
    }
    let before = core.peer(0).expect("peer").counters();
    core.skip(0);
    core.skip(0);
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let first = core.peer(0).expect("peer").counters();
    assert_eq!(first.popped - before.popped, 8);
    assert_eq!(first.beyond_window - before.beyond_window, 8);
    assert_eq!(
        first.inspect_budget_exhausted - before.inspect_budget_exhausted,
        1
    );
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let second = core.peer(0).expect("peer").counters();
    assert_eq!(second.popped - first.popped, 4);
    assert_eq!(second.beyond_window - first.beyond_window, 4);
}

#[test]
fn budget_bounds_hold_across_call_orders_and_sparse_peers() {
    let (mut producer, mut core, _keeper) = one_peer(16);
    for index in 0..12usize {
        assert!(producer.push(media_frame(
            PLAYOUT_WINDOW_SAMPLES as u64 + (index * FRAME_SAMPLES) as u64,
            &[1; FRAME_SAMPLES],
            0,
        )));
    }
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let before_second = core.peer(0).expect("peer").counters();
    core.skip(0);
    core.skip(0);
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let after_second = core.peer(0).expect("peer").counters();
    assert!(after_second.popped - before_second.popped <= MAX_INSPECT_PER_CYCLE as u64);

    let (mut producer, mut skip_only_core, _keeper) = one_peer(16);
    for index in 0..12usize {
        assert!(producer.push(media_frame(
            PLAYOUT_WINDOW_SAMPLES as u64 + (index * FRAME_SAMPLES) as u64,
            &[1; FRAME_SAMPLES],
            0,
        )));
    }
    let before_skips = skip_only_core.peer(0).expect("peer").counters();
    for _ in 0..5 {
        skip_only_core.skip(0);
    }
    let after_skips = skip_only_core.peer(0).expect("peer").counters();
    assert!(after_skips.popped - before_skips.popped <= MAX_INSPECT_PER_CYCLE as u64);

    let (mut producer0, consumer0, _keeper0) = spsc::channel_with_keeper(16).expect("ring");
    let (mut producer3, consumer3, _keeper3) = spsc::channel_with_keeper(16).expect("ring");
    for index in 0..12usize {
        let frame = media_frame(
            PLAYOUT_WINDOW_SAMPLES as u64 + (index * FRAME_SAMPLES) as u64,
            &[1; FRAME_SAMPLES],
            0,
        );
        assert!(producer0.push(frame));
        assert!(producer3.push(frame));
    }
    let mut peers = std::array::from_fn(|_| None);
    peers[0] = Some(PeerRender::new(
        consumer0,
        Pcm16Decoder,
        Epoch(0),
        SampleIndex::ZERO,
    ));
    peers[3] = Some(PeerRender::new(
        consumer3,
        Pcm16Decoder,
        Epoch(0),
        SampleIndex::ZERO,
    ));
    let mut sparse_core = RenderCore::new(peers);
    let before0 = sparse_core.peer(0).expect("peer").counters();
    let before3 = sparse_core.peer(3).expect("peer").counters();
    sparse_core.skip(0);
    sparse_core.skip(0);
    sparse_core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let after0 = sparse_core.peer(0).expect("peer").counters();
    let after3 = sparse_core.peer(3).expect("peer").counters();
    assert!(after0.popped - before0.popped <= MAX_INSPECT_PER_CYCLE as u64);
    assert!(after3.popped - before3.popped <= MAX_INSPECT_PER_CYCLE as u64);
}

#[test]
fn admit_budget_leaves_uninspected_frames_for_next_cycle() {
    let (mut producer, mut core, _keeper) = one_peer(16);
    for index in 0..8usize {
        assert!(producer.push(media_frame(
            (index * FRAME_SAMPLES) as u64,
            &[index as i16 + 1; FRAME_SAMPLES],
            0,
        )));
    }
    let mut empty = [];
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let first = core.peer(0).expect("peer").counters();
    assert_eq!(first.admitted_new, MAX_ADMIT_PER_CYCLE as u64);
    assert_eq!(first.admit_budget_exhausted, 1);
    assert_eq!(first.popped, MAX_ADMIT_PER_CYCLE as u64);
    core.render(
        RenderTarget::i16(&mut empty, 1).expect("target"),
        &mut NoProbe,
    );
    let second = core.peer(0).expect("peer").counters();
    assert_eq!(second.admitted_new, 8);
    assert_eq!(second.popped, 8);
}
