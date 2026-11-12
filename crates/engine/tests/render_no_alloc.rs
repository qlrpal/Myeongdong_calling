use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

use mdc_audio::{RenderTarget, spsc};
use mdc_codec::{Pcm16Decoder, pcm16_encode};
use mdc_engine::render::{CycleProbe, FRAME_SAMPLES, MAX_PEERS, PeerRender, RenderCore, Stage};
use mdc_timeline::{
    ClockDomainId, Codec, Epoch, FrameFlags, MediaFrame, PeerId, SampleIndex, StreamId, StreamKey,
};

struct CountingAllocator;
static ENABLED: AtomicBool = AtomicBool::new(false);
static ALLOCATIONS: AtomicUsize = AtomicUsize::new(0);
static DEALLOCATIONS: AtomicUsize = AtomicUsize::new(0);

unsafe impl GlobalAlloc for CountingAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        if ENABLED.load(Ordering::Relaxed) {
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        if ENABLED.load(Ordering::Relaxed) {
            DEALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        unsafe { System.dealloc(ptr, layout) }
    }
}

#[global_allocator]
static ALLOCATOR: CountingAllocator = CountingAllocator;

#[test]
fn render_allocates_and_deallocates_nothing() {
    const CYCLES: usize = 600;
    let mut producers = Vec::new();
    let mut keepers = Vec::new();
    let mut peers = std::array::from_fn(|_| None);
    for (peer_index, peer) in peers.iter_mut().enumerate() {
        let (producer, consumer, keeper) = spsc::channel_with_keeper(16).expect("ring");
        producers.push(producer);
        keepers.push(keeper);
        *peer = Some(PeerRender::new(
            consumer,
            Pcm16Decoder,
            Epoch(0),
            SampleIndex::ZERO,
        ));
        assert!(peer_index < MAX_PEERS);
    }
    let mut core = RenderCore::new(peers);
    let frames: Vec<MediaFrame> = (0..CYCLES)
        .map(|index| {
            MediaFrame::new(
                StreamKey {
                    peer: PeerId(1),
                    stream: StreamId(0),
                    epoch: Epoch(0),
                },
                ClockDomainId(0),
                SampleIndex::new(
                    (index * FRAME_SAMPLES) as u64 + if index >= 300 { 64 } else { 0 },
                ),
                FRAME_SAMPLES as u16,
                Codec::Pcm16,
                FrameFlags::empty(),
                pcm16_encode(&[1; FRAME_SAMPLES]).expect("payload"),
            )
            .expect("frame")
        })
        .collect();
    let beyond_window_frames: Vec<MediaFrame> = (0..CYCLES / 50)
        .map(|index| {
            MediaFrame::new(
                StreamKey {
                    peer: PeerId(1),
                    stream: StreamId(0),
                    epoch: Epoch(0),
                },
                ClockDomainId(0),
                SampleIndex::new((index * 50 * FRAME_SAMPLES + 1_048_576) as u64),
                FRAME_SAMPLES as u16,
                Codec::Pcm16,
                FrameFlags::empty(),
                pcm16_encode(&[2; FRAME_SAMPLES]).expect("payload"),
            )
            .expect("frame")
        })
        .collect();
    let mut output = [0i16; 192];
    struct FixedProbe {
        calls: [u64; 3],
    }
    impl CycleProbe for FixedProbe {
        fn mark(&mut self, stage: Stage) {
            let index = match stage {
                Stage::AfterPop => 0,
                Stage::AfterDecode => 1,
                Stage::AfterMix => 2,
            };
            if let Some(calls) = self.calls.get_mut(index) {
                *calls = calls.saturating_add(1);
            }
        }
    }
    let mut probe = FixedProbe { calls: [0; 3] };

    ALLOCATIONS.store(0, Ordering::SeqCst);
    DEALLOCATIONS.store(0, Ordering::SeqCst);
    ENABLED.store(true, Ordering::SeqCst);
    for (cycle, frame) in frames.iter().enumerate() {
        for producer in &mut producers {
            if cycle % 50 == 0 {
                for _ in 0..8 {
                    assert!(producer.push(beyond_window_frames[cycle / 50]));
                }
            }
            assert!(producer.push(*frame));
        }
        if cycle == 300 {
            core.skip(64);
        }
        let count = [96usize, 128, 192, 96][cycle % 4];
        core.render(
            RenderTarget::i16(&mut output[..count], 1).expect("target"),
            &mut probe,
        );
    }
    ENABLED.store(false, Ordering::SeqCst);
    assert_eq!(ALLOCATIONS.load(Ordering::SeqCst), 0);
    assert_eq!(DEALLOCATIONS.load(Ordering::SeqCst), 0);
    assert_eq!(probe.calls, [CYCLES as u64; 3]);
    assert_eq!(MAX_PEERS, 5);
    for index in 0..MAX_PEERS {
        let counters = core.peer(index).expect("peer").counters();
        assert!(counters.beyond_window > 0);
        assert!(counters.skipped_samples > 0);
        assert!(counters.admitted_new > 0);
        assert!(counters.played_samples > 0);
        assert!(counters.inspect_budget_exhausted > 0);
    }
}
