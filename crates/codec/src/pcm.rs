use mdc_timeline::{Codec, MAX_PAYLOAD, MediaFrame, Payload};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DecodeError {
    CodecMismatch,
    PayloadLengthMismatch,
    OutputLengthMismatch,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EncodeError {
    TooManySamples,
}

pub trait Decoder {
    fn decode(&mut self, frame: &MediaFrame, out: &mut [i16]) -> Result<(), DecodeError>;
}

#[derive(Clone, Copy, Debug, Default)]
pub struct Pcm16Decoder;

impl Decoder for Pcm16Decoder {
    fn decode(&mut self, frame: &MediaFrame, out: &mut [i16]) -> Result<(), DecodeError> {
        if frame.codec() != Codec::Pcm16 {
            return Err(DecodeError::CodecMismatch);
        }
        let sample_count = frame.range().len() as usize;
        if out.len() != sample_count {
            return Err(DecodeError::OutputLengthMismatch);
        }
        let payload = frame.payload().as_slice();
        if payload.len() != sample_count.saturating_mul(2) {
            return Err(DecodeError::PayloadLengthMismatch);
        }
        for (sample, bytes) in out.iter_mut().zip(payload.chunks_exact(2)) {
            let Some(lo) = bytes.first() else {
                return Err(DecodeError::PayloadLengthMismatch);
            };
            let Some(hi) = bytes.get(1) else {
                return Err(DecodeError::PayloadLengthMismatch);
            };
            *sample = i16::from_le_bytes([*lo, *hi]);
        }
        Ok(())
    }
}

pub fn pcm16_encode(samples: &[i16]) -> Result<Payload, EncodeError> {
    if samples.len() > MAX_PAYLOAD / 2 {
        return Err(EncodeError::TooManySamples);
    }
    let mut bytes = [0u8; MAX_PAYLOAD];
    for (sample, chunk) in samples.iter().zip(bytes.chunks_exact_mut(2)) {
        chunk.copy_from_slice(&sample.to_le_bytes());
    }
    Payload::from_slice(&bytes[..samples.len().saturating_mul(2)])
        .map_err(|_| EncodeError::TooManySamples)
}
