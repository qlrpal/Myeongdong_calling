//! 상대별 수신 프레임 정렬, 재생 마감, 폐기 정책. 렌더 스레드가 소유한다.
//!
//! 단계: P2. 고정 용량 재생 슬롯을 구현했다.
#![forbid(unsafe_code)]

mod slots;

pub use slots::{PlayoutSlots, SLOT_CAPACITY};
