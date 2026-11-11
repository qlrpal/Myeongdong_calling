//! WASAPI 캡처·렌더와 장치 시계. P1 프로브의 기록 인터페이스를 정의한다.
//!
//! 이 파일은 플랫폼 독립 타입만 담는다. Windows 구현(windows-rs, COM)은
//! `cfg(windows)` 모듈로 이후 추가한다.
//!
//! # 시각 단위 (혼동 방지를 위해 타입으로 분리)
//! - [`QpcTicks`]: 앱이 직접 읽은 QueryPerformanceCounter 원시 틱. 주파수와 함께 기록한다.
//! - [`Hns`]: WASAPI가 돌려주는 100ns 단위 값. `GetBuffer`의 QPC 위치, `IAudioClock::GetPosition`의
//!   QPC 위치, 장치 주기(REFERENCE_TIME)가 이 단위다.
//!
//! 서로 다른 장치의 [`DevicePosition`]은 서로 다른 발진기를 따른다. 직접 빼지 않는다.

/// 앱이 읽은 QPC 원시 틱.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct QpcTicks(pub u64);

/// 100ns 단위 시각·길이 (WASAPI REFERENCE_TIME 및 QPC 위치).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct Hns(pub u64);

/// 장치 스트림 위치(프레임). 해당 장치 발진기 기준.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct DevicePosition(pub u64);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DataFlow {
    Capture,
    Render,
}

/// 프로브가 비교할 스트림 모드.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StreamMode {
    /// 공유 모드, IAudioClient3 저지연 주기.
    Shared,
    /// 공유 모드 + AUDCLNT_STREAMOPTIONS_RAW (일부 APO 우회. endpoint 효과는 남을 수 있음).
    SharedRaw,
    /// 독점 모드, 이벤트 구동.
    Exclusive,
}

/// 스레드 MMCSS 등록 방식.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MmcssTask {
    None,
    Audio,
    ProAudio,
}

/// 장치 믹스 형식.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Format {
    pub sample_rate: u32,
    pub channels: u16,
    pub bits_per_sample: u16,
    pub float: bool,
}

/// `IAudioClient3::GetSharedModeEnginePeriod` 결과(프레임 단위).
/// 요청 주기는 `fundamental`의 정수배이면서 `[min, max]` 안이어야 한다.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SharedPeriods {
    pub default_frames: u32,
    pub fundamental_frames: u32,
    pub min_frames: u32,
    pub max_frames: u32,
}

impl SharedPeriods {
    /// 이 주기를 공유 모드에서 요청할 수 있는지.
    #[must_use]
    pub const fn allows(&self, period_frames: u32) -> bool {
        self.fundamental_frames != 0
            && period_frames.is_multiple_of(self.fundamental_frames)
            && period_frames >= self.min_frames
            && period_frames <= self.max_frames
    }

    /// 허용되는 가장 짧은 주기 중 `multiple_of`의 배수(예: 코덱 프레임에 정렬하고 싶을 때).
    #[must_use]
    pub fn smallest_aligned(&self, multiple_of: u32) -> Option<u32> {
        if self.fundamental_frames == 0 || multiple_of == 0 {
            return None;
        }
        let mut p = self.min_frames.div_ceil(self.fundamental_frames) * self.fundamental_frames;
        while p <= self.max_frames {
            if p.is_multiple_of(multiple_of) {
                return Some(p);
            }
            p += self.fundamental_frames;
        }
        None
    }
}

/// `IAudioClient::GetDevicePeriod` 결과(독점 모드 기준).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct ExclusivePeriods {
    pub default: Hns,
    pub minimum: Hns,
}

/// 장치 하나의 정적 정보. 실시간 경로 밖에서만 쓰므로 힙 문자열을 허용한다.
#[derive(Clone, Debug)]
pub struct DeviceInfo {
    pub endpoint_id: String,
    pub friendly_name: String,
    pub flow: DataFlow,
    pub mix_format: Format,
    pub shared: Option<SharedPeriods>,
    pub exclusive: Option<ExclusivePeriods>,
    /// 장치 속성 RawProcessingSupported. 조회 실패는 None.
    pub raw_supported: Option<bool>,
    /// 드라이버 식별(예: "Microsoft HD Audio" vs 벤더 드라이버). 결과 표에 그대로 기록한다.
    pub driver: String,
}

/// 프로브가 여는 스트림의 요청값.
#[derive(Clone, Debug)]
pub struct StreamRequest {
    pub endpoint_id: String,
    pub flow: DataFlow,
    pub mode: StreamMode,
    /// 요청 주기(프레임). 공유 모드는 [`SharedPeriods::allows`]를 만족해야 한다.
    pub period_frames: u32,
    pub format: Format,
    pub mmcss: MmcssTask,
}

