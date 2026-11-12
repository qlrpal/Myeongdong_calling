use mdc_jitter::{PlayoutSlots, SLOT_CAPACITY};
use mdc_timeline::{
    ClockDomainId, Codec, Epoch, FrameFlags, MediaFrame, Payload, PeerId, SampleIndex, StreamId,
    StreamKey,
};

fn frame(start: u64) -> MediaFrame {
    MediaFrame::new(
        StreamKey {
            peer: PeerId(1),
            stream: StreamId(0),
            epoch: Epoch(0),
        },
        ClockDomainId(0),
        SampleIndex::new(start),
        1,
        Codec::Pcm16,
        FrameFlags::empty(),
        Payload::from_slice(&[0, 0]).expect("payload"),
    )
    .expect("frame")
}

#[test]
fn fixed_slots_insert_take_discard_and_capacity() {
    let mut slots = PlayoutSlots::new();
    assert!(slots.insert(frame(10)));
    assert!(slots.insert(frame(20)));
    assert!(slots.take_starting_at(SampleIndex::new(11)).is_none());
    assert!(slots.take_starting_at(SampleIndex::new(10)).is_some());
    assert_eq!(slots.len(), 1);
    assert_eq!(slots.discard_before(SampleIndex::new(21)), 1);
    assert!(slots.is_empty());

    for i in 0..SLOT_CAPACITY {
        assert!(slots.insert(frame((i * 2) as u64)));
    }
    assert!(!slots.insert(frame(100)));
    assert_eq!(slots.len(), SLOT_CAPACITY);
}
