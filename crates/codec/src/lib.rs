//! 코덱 공통 트레이트와 PCM·표준 Opus·Opus Custom 구현. timeline의 MediaFrame을 만들고 푼다.
//!
//! 단계: P2. PCM16 인코더와 디코더를 구현했다.
#![forbid(unsafe_code)]

mod pcm;

pub use pcm::{DecodeError, Decoder, EncodeError, Pcm16Decoder, pcm16_encode};
