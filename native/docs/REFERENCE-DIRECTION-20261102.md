# 외부 레퍼런스와 구현 방향 대조

2026-11-02. 인터넷의 공식 문서·공개 소스와 현재 작업 트리를 비교한 검토다. 새 성능 시험이나 오디오 정책 변경은 아니다. 소스 링크의 main/master는 이동하므로 아래 함수와 관측 범위를 함께 기록한다.

## 제품 레퍼런스의 우선순위

사용자 요청에 따라 제품 방향의 주 레퍼런스는 **Mumble·SYNCROOM·SonoBus**로 정한다. Microsoft·Chromium·NetEq는 API 의미와 개별 구현 검증을 위한 보조 근거다. JackTrip·Jamulus는 합주용 극저지연의 추가 비교 대상이다.

| 제품 | 공식 자료에서 확인한 요소 | 적용 방향과 한계 |
|---|---|---|
| [Mumble](https://www.mumble.info/documentation/user/audio-settings/) | WASAPI, 별도의 jitter buffer와 output delay, 독점 모드는 선택 사항 | 게임 중 통화라는 사용 조건에 가장 가까운 기준. 네트워크 수신 여유와 출력 장치 여유를 구분해 최소화한다. 설명서상의 설정만으로 실제 지연 우열을 확정하지 않는다. |
| [SYNCROOM 2](https://syncroom.yamaha.com/global/v2/play/manual/index_pc.html) | 연결 직후 지연·연결 상태·ASIO 버퍼를 고려한 수신 버퍼 최적화, Windows ASIO/공유 WASAPI/독점 WASAPI 등 지원 | 장치와 경로의 실제 조건을 함께 고려하는 초기 최적화를 참고한다. 우리는 공유 WASAPI 조건을 유지하며, ASIO 권장 환경에서의 성능을 그대로 목표 수치로 복제하지 않는다. 공개 설명서로 내부 코덱·복구 알고리즘을 단정할 수 없다. |
| [SonoBus](https://www.sonobus.net/sonobus_userguide.html) | 상대별 수신 여유 자동 증가·감소, 상대별 Opus/PCM 선택, 지연 추정에 장치의 숨은 지연이 빠질 수 있음을 명시 | P2P 다인 수신의 비교 기준. 상대별 정책과 안정 후 여유 회복을 검증하고 추정 지연과 실제 입→귀를 구분한다. PCM 전환 자체를 개선으로 간주하지 않는다. |
| [JackTrip](https://support.jacktrip.com/how-to-optimize-latency-when-using-jacktrip) | 더 작은 오디오 버퍼의 지연 이득과 장치·네트워크·CPU 요구 증가를 함께 설명 | 선택적 극저지연 후보의 한계 조건을 비교한다. 공식 예시의 개선 비율을 우리 앱 예상치로 사용하지 않는다. |

제품 기준의 후속 순서는 입력에서 추가 손실을 만드는 정책 비교 → 상대별 수신 여유의 최소 안정점·복귀 검증 → 다인 출력/송신 비용 검증이다. 이미 구현된 적응 정책을 새 기능처럼 중복 추가하지 않고 기본 경로와 선택 후보의 동작부터 대조한다. 프레임·코덱·장치 주기를 동시에 변경하지 않는다. 제품 간 지연 비교는 동일 장치·경로·측정 지점에서 실시해야 하며 이번 문서 작업에서는 실행하지 않았다.

## 보조 기술 근거

| 레퍼런스 | 확인한 내용 | 프로젝트 판단 |
|---|---|---|
| [Microsoft Low Latency Audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio) | IAudioClient3으로 지원 주기를 조회하고 작은 공유 스트림을 초기화할 수 있다. | SharedCaptureRecorder/SharedRenderPlayer의 공유 모드 방향을 유지한다. 실제 적용 주기와 실패 시 대체 경로를 확인해야 한다. |
| [Microsoft BUFFERFLAGS](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags) | DATA_DISCONTINUITY는 이전 장치 위치와의 불연속, TIMESTAMP_ERROR는 시각 불확실성을 뜻한다. | 경고만으로 실제 누락 샘플 수를 확정할 수 없다. 조립 중 PCM 전량 폐기는 API 의무가 아니라 앱 정책이다. |
| [Chromium Windows 입력 소스](https://raw.githubusercontent.com/chromium/chromium/main/media/audio/win/audio_low_latency_input_win.cc) | PullCaptureDataAndPushToSink의 GetBuffer 뒤 경고·시각 처리를 하고 기존 FIFO 잔량을 고려해 PCM 또는 무음을 Push한다. 해당 경고만으로 FIFO를 초기화하지 않는다. | WindowsAudioDevice.Capture의 무조건 부분 초기화를 비교 검증할 구체적 근거다. Chromium의 위치 차이 기반 추정과 시각 보정을 그대로 물리 손실 측정으로 복제하지 않는다. |
| [Mumble WASAPI 소스](https://raw.githubusercontent.com/mumble-voip/mumble/master/src/mumble/WASAPI.cpp) | WASAPIInput::run의 공유 입력은 GetBuffer 데이터를 addMic으로 전달한다. WASAPIOutput::run은 출력 버퍼에 mix 결과를 채운다. | 샘플 조립과 공통 출력 믹싱을 참고할 수 있다. 관측한 입력 루프에 불연속별 부분 폐기가 없다는 사실이 모든 드라이버에서 플래그를 무시해도 안전하다는 증거는 아니다. |
| [WebRTC NetEq](https://webrtc.googlesource.com/src/+/refs/heads/main/modules/audio_coding/neteq/g3doc/index.md) | RTP 입장과 재생 요구를 분리하고 적응 여유·시간 보정·PLC를 결합한다. | render-driven RtpOpusPlayoutBuffer 방향은 일치한다. 자체 구현의 품질 동등성은 별도 검증 대상이다. |
| [Jamulus 매뉴얼](https://jamulus.app/wiki/Software-Manual#jitter-buffer) | 네트워크뿐 아니라 사운드 카드 시각 변동도 여유에 영향을 준다. 작은 패킷은 부하와 끊김 가능성을 높인다. | 2.5/5ms를 무조건 기본 승격하지 않고 장치 주기와 연속성까지 비교한 기존 판단을 유지한다. |

## 가장 먼저 검증할 앱 변경

입력 경고의 관측·시각 신뢰 해제와 PCM 처리 정책을 분리한다. 동일 장치·형식에서 신선한 부분 PCM을 유지하는 후보를 기존 초기화와 비교하되, 장치 재시작·형식 변경·오래된 잔량까지 무조건 이어붙이지 않는다. 임의의 장치 위치 차이만큼 무음을 넣거나 큰 큐를 추가하는 방식도 피한다.

먼저 같은 캡처 패킷 크기·플래그·간격을 재생하여 샘플 보존량, 프레임 완성 시각, RTP 시간축, 오래된 음성 재생 여부를 비교한다. 이후 같은 프레임/수신 여유/출력 설정으로 실제 두 PC A/B/A를 수행한다. 경고가 한쪽에만 발생한 자연 시험 한 번으로 정책 효과를 확정하지 않는다. 채택 기준은 앱의 추가 폐기 감소와 함께 지연·PLC 시간·연속성이 악화되지 않는 것이다.

## 기존 결과 해석의 한계

[EXTREME-139](reports/EXTREME-139.md)의 앱 폐기30ms와 수신 PLC22.5ms는 서로 다른 측정량이다. 개별 대응된 첫20ms 사건에서 송신 전단의 간격 증가와 늦은 인증 수신을 확인했지만, 폐기 정책만으로 전체22.5ms를 설명하는 대조 실험은 아직 없다. 입력 장치 불연속 자체의 영향도 미분리다. 최초 탐색 시험의 미관측 RTP/전진 PLC 사건은 별도 원인 후보로 유지한다.

이번 검토는 기존 아키텍처를 유지하고 입력 부분 폐기 정책을 먼저 검증할 근거를 보강했다. 평상시 입→귀 지연 감소, 새 정책의 안전성, 게임·다인·외부 통화 성능은 이번에 측정하지 않았다. 제품 코드·기본값·장치·네트워크 설정은 변경하지 않았다.
