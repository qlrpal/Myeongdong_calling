//! 명동콜링 2.0 공통 미디어 시간축.
//!
//! 이 크레이트는 특정 전송 형식(자체 v3, RTP)과 무관한 미디어 의미론을 소유한다.
//! `wire-*` 크레이트는 여기 정의된 값을 패킷으로 바꾸기만 한다.
//!
//! # 시간축 의미 (합의 사항)
//!
//! - [`ClockDomainId`]: 물리 발진기(캡처 장치 인스턴스)의 식별자.
//!   같은 ID 안에서는 장기 소비율(ppm) 추정이 epoch를 넘어 유효하다.
//! - [`SampleIndex`]: 한 [`StreamKey`] 안에서의 송신 캡처 장치 샘플 카운터.
//!   캡처 단계에서 유실된 샘플도 건너뛰어 센다. **같은 StreamKey 안에서만**
//!   두 인덱스의 차이가 의미를 가진다. epoch가 다르면 같은 클록 도메인이어도
//!   오프셋 관계는 없다(속도 관계만 유지).
//! - [`Epoch`]: 디코더와 지터 기준점의 전환. 클록 도메인 변경은 반드시 epoch 변경을
//!   동반하지만, 그 역은 성립하지 않는다.
//!
//! # 실시간 제약
//!
//! 이 크레이트의 모든 타입은 힙 할당을 하지 않는다. [`MediaFrame`]은 고정 크기
//! 인라인 버퍼를 가지므로 SPSC 링으로 값 복사 이동할 수 있다.
#![cfg_attr(not(test), no_std)]
#![forbid(unsafe_code)]

mod capture;
mod frame;
mod ledger;

pub use capture::{CaptureContinuity, CapturePlacement, CaptureTimeline, GapDetector, ResetReason};
pub use frame::{Codec, FrameError, FrameFlags, MAX_PAYLOAD, MediaFrame, Payload};
pub use ledger::{Admission, FrameLedger, LEDGER_CAPACITY};

/// 엔진 전체의 명목 샘플레이트.
pub const SAMPLE_RATE: u32 = 48_000;

/// 엔진이 로컬에서 부여하는 상대 식별자. 네트워크 ID와 무관하다.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct PeerId(pub u16);

/// 한 송신자 안에서의 스트림 식별자(현재는 마이크 하나 = 스트림 하나).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct StreamId(pub u32);

/// 디코더·지터 기준점 세대. 엄격히 증가한다.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct Epoch(pub u32);

impl Epoch {
    /// 다음 epoch. u32 고갈은 현실적으로 발생하지 않으므로 포화 처리한다.
    #[must_use]
    pub const fn next(self) -> Self {
        Epoch(self.0.saturating_add(1))
    }
}

/// 물리 발진기 식별자. 장치 변경 시에만 바뀐다.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct ClockDomainId(pub u32);

/// 디코더·지터 상태의 키. 같은 키 안에서만 SampleIndex 차이가 정의된다.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct StreamKey {
    pub peer: PeerId,
    pub stream: StreamId,
    pub epoch: Epoch,
}

/// 송신 캡처 장치 샘플 카운터 (64비트, 48kHz에서 사실상 순환하지 않음).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct SampleIndex(u64);

impl SampleIndex {
    pub const ZERO: SampleIndex = SampleIndex(0);

    #[must_use]
    pub const fn new(v: u64) -> Self {
        SampleIndex(v)
    }

    #[must_use]
    pub const fn get(self) -> u64 {
        self.0
    }

    /// `n` 샘플 뒤의 인덱스. 오버플로는 None.
    #[must_use]
    pub const fn checked_add(self, n: u64) -> Option<Self> {
        match self.0.checked_add(n) {
            Some(v) => Some(SampleIndex(v)),
            None => None,
        }
    }

    /// `self`에서 `later`까지의 샘플 수. `later`가 앞서 있으면 None.
    #[must_use]
    pub const fn distance_to(self, later: SampleIndex) -> Option<u64> {
        later.0.checked_sub(self.0)
    }
}

/// 반열린 구간 `[start, start + len)`. len은 1 이상.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SampleRange {
    start: SampleIndex,
    len: u32,
}

impl SampleRange {
    /// len이 0이거나 끝이 u64를 넘으면 None.
    #[must_use]
    pub const fn new(start: SampleIndex, len: u32) -> Option<Self> {
        if len == 0 || start.0.checked_add(len as u64).is_none() {
            return None;
        }
        Some(SampleRange { start, len })
    }

    #[must_use]
    pub const fn start(self) -> SampleIndex {
        self.start
    }

    #[must_use]
    pub const fn len(self) -> u32 {
        self.len
    }

    /// 생성자가 len ≥ 1을 보장하므로 항상 false.
    #[must_use]
    pub const fn is_empty(self) -> bool {
        false
    }

    /// 배타적 끝. 생성 시 오버플로를 검사했으므로 항상 유효하다.
    #[must_use]
    pub const fn end(self) -> SampleIndex {
        SampleIndex(self.start.0 + self.len as u64)
    }

    #[must_use]
    pub const fn overlaps(self, other: SampleRange) -> bool {
        self.start.0 < other.end().0 && other.start.0 < self.end().0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn range_rejects_empty_and_overflow() {
        assert!(SampleRange::new(SampleIndex::new(0), 0).is_none());
        assert!(SampleRange::new(SampleIndex::new(u64::MAX), 1).is_none());
        assert!(SampleRange::new(SampleIndex::new(u64::MAX - 1), 1).is_some());
    }

    #[test]
    fn range_overlap_is_half_open() {
        let a = SampleRange::new(SampleIndex::new(0), 128).unwrap();
        let b = SampleRange::new(SampleIndex::new(128), 128).unwrap();
        let c = SampleRange::new(SampleIndex::new(127), 2).unwrap();
        assert!(!a.overlaps(b));
        assert!(a.overlaps(c) && b.overlaps(c));
    }

    #[test]
    fn distance_is_directional() {
        let a = SampleIndex::new(100);
        let b = SampleIndex::new(228);
        assert_eq!(a.distance_to(b), Some(128));
        assert_eq!(b.distance_to(a), None);
    }

    #[test]
    fn sample_index_does_not_wrap_in_practice() {
        // 48kHz로 1만 년 = 약 1.5e16 샘플 < u64::MAX(1.8e19)
        let ten_thousand_years = 48_000u64 * 60 * 60 * 24 * 366 * 10_000;
        assert!(SampleIndex::ZERO.checked_add(ten_thousand_years).is_some());
    }
}
