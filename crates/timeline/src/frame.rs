//! 미디어 프레임: 전송·코덱 사이를 오가는 고정 크기 값 타입.

use crate::{ClockDomainId, SampleIndex, SampleRange, StreamKey};

/// 인라인 페이로드 최대 크기.
///
/// PCM16 모노 10ms(480샘플) = 960바이트를 담을 수 있는 크기다. Opus 프레임은
/// 이보다 훨씬 작다. 힙 할당·참조 카운팅 없이 SPSC 링으로 값 이동하기 위한 선택이며,
/// 이동 비용(약 1KB memcpy)은 P2 렌더 마감 시험에서 함께 측정한다.
pub const MAX_PAYLOAD: usize = 1024;

/// 프레임 코덱. 프레임 길이 제약은 [`Codec::accepts_sample_count`]가 검사한다.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Codec {
    /// 16비트 리틀엔디언 모노 PCM. 코덱 지연 0의 기준선.
    Pcm16,
    /// 표준 Opus(48kHz). CELT 전용 2.5/5ms 포함.
    Opus,
    /// 비표준 Opus Custom 모드. 디코더 모드가 프레임 길이에 고정되므로
    /// 길이를 바꾸려면 epoch를 올려야 한다(I7).
    OpusCustom,
}

impl Codec {
    /// 해당 코덱이 이 프레임 길이(샘플)를 표현할 수 있는지.
    #[must_use]
    pub const fn accepts_sample_count(self, n: u16) -> bool {
        match self {
            Codec::Pcm16 => n >= 1 && (n as usize) * 2 <= MAX_PAYLOAD,
            // 48kHz 표준 Opus 프레임: 2.5 / 5 / 10 / 20ms.
            Codec::Opus => matches!(n, 120 | 240 | 480 | 960),
            // opus_custom.h 주석 기준: 64~1024샘플, 소인수가 2·3·5뿐.
            // (P2에서 실제 opus_custom_mode_create 결과로 재확인할 것)
            Codec::OpusCustom => n >= 64 && n <= 1024 && only_factors_2_3_5(n),
        }
    }
}

const fn only_factors_2_3_5(mut n: u16) -> bool {
    if n == 0 {
        return false;
    }
    while n.is_multiple_of(2) {
        n /= 2;
    }
    while n.is_multiple_of(3) {
        n /= 3;
    }
    while n.is_multiple_of(5) {
        n /= 5;
    }
    n == 1
}

/// 프레임 플래그.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct FrameFlags(u8);

impl FrameFlags {
    /// 이 프레임 직전 샘플이 캡처 단계에서 유실됐다(네트워크 손실과 구분, I3).
    pub const GAP_BEFORE: FrameFlags = FrameFlags(1 << 0);

    #[must_use]
    pub const fn empty() -> Self {
        FrameFlags(0)
    }

    #[must_use]
    pub const fn contains(self, f: FrameFlags) -> bool {
        self.0 & f.0 == f.0
    }

    #[must_use]
    pub const fn with(self, f: FrameFlags) -> Self {
        FrameFlags(self.0 | f.0)
    }

    #[must_use]
    pub const fn bits(self) -> u8 {
        self.0
    }
}

/// 고정 크기 인라인 페이로드.
#[derive(Clone, Copy)]
pub struct Payload {
    len: u16,
    bytes: [u8; MAX_PAYLOAD],
}

impl Payload {
    /// 슬라이스를 복사해 만든다. 너무 길면 오류.
    pub fn from_slice(data: &[u8]) -> Result<Self, FrameError> {
        if data.len() > MAX_PAYLOAD {
            return Err(FrameError::PayloadTooLarge { len: data.len() });
        }
        let mut bytes = [0u8; MAX_PAYLOAD];
        bytes[..data.len()].copy_from_slice(data);
        Ok(Payload {
            len: data.len() as u16,
            bytes,
        })
    }

    #[must_use]
    pub fn as_slice(&self) -> &[u8] {
        &self.bytes[..self.len as usize]
    }

    #[must_use]
    pub const fn len(&self) -> usize {
        self.len as usize
    }

    #[must_use]
    pub const fn is_empty(&self) -> bool {
        self.len == 0
    }
}

impl core::fmt::Debug for Payload {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        write!(f, "Payload({} bytes)", self.len)
    }
}

/// 프레임 생성 오류.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FrameError {
    PayloadTooLarge { len: usize },
    UnsupportedSampleCount { codec: Codec, samples: u16 },
    PcmLengthMismatch { samples: u16, bytes: usize },
    RangeOverflow,
}

