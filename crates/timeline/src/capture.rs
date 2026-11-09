//! 송신 측: WASAPI 장치 위치로부터 SampleIndex를 배정한다 (합의 B4).
//!
//! - 장치 위치 차이로 정확한 유실 길이를 알 수 있으면 그만큼 건너뛰고 GAP_BEFORE.
//! - 위치가 뒤로 가거나, 상한을 넘거나, 신뢰할 수 없으면 임의 길이를 만들지 않고
//!   새 epoch로 전환한다. 같은 장치라면 ClockDomainId는 유지된다(속도 추정 유효).

use crate::{ClockDomainId, Epoch, PeerId, SampleIndex, StreamId, StreamKey};

/// 새 epoch로 넘어간 이유.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResetReason {
    /// 장치 위치가 예상보다 뒤로 갔다.
    Backwards,
    /// 유실 길이가 허용 상한을 넘었다.
    GapTooLarge { missing: u64 },
    /// 드라이버가 위치를 신뢰할 수 없다고 보고했거나 호출자가 판단했다.
    Unreliable,
}

/// 캡처 패킷 하나의 연속성 판정.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CaptureContinuity {
    Continuous,
    /// 정확한 길이를 아는 유실.
    Gap {
        missing: u64,
    },
    /// 길이를 알 수 없음 → 새 epoch.
    Reset(ResetReason),
}

/// 장치 위치(프레임 단위) 연속성 판정기. 할당 없음.
#[derive(Clone, Copy, Debug)]
pub struct GapDetector {
    expected: Option<u64>,
    max_gap: u64,
}

impl GapDetector {
    /// `max_gap`: 정확한 공백으로 인정할 최대 유실 샘플 수.
    #[must_use]
    pub const fn new(max_gap: u64) -> Self {
        GapDetector {
            expected: None,
            max_gap,
        }
    }

    /// 다음 판정에서 처음 패킷처럼 다시 시작한다.
    pub fn reset(&mut self) {
        self.expected = None;
    }

    /// `device_position`: GetBuffer가 준 첫 프레임의 장치 위치.
    /// `frames`: 이번 패킷의 프레임 수.
    /// `reliable`: 위치를 믿을 수 있는지(예: 타임스탬프 오류 플래그가 없을 때 참).
    pub fn observe(
        &mut self,
        device_position: u64,
        frames: u32,
        reliable: bool,
    ) -> CaptureContinuity {
        let next = device_position.saturating_add(u64::from(frames));
        let prev = self.expected.replace(next);
        if !reliable {
            return CaptureContinuity::Reset(ResetReason::Unreliable);
        }
        let Some(expected) = prev else {
            return CaptureContinuity::Continuous;
        };
        match device_position.checked_sub(expected) {
            Some(0) => CaptureContinuity::Continuous,
            Some(missing) if missing <= self.max_gap => CaptureContinuity::Gap { missing },
            Some(missing) => CaptureContinuity::Reset(ResetReason::GapTooLarge { missing }),
            None => CaptureContinuity::Reset(ResetReason::Backwards),
        }
    }
}

/// 캡처 패킷의 시간축 배치 결과.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct CapturePlacement {
    pub key: StreamKey,
    pub clock: ClockDomainId,
    /// 패킷 첫 샘플의 인덱스.
    pub start: SampleIndex,
    /// 이 패킷 직전에 캡처 유실이 있었다. 이 패킷 첫 샘플을 포함하는 프레임에 GAP_BEFORE.
    pub gap_before: bool,
    /// 이 패킷에서 epoch가 바뀌었다. 프레이머는 조립 중인 샘플을 버려야 한다.
    pub epoch_changed: bool,
    pub continuity: CaptureContinuity,
}

/// 송신 스트림의 시간축 소유자. 캡처 스레드가 단독 소유한다.
#[derive(Clone, Copy, Debug)]
pub struct CaptureTimeline {
    peer: PeerId,
    stream: StreamId,
    epoch: Epoch,
    clock: ClockDomainId,
    next_index: SampleIndex,
    detector: GapDetector,
}

impl CaptureTimeline {
    #[must_use]
    pub const fn new(peer: PeerId, stream: StreamId, clock: ClockDomainId, max_gap: u64) -> Self {
        CaptureTimeline {
            peer,
            stream,
            epoch: Epoch(0),
            clock,
            next_index: SampleIndex::ZERO,
            detector: GapDetector::new(max_gap),
        }
    }

    #[must_use]
    pub const fn key(&self) -> StreamKey {
        StreamKey {
            peer: self.peer,
            stream: self.stream,
            epoch: self.epoch,
        }
    }

