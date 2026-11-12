//! 작업자 소유권과 오디오·네트워크 조립. 캡처·수신·렌더·제어·진단 스레드.
//!
//! 단계: P2. 고정 버퍼 기반 pull 렌더 코어를 구현했다.
#![forbid(unsafe_code)]

pub mod render;
