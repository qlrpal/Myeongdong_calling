use mdc_codec::{DecodeError, Decoder, Pcm16Decoder, pcm16_encode};
use mdc_timeline::{
    ClockDomainId, Codec, Epoch, FrameError, FrameFlags, MediaFrame, Payload, PeerId, SampleIndex,
    StreamId, StreamKey,
};

fn frame(codec: Codec, samples: u16, payload: Payload) -> MediaFrame {
    MediaFrame::new(
        StreamKey {
            peer: PeerId(1),
            stream: StreamId(0),
            epoch: Epoch(0),
        },
        ClockDomainId(0),
        SampleIndex::ZERO,
        samples,
        codec,
        FrameFlags::empty(),
        payload,
    )
    .expect("valid test frame")
}

#[test]
fn pcm16_round_trip_and_errors() {
    let input = [i16::MIN, -1234, 0, 1234, i16::MAX];
    let payload = pcm16_encode(&input).expect("encodable");
    let pcm = frame(Codec::Pcm16, input.len() as u16, payload);
    let mut decoder = Pcm16Decoder;
    let mut output = [0i16; 5];
    assert_eq!(decoder.decode(&pcm, &mut output), Ok(()));
    assert_eq!(output, input);
    assert_eq!(
        decoder.decode(&pcm, &mut output[..4]),
        Err(DecodeError::OutputLengthMismatch)
    );

    let opus = frame(
        Codec::Opus,
        120,
        Payload::from_slice(&[0; 1]).expect("small payload"),
    );
    assert_eq!(
        decoder.decode(&opus, &mut [0; 120]),
        Err(DecodeError::CodecMismatch)
    );

    let bad_length = MediaFrame::new(
        StreamKey {
            peer: PeerId(1),
            stream: StreamId(0),
            epoch: Epoch(0),
        },
        ClockDomainId(0),
        SampleIndex::ZERO,
        2,
        Codec::Pcm16,
        FrameFlags::empty(),
        Payload::from_slice(&[0, 0]).expect("payload"),
    );
    assert!(matches!(
        bad_length,
        Err(FrameError::PcmLengthMismatch { .. })
    ));
}
