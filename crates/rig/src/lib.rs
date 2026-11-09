//! 측정 신호 생성, 외부 L/R 녹음 분석, 재시작 단위 통계.
//!
//! 단계: P0. 구현됨: 고유 코드 처프 열 생성과 48kHz PCM WAV 출력(`chirp`).
//! 템플릿·일정 API와 A단계 WAV 입력·상관·동기 검출·ID 판독을 제공한다. 통계는 아직 구현 없음.
//! 오프라인 생성기이며 실시간 오디오 경로가 아니다.
#![forbid(unsafe_code)]

mod chirp;
pub use chirp::*;
pub mod detect;
pub mod fft;
pub mod wav_in;
pub use detect::*;
pub use fft::*;
pub use wav_in::*;

pub mod analyze;
pub mod calib;
pub mod report;