    /// 캡처 패킷 하나를 시간축에 배치한다.
    ///
    /// epoch를 넘어서도 인덱스 번호는 계속 증가시킨다(로그 가독성). 단, 다른 epoch의
    /// 인덱스 사이에는 오프셋 의미가 없다.
    pub fn on_packet(
        &mut self,
        device_position: u64,
        frames: u32,
        reliable: bool,
    ) -> CapturePlacement {
        let continuity = self.detector.observe(device_position, frames, reliable);
        let (start, gap_before, epoch_changed) = match continuity {
            CaptureContinuity::Continuous => (self.next_index, false, false),
            CaptureContinuity::Gap { missing } => match self.next_index.checked_add(missing) {
                Some(s) => (s, true, false),
                // 사실상 도달 불가. 도달하면 새 epoch로 처리한다.
                None => {
                    self.epoch = self.epoch.next();
                    (self.next_index, false, true)
                }
            },
            CaptureContinuity::Reset(_) => {
                self.epoch = self.epoch.next();
                (self.next_index, false, true)
            }
        };
        self.next_index = start.checked_add(u64::from(frames)).unwrap_or(start);
        CapturePlacement {
            key: self.key(),
            clock: self.clock,
            start,
            gap_before,
            epoch_changed,
            continuity,
        }
    }

    /// 캡처 장치가 바뀌었다: 새 클록 도메인, 새 epoch, 판정기 초기화.
    pub fn change_device(&mut self, clock: ClockDomainId) {
        self.clock = clock;
        self.epoch = self.epoch.next();
        self.detector.reset();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tl() -> CaptureTimeline {
        CaptureTimeline::new(PeerId(0), StreamId(0), ClockDomainId(7), 48_000)
    }

    #[test]
    fn continuous_packets_are_contiguous() {
        let mut t = tl();
        let a = t.on_packet(1000, 128, true);
        let b = t.on_packet(1128, 128, true);
        assert_eq!(a.start.get(), 0);
        assert_eq!(b.start.get(), 128);
        assert!(!b.gap_before && !b.epoch_changed);
    }

    #[test]
    fn known_gap_skips_exact_length() {
        let mut t = tl();
        t.on_packet(0, 128, true);
        let p = t.on_packet(128 + 256, 128, true);
        assert_eq!(p.continuity, CaptureContinuity::Gap { missing: 256 });
        assert_eq!(p.start.get(), 128 + 256);
        assert!(p.gap_before && !p.epoch_changed);
        assert_eq!(p.key.epoch, Epoch(0));
    }

    #[test]
    fn backwards_position_starts_new_epoch_same_clock() {
        let mut t = tl();
        t.on_packet(10_000, 128, true);
        let p = t.on_packet(500, 128, true);
        assert_eq!(
            p.continuity,
            CaptureContinuity::Reset(ResetReason::Backwards)
        );
        assert!(p.epoch_changed);
        assert_eq!(p.key.epoch, Epoch(1));
        assert_eq!(p.clock, ClockDomainId(7), "같은 장치면 클록 도메인 유지");
        // 새 epoch 이후는 다시 연속으로 판정된다.
        let q = t.on_packet(628, 128, true);
        assert_eq!(q.continuity, CaptureContinuity::Continuous);
        assert_eq!(q.start.get(), p.start.get() + 128);
    }

    #[test]
    fn oversized_gap_is_not_invented() {
        let mut t = tl();
        t.on_packet(0, 128, true);
        let p = t.on_packet(128 + 48_001, 128, true);
        assert_eq!(
            p.continuity,
            CaptureContinuity::Reset(ResetReason::GapTooLarge { missing: 48_001 })
        );
        assert!(p.epoch_changed && !p.gap_before);
    }

    #[test]
    fn unreliable_position_resets() {
        let mut t = tl();
        t.on_packet(0, 128, true);
        let p = t.on_packet(128, 128, false);
        assert_eq!(
            p.continuity,
            CaptureContinuity::Reset(ResetReason::Unreliable)
        );
        assert!(p.epoch_changed);
    }

    #[test]
    fn device_change_switches_clock_domain() {
        let mut t = tl();
        t.on_packet(0, 128, true);
        t.change_device(ClockDomainId(8));
        let p = t.on_packet(0, 128, true);
        assert_eq!(p.clock, ClockDomainId(8));
        assert_eq!(p.key.epoch, Epoch(1));
        assert_eq!(
            p.continuity,
            CaptureContinuity::Continuous,
            "새 장치의 첫 패킷"
        );
    }
}
