# 명동 콜링 Native 0.5.5

지연을 우선하는 Windows 음성 통화 프로토타입입니다. 우선순위는 지연 감소, 음질, 부가 기능 순서입니다. WebRTC 직접 연결을 우선하고 필요하면 OCI TURN으로 중계합니다. 현재 검증 기준은 두 PC 통화입니다.

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
- 기본 수신은 RTP 시간순 재생과 Opus PLC입니다. 압축 패킷 목표 여유20ms, 전체 앱 큐 상한120ms이며 장치 요청 크기에 따라 실제 큐는 더 길 수 있습니다. 기존 PCM 경로(시작40ms/상한80ms)로 복귀할 수 있습니다.
- 큐를 보고 샘플 소비율을 미세 조절합니다. 최대+1%/−0.2%로 과잉 대기와 클록 차이를 보정하며, 피치 보존형 시간 신축은 아닙니다. 출력 요청 크기·보정 ppm·읽기 직후 큐를 표시합니다.
- 기본 ‘재생: 저지연 우선’은 소비율 보정 목표20ms, 빈 큐 에피소드당 한 번10ms 보류, 공백 뒤 최신 큐 재시작을 사용합니다. 연속 PLC60ms 상한은 유지합니다. 오래된 음성 생략량은 별도로 표시합니다.
- 통화에서 나간 뒤 ‘재생: 기존 방식’을 선택하면 이전 시작 여유 학습으로 복귀합니다. 선택 기능인 지터 적응20~40ms는 기존 방식에서만 사용할 수 있습니다.
- RTT·손실·지터·재생 큐·부족 길이·장치 상태, 진단 JSON, 로컬/루프백 측정.
- 최대6명 메시 구조지만 그룹 실통화는 미검증입니다. 현재 자체 에코 제거는 없습니다.

RTT·API 추정값·루프백 값은 실제 입→귀 지연과 다릅니다. 코덱 선행 대기는 약4ms 줄었지만 두 PC 전체 루프백 개선은 일관되게 입증되지 않았습니다. 순간 부족과 큐 변동은 남아 있습니다.

## 소스 구조

| 경로 | 역할 |
|---|---|
| `VoiceNative/Audio/` | WASAPI 장치, RTP/PCM 재생 큐, PCM 변환, Opus |
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
- [네트워크 시험 종합 보고서](docs/reports/NETWORK-TEST-SUMMARY.md)
- [ETW MCP·Pktmon 재시험](docs/reports/ETW-MCP-PKTMON-LOCALIZATION.md)
- [Opus 비교 방법](docs/reports/OPUS-COMPARISON.md)
- [두 PC A/B/A 결과](docs/reports/OPUS-AB-REPORT.md)
- [RTP 시간축 재생·PLC 구현과 검증](docs/reports/RTP-PLAYOUT.md)
- [두 PC PCM/RTP/PCM 실통화 비교](docs/reports/RTP-AB-REPORT.md)
- [부하·5분 연속 통화 및 진단 수정](docs/reports/RTP-STRESS-SOAK.md)
- [소비율 보정·큐 회복 구현과 검증](docs/reports/PLAYOUT-CLOCK.md)
- [재생 목표 대기 비교·기본값 유지 판단](docs/reports/PLAYOUT-TARGET-COMPARISON.md)
- [재생 기한·시작 위상·공백 뒤 회복 조사](docs/reports/PLAYOUT-DEADLINE-RECOVERY.md)
- [0.5.5 저지연 기본 정책 적용·5분 검증](docs/reports/LOW-LATENCY-055.md)
- [적응 여유 구현·고정/적응 비교](docs/reports/ADAPTIVE-RESERVE.md)
- [지연 발생 구간 추적·첫 측정 멈춤 수정](docs/reports/PIPELINE-LOCALIZATION.md)
- [양쪽 OS 송수신 경계 비교](docs/reports/DUAL-OS-LOCALIZATION.md)
- [DPC·ISR·스케줄링 동시 대조](docs/reports/NIC-SCHEDULING-LOCALIZATION.md)
- [양쪽 공유기 ping·WLAN 이벤트 대조](docs/reports/WLAN-GATEWAY-LOCALIZATION.md)
- [무통화·통화·무통화 대조](docs/reports/TRAFFIC-ABA-LOCALIZATION.md)
- [합성 UDP·실제 통화 순서 대조](docs/reports/UDP-CALL-LOCALIZATION.md)
- [실제 송신 패턴 재생·등간격 대조](docs/reports/UDP-PATTERN-LOCALIZATION.md)
- [무선 재시도·신호·전원 지표 대조](docs/reports/WLAN-RADIO-LOCALIZATION.md)
- [무선 통계 조회 꺼짐·켜짐 대조](docs/reports/WLAN-OBSERVER-LOCALIZATION.md)
- [통계 조회 없는 WLAN ETW·UDP 대조](docs/reports/WLAN-ETW-REPLAY-LOCALIZATION.md)
- [과거 개발 기록](docs/HISTORY.md)

기본은 고정20ms로 유지합니다. 상세 추적으로 캡처 전달 약17~19ms, 수신→디코딩 약31~36ms와 송신 이후의 순간 변동을 구분했습니다. 첫 진단 신호의 지연 초기화를 오디오 콜백 밖으로 옮겼습니다. 양쪽 OS 비교에서는 큰 간격이 주로 OS 송신~상대 OS 수신 이벤트 사이에서 늘어났습니다. 후속 DPC/ISR·스케줄링 동시 시험의 긴 수신 구간에서는 동급의 긴 실행/대기가 관측되지 않았습니다. 공유기 대조에서는 큰 간격54개 중53개 주변에서 노트북→공유기 ping이20ms 이상으로 뛰어, 노트북 무선 경로/AP 처리 쪽으로 범위가 좁혀졌습니다. 스캔·절전 이벤트는 관측되지 않아 세부 원인은 확정하지 않았습니다. 다른 실행의 PC 콜백 지연도 별도 대상으로 남습니다. 수집은 `trace-network.ps1`/`trace-scheduling.ps1`/`trace-wlan.ps1`, 분석은 `TransportProbe/Analysis/`를 사용합니다. OCI 서버 변경 없이 새 클라이언트로 사용할 수 있습니다.

최신 WLAN ETW·UDP 재생 시험에서도 직접 원인을 설명하는 이벤트는 없었고, PC 유선→공유기 RTT까지 함께 늘어난 사례가 나왔습니다. 노트북 무선만으로 원인을 한정하지 않고 공유기 처리와 공통 LAN 경로도 조사 범위에 포함합니다. 위 수치들은 각 실행의 관측 결과이며 전체 통화 지연이나 단일 원인 확정값이 아닙니다.

후속 ETW MCP·Pktmon에서는 연결된 일부 PC→노트북 사례의 긴 간격이 Intel Wi-Fi NIC 첫 관측부터 존재했습니다. 이후 관측 경로는 짧았지만 자료 연결 범위가 부분적이므로 드라이버·펌웨어·무선/AP 중 직접 원인은 미확정입니다.