/// 실제로 열린 스트림 값. 요청과 다를 수 있으므로 반드시 따로 기록한다.
#[derive(Clone, Copy, Debug)]
pub struct StreamActual {
    pub buffer_frames: u32,
    pub period_frames: u32,
    /// `IAudioClient::GetStreamLatency`.
    pub stream_latency: Hns,
    pub qpc_frequency: u64,
    pub clock_frequency: u64,
}

/// 캡처 패킷 하나의 기록. 캡처 스레드가 진단 SPSC로 값 복사해 보낸다.
#[derive(Clone, Copy, Debug)]
pub struct CaptureRecord {
    pub seq: u64,
    /// 이벤트 대기에서 깨어난 직후.
    pub wake: QpcTicks,
    /// `GetBuffer` 반환 직후.
    pub after_get_buffer: QpcTicks,
    /// `GetBuffer`가 준 첫 프레임의 장치 위치.
    pub device_position: DevicePosition,
    /// `GetBuffer`가 준 첫 프레임의 QPC 위치(100ns).
    pub first_frame_qpc: Hns,
    pub frames: u32,
    pub flags: CaptureFlags,
    /// 패킷 처리(인코딩·송신 포함 시 그 끝)까지 마친 직후.
    pub done: QpcTicks,
}

/// AUDCLNT_BUFFERFLAGS_* 대응.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct CaptureFlags {
    pub data_discontinuity: bool,
    pub silent: bool,
    pub timestamp_error: bool,
}

/// 렌더 주기 하나의 기록.
#[derive(Clone, Copy, Debug)]
pub struct RenderRecord {
    pub seq: u64,
    pub wake: QpcTicks,
    /// 쓰기 전 `GetCurrentPadding`.
    pub padding_before: u32,
    pub frames_written: u32,
    /// `ReleaseBuffer` 반환 직후.
    pub after_release: QpcTicks,
    /// `IAudioClock::GetPosition`의 위치와 그 QPC(100ns).
    pub clock_position: u64,
    pub clock_qpc: Hns,
    /// 쓴 블록의 첫 샘플이 어느 원본 프레임에서 왔는지(합의 B9). 없으면 PLC/무음.
    pub source: Option<(mdc_timeline::StreamKey, mdc_timeline::SampleIndex)>,
    /// 렌더 처리(디코딩·믹스 포함) 소요. P2 마감 시험의 핵심 값.
    pub work: QpcTicks,
    pub deadline_missed: bool,
}

/// 실시간 스레드가 기록을 넘기는 곳. 구현은 고정 크기 SPSC여야 하며 블로킹·할당 금지.
pub trait ProbeSink {
    /// 가득 차면 false(기록 유실은 별도 카운터로 센다).
    fn push_capture(&mut self, r: CaptureRecord) -> bool;
    fn push_render(&mut self, r: RenderRecord) -> bool;
}

#[cfg(test)]
mod tests {
    use super::*;

    // 정수배·범위 규칙 검사용 가상의 장치 값.
    const DOC_EXAMPLE: SharedPeriods = SharedPeriods {
        default_frames: 448,
        fundamental_frames: 48,
        min_frames: 48,
        max_frames: 448,
    };

    #[test]
    fn shared_period_rules() {
        assert!(DOC_EXAMPLE.allows(48));
        assert!(DOC_EXAMPLE.allows(96));
        assert!(DOC_EXAMPLE.allows(432));
        assert!(!DOC_EXAMPLE.allows(100));
        assert!(!DOC_EXAMPLE.allows(480));
    }

    #[test]
    fn aligned_period_search() {
        // HDAudio 예: fundamental 128, min 128, max 480 가정 시 240 정렬은 불가
        let hda = SharedPeriods {
            default_frames: 480,
            fundamental_frames: 128,
            min_frames: 128,
            max_frames: 480,
        };
        assert_eq!(hda.smallest_aligned(240), None);
        assert_eq!(hda.smallest_aligned(128), Some(128));
        // fundamental 16이면 240 정렬 가능
        let fine = SharedPeriods {
            default_frames: 480,
            fundamental_frames: 16,
            min_frames: 128,
            max_frames: 480,
        };
        assert_eq!(fine.smallest_aligned(240), Some(240));
        assert_eq!(fine.smallest_aligned(120), Some(240));
    }
}
/// Read-only device probe records and presentation, outside realtime paths.
pub mod probe_table;
pub mod provenance;
pub mod spsc;
pub mod stream_mode;
pub mod stream_report;
#[cfg(windows)]
pub mod wasapi;
