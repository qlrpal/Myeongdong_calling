//! 수신 측: 같은 StreamKey 안에서 도착 프레임을 분류한다 (합의 B5).
//!
//! 모든 프레임은 AEAD 인증을 통과한 뒤 여기 들어오므로, 충돌·부분 겹침은 공격이 아니라
//! 송신자 버그로만 생긴다. 규칙:
//! - 같은 `(start, len)` → 중복. 먼저 받은 것을 유지한다(내용 비교 없음).
//! - 범위가 다르게 겹침 → 충돌(I2 위반). 나중 것을 버리고 오류로 센다.
//! - 재생 지점보다 앞서 끝나거나 걸친 프레임 → 늦음.
//!
//! epoch가 다른 프레임은 이 장부에 넣지 않는다. 호출자가 [`Epoch`] 비교로 먼저 거른다(I4).

use crate::{Epoch, SampleIndex, SampleRange};

/// 장부가 기억하는 최근 프레임 수. 10ms 프레임 기준 약 0.64초, 2.67ms 기준 약 0.17초.
pub const LEDGER_CAPACITY: usize = 64;

/// 도착 프레임 분류 결과.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Admission {
    New,
    Duplicate,
    /// 이미 재생 지점을 지났다(전부 또는 일부).
    Late,
    /// 기존 프레임과 범위가 다르게 겹친다. 송신자 프로토콜 위반.
    Conflict,
    /// 현재보다 오래된 epoch. 버린다.
    StaleEpoch,
    /// 현재보다 새 epoch. 호출자가 디코더·지터 기준점을 전환한 뒤 새 장부로 다시 넣는다.
    NewerEpoch,
}

/// 상대 스트림 하나의 최근 도착 기록. 렌더 스레드가 단독 소유하며 할당하지 않는다.
#[derive(Clone, Copy, Debug)]
pub struct FrameLedger {
    epoch: Epoch,
    entries: [Option<SampleRange>; LEDGER_CAPACITY],
    cursor: usize,
    played_until: Option<SampleIndex>,
}

impl FrameLedger {
    #[must_use]
    pub const fn new(epoch: Epoch) -> Self {
        FrameLedger {
            epoch,
            entries: [None; LEDGER_CAPACITY],
            cursor: 0,
            played_until: None,
        }
    }

    #[must_use]
    pub const fn epoch(&self) -> Epoch {
        self.epoch
    }

    /// 도착 프레임을 분류하고, New이면 기록한다.
    pub fn admit(&mut self, epoch: Epoch, range: SampleRange) -> Admission {
        if epoch < self.epoch {
            return Admission::StaleEpoch;
        }
        if epoch > self.epoch {
            return Admission::NewerEpoch;
        }
        if let Some(p) = self.played_until
            && range.start() < p
        {
            return Admission::Late;
        }
        for e in self.entries.iter().flatten() {
            if *e == range {
                return Admission::Duplicate;
            }
            if e.overlaps(range) {
                return Admission::Conflict;
            }
        }
        self.entries[self.cursor] = Some(range);
        self.cursor = (self.cursor + 1) % LEDGER_CAPACITY;
        Admission::New
    }

    /// 렌더가 `to` 직전까지 재생(또는 PLC로 대체)했다. 되돌아가지 않는다.
    pub fn advance_played(&mut self, to: SampleIndex) {
        self.played_until = Some(match self.played_until {
            Some(p) if p > to => p,
            _ => to,
        });
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn r(start: u64, len: u32) -> SampleRange {
        SampleRange::new(SampleIndex::new(start), len).unwrap()
    }

    #[test]
    fn duplicate_and_redundant_copies() {
        let mut l = FrameLedger::new(Epoch(0));
        assert_eq!(l.admit(Epoch(0), r(1280, 128)), Admission::New);
        assert_eq!(l.admit(Epoch(0), r(1408, 128)), Admission::New);
        // 중복 전송된 과거 프레임
        assert_eq!(l.admit(Epoch(0), r(1280, 128)), Admission::Duplicate);
        // 중복 사본이 원본보다 먼저 온 경우: 사본이 New, 원본이 Duplicate
        assert_eq!(l.admit(Epoch(0), r(1664, 128)), Admission::New);
        assert_eq!(l.admit(Epoch(0), r(1664, 128)), Admission::Duplicate);
    }

    #[test]
    fn same_start_different_length_is_conflict() {
        let mut l = FrameLedger::new(Epoch(0));
        l.admit(Epoch(0), r(0, 240));
        assert_eq!(l.admit(Epoch(0), r(0, 120)), Admission::Conflict);
        assert_eq!(
            l.admit(Epoch(0), r(120, 240)),
            Admission::Conflict,
            "부분 겹침"
        );
        assert_eq!(
            l.admit(Epoch(0), r(240, 120)),
            Admission::New,
            "경계 맞닿음은 정상"
        );
    }

    #[test]
    fn gap_then_redundant_pre_gap_frame_is_unambiguous() {
        // 합의 사례 3: 1664~1791 뒤 256샘플 캡처 유실, 다음 프레임 2048(GAP_BEFORE) + 1664 중복
        let mut l = FrameLedger::new(Epoch(0));
        assert_eq!(l.admit(Epoch(0), r(1664, 128)), Admission::New);
        assert_eq!(l.admit(Epoch(0), r(2048, 128)), Admission::New);
        assert_eq!(l.admit(Epoch(0), r(1664, 128)), Admission::Duplicate);
    }

    #[test]
    fn late_after_playout() {
        let mut l = FrameLedger::new(Epoch(0));
        l.advance_played(SampleIndex::new(2176 + 128));
        assert_eq!(l.admit(Epoch(0), r(2176, 128)), Admission::Late);
        assert_eq!(l.admit(Epoch(0), r(2304, 128)), Admission::New);
        l.advance_played(SampleIndex::new(100)); // 되돌아가지 않음
        assert_eq!(l.admit(Epoch(0), r(2176, 128)), Admission::Late);
    }

    #[test]
    fn epoch_ordering() {
        let mut l = FrameLedger::new(Epoch(5));
        assert_eq!(l.admit(Epoch(4), r(0, 128)), Admission::StaleEpoch);
        assert_eq!(l.admit(Epoch(6), r(0, 128)), Admission::NewerEpoch);
    }

    #[test]
    fn capacity_wraps_without_allocation() {
        let mut l = FrameLedger::new(Epoch(0));
        for i in 0..(LEDGER_CAPACITY as u64 * 3) {
            assert_eq!(l.admit(Epoch(0), r(i * 128, 128)), Admission::New);
        }
    }
}
