# 명동 콜링 Native 0.4.9

지연을 우선하는 Windows 음성 통화 프로토타입입니다. WebRTC 직접 연결을 우선하고 필요하면 OCI TURN으로 중계합니다. 현재 검증 기준은 두 PC 통화입니다.

## 실행과 빌드

Windows10 2004 이상, .NET10 SDK, Node.js22 이상을 사용합니다. 저장소 루트에서 실행합니다.

```powershell
./native/run.ps1
./native/build.ps1 -Package
./native/verify.ps1
```

배포는 `native/dist/` 전체 또는 `native/dist.zip`을 사용합니다. 기존 dist/ZIP과 그 안의 사용자 진단 자료는 새 빌드 때 `.local/archive/build-*`로 보존하며 새 패키지에 섞지 않습니다. 대상 PC에는 .NET10 Desktop Runtime이 필요합니다. 의존성 복원 후에는 빌드/검사에 `-NoRestore`를 붙일 수 있습니다.
앱 주소는 `https://qlrpal.ddns.net/`, 두 사용자는 같은 방 이름으로 참가합니다. 사용자 공유기의 포트포워딩은 필요 없지만 Windows 앱 방화벽 허용은 필요할 수 있습니다.

## 현재 동작

- C# Windows Forms / SIPSorcery10.0.17 / NAudio3.0.0 / Concentus2.2.2.
- Opus48kHz 모노10ms. 기본 코덱은 기존0.4.8 설정이며 VOIP/전용 저지연64·96kbps 비교 옵션을 제공합니다.
- WASAPI 공유 이벤트 모드. 지원 시 저지연을 요청하며 불안정한 최소 입력 주기는 표준10ms로 복귀합니다.
- 기본 시작 PCM 여유40ms, 큐 상한80ms. 출력 요청40/20ms와 코덱 선택은 서로 다릅니다.
- RTT·손실·지터·재생 큐·부족 길이·장치 상태, 진단 JSON, 로컬/루프백 측정.
- 최대6명 메시 구조지만 그룹 실통화는 미검증입니다. 현재 자체 에코 제거는 없습니다.

RTT·API 추정값·루프백 값은 실제 입→귀 지연과 다릅니다. 코덱 선행 대기는 약4ms 줄었지만 두 PC 전체 루프백 개선은 일관되게 입증되지 않았습니다. 순간 부족과 큐 변동은 남아 있습니다.

## 소스 구조

| 경로 | 역할 |
|---|---|
| `VoiceNative/Audio/` | WASAPI 장치, PCM 큐, PCM 변환, Opus |
| `VoiceNative/Calling/` | 통화 인터페이스, WebRTC 엔진, 송신 큐 |
| `VoiceNative/Diagnostics/` | 계측·지연 추적·로컬 검사·진단 JSON |
| `VoiceNative/UI/` | 통화 화면 |
| `TransportProbe/Checks/` | 독립 검사와 코덱 비교 |
| `TransportProbe/Integration/` | 실제 WebRTC 전송 검사 |
| `TransportProbe/Automation/` | 두 PC 자동 통화/A-B-A 실험 |
| `server/` | 독립 Node 서버와 coturn 배포 스크립트 |
| `docs/` | 정적 분석·과거 기록·보고서 |

자동 통화 CLI는 앱에서 검증 도구로 이동했습니다. 예전 실행 파일은 `.local/archive/`에 보존합니다.

```powershell
dotnet run --project native/TransportProbe -c Release -- --opus-comparison native/.local/reports/opus-comparison.json
dotnet run --project native/TransportProbe -c Release -- --opus-ab-test https://qlrpal.ddns.net/ UNIQUE_PREFIX pc-report.json pc
./native/verify.ps1 -NoRestore -Integration
```

기본 검사는 정적 분석·회귀·서버 검사입니다. `-Integration`은 실제 네트워크 검사를 추가합니다. 마이크/스피커 검사는 자동 기본 실행에 포함하지 않습니다.

## 서버와 기록

- [서버 실행·OCI TURN 운영](server/README.md)
- [정적 분석과 Git 준비 상태](docs/STATIC-ANALYSIS.md)
- [검증 기록](docs/reports/VERIFICATION.md)
- [Opus 비교 방법](docs/reports/OPUS-COMPARISON.md)
- [두 PC A/B/A 결과](docs/reports/OPUS-AB-REPORT.md)
- [과거 개발 기록](docs/HISTORY.md)

다음 개발은 수신 시간축 버퍼/PLC입니다. 이번 정리는 오디오 파라미터와 OCI 운영 배포를 변경하지 않습니다.