/// 인코딩된(또는 PCM) 미디어 프레임 하나.
///
/// 불변식: `codec.accepts_sample_count(sample_count)`가 참이고, PCM이면
/// `payload.len() == 2 * sample_count`이며, `[start, start + sample_count)`가
/// u64 범위를 넘지 않는다. 생성자 [`MediaFrame::new`]만 이 값을 만들 수 있다.
#[derive(Clone, Copy, Debug)]
pub struct MediaFrame {
    key: StreamKey,
    clock: ClockDomainId,
    range: SampleRange,
    codec: Codec,
    flags: FrameFlags,
    payload: Payload,
}

impl MediaFrame {
    pub fn new(
        key: StreamKey,
        clock: ClockDomainId,
        start: SampleIndex,
        sample_count: u16,
        codec: Codec,
        flags: FrameFlags,
        payload: Payload,
    ) -> Result<Self, FrameError> {
        if !codec.accepts_sample_count(sample_count) {
            return Err(FrameError::UnsupportedSampleCount {
                codec,
                samples: sample_count,
            });
        }
        if codec == Codec::Pcm16 && payload.len() != sample_count as usize * 2 {
            return Err(FrameError::PcmLengthMismatch {
                samples: sample_count,
                bytes: payload.len(),
            });
        }
        let range =
            SampleRange::new(start, u32::from(sample_count)).ok_or(FrameError::RangeOverflow)?;
        Ok(MediaFrame {
            key,
            clock,
            range,
            codec,
            flags,
            payload,
        })
    }

    #[must_use]
    pub const fn key(&self) -> StreamKey {
        self.key
    }
    #[must_use]
    pub const fn clock(&self) -> ClockDomainId {
        self.clock
    }
    #[must_use]
    pub const fn range(&self) -> SampleRange {
        self.range
    }
    #[must_use]
    pub const fn codec(&self) -> Codec {
        self.codec
    }
    #[must_use]
    pub const fn flags(&self) -> FrameFlags {
        self.flags
    }
    #[must_use]
    pub const fn payload(&self) -> &Payload {
        &self.payload
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Epoch, PeerId, StreamId};

    fn key() -> StreamKey {
        StreamKey {
            peer: PeerId(1),
            stream: StreamId(0),
            epoch: Epoch(0),
        }
    }

    #[test]
    fn opus_custom_accepts_capture_aligned_sizes() {
        for n in [64u16, 128, 240, 256, 480, 960, 1024] {
            assert!(Codec::OpusCustom.accepts_sample_count(n), "{n}");
        }
        for n in [63u16, 1025, 112 /* 2^4·7 */, 0] {
            assert!(!Codec::OpusCustom.accepts_sample_count(n), "{n}");
        }
    }

    #[test]
    fn standard_opus_rejects_128() {
        assert!(!Codec::Opus.accepts_sample_count(128));
        assert!(Codec::Opus.accepts_sample_count(120));
    }

    #[test]
    fn pcm_length_must_match() {
        let p = Payload::from_slice(&[0u8; 256]).unwrap();
        assert!(
            MediaFrame::new(
                key(),
                ClockDomainId(0),
                SampleIndex::ZERO,
                128,
                Codec::Pcm16,
                FrameFlags::empty(),
                p
            )
            .is_ok()
        );
        assert_eq!(
            MediaFrame::new(
                key(),
                ClockDomainId(0),
                SampleIndex::ZERO,
                120,
                Codec::Pcm16,
                FrameFlags::empty(),
                p
            )
            .unwrap_err(),
            FrameError::PcmLengthMismatch {
                samples: 120,
                bytes: 256
            }
        );
    }

    #[test]
    fn pcm_10ms_fits_inline() {
        let p = Payload::from_slice(&[0u8; 960]).unwrap();
        assert!(
            MediaFrame::new(
                key(),
                ClockDomainId(0),
                SampleIndex::ZERO,
                480,
                Codec::Pcm16,
                FrameFlags::empty(),
                p
            )
            .is_ok()
        );
        assert!(Payload::from_slice(&[0u8; MAX_PAYLOAD + 1]).is_err());
    }

    #[test]
    fn flags_compose() {
        let f = FrameFlags::empty().with(FrameFlags::GAP_BEFORE);
        assert!(f.contains(FrameFlags::GAP_BEFORE));
        assert!(!FrameFlags::empty().contains(FrameFlags::GAP_BEFORE));
    }
}
