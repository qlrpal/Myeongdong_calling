# 명동 콜링 Native 0.6.0

지연을 우선하는 Windows 음성 통화 앱입니다. 우선순위는 지연 감소, 음질, 부가 기능 순서입니다. WebRTC 직접 연결을 우선하고 필요하면 OCI TURN으로 중계합니다. [통화 앱 사용법](docs/USER-GUIDE.md).

## 통화 화면

방 만들기·참가·방 이름 복사·나가기, 마이크 음소거, 입력·출력 장치 선택, 상대별 0~200% 볼륨·음소거를 제공합니다. 장치 선택 변경 시 같은 방에 재연결합니다. 서버 연결 종료·지속적인 ICE 실패·입력 정지 시 최대 5회 재연결하며, 나가기와 창 닫기는 재시도를 취소합니다. Windows 기본 장치 변경도 감지합니다. 고급 설정과 기존 진단 도구는 별도 탭에 보존했습니다.

서버·방·닉네임·장치 선택·마이크 음소거는 `%LOCALAPPDATA%/MyeongdongCalling/settings.json`에 저장합니다. 로컬 이벤트 로그는 같은 폴더의 `logs/app.log`에 최대 약 1MiB씩 현재·이전 파일을 유지합니다. 로그에는 IP·닉네임이 포함될 수 있고 음성과 세션 토큰은 저장하지 않습니다. 상대별 볼륨은 상대의 세션 ID가 유지되는 자동 재연결 동안 유지하며, 상대가 다시 입장하거나 자신이 통화에서 나가면 초기화합니다.

## 실행과 빌드

Windows10 2004 이상, .NET10 SDK, Node.js22 이상을 사용합니다. 저장소 루트에서 실행합니다.

```powershell
./native/run.ps1
# 현재 PC의 LAN 인터페이스를 명시할 때 (주소는 실행 환경에 맞게 선택)
./native/run.ps1 -MediaAddress 172.30.1.88
./native/build.ps1 -Package
./native/verify.ps1
```

배포는 `native/dist/` 전체 또는 `native/dist.zip`을 사용합니다. 기존 dist/ZIP과 그 안의 사용자 진단 자료는 새 빌드 때 `.local/archive/build-*`로 보존하며 새 패키지에 섞지 않습니다. 대상 PC에는 .NET10 Desktop Runtime이 필요합니다. 의존성 복원 후에는 빌드/검사에 `-NoRestore`를 붙일 수 있습니다.
앱 주소는 `https://qlrpal.ddns.net/`, 두 사용자는 같은 방 이름으로 참가합니다. 사용자 공유기의 포트포워딩은 필요 없지만 Windows 앱 방화벽 허용은 필요할 수 있습니다.

### 극저지연 실험 브랜치

`codex/extreme-latency`의 지연 우선 교체 후보는 `./native/run.ps1 -Experiment opus-adaptive`로 실행합니다. 양쪽 모두 이 브랜치의 최신 빌드를 사용하세요. 2.5ms Opus, 5~20ms 적응 여유, 공유 출력 한 주기+1ms를 사용하며 기본 실행과 기존 배포본은 그대로 유지합니다. 두 PC 1:1에서 직접 연결·TURN 장시간 비교를 수행했지만, 추가 대조에서는 연속성 기준을 넘는 상태도 관측됐습니다. 모든 환경에서 안정화됐다는 뜻은 아닙니다. [최초 대체 후보 보고서](docs/reports/EXTREME-064.md), [복구·패킷 길이 추가 대조](docs/reports/EXTREME-066.md), [실험 가이드](docs/EXTREME-LATENCY.md)를 참고하세요. `opus-adaptive-5`는 별도의 5ms 대조 프로필이며 일괄 권장값으로 승격하지 않았습니다. 기존 설정 실행은 `-Experiment baseline`입니다. 게임 부하·다자/다른 하드웨어 검증은 남아 있습니다.

## 현재 동작

- C# Windows Forms / SIPSorcery10.0.17 / NAudio3.0.0 / Concentus2.2.2.
- Opus48kHz 모노10ms. 기본은 전용 저지연64kbps VBR로 코덱 선행 대기는2.5ms입니다. 기존 자동 설정·VOIP·96kbps도 선택할 수 있습니다.64kbps는 목표값이며 기존 자동 설정보다 대역폭 사용이 늘 수 있습니다.
- 게임 소리와 통화를 함께 재생하는 WASAPI 공유 이벤트 모드를 목표로 합니다. 지원 시 저지연을 요청하며 불안정한 최소 입력 주기는 표준10ms 공유 모드로 복귀합니다. 독점 모드·ASIO는 현재 구현 범위에서 제외합니다.
- ‘통화 출력 효과 우회 우선’은 저지연 RTP 출력에서 RAW 공유 모드와 GameChat 분류를 요청합니다. 미지원 시 기존 공유 처리로 복귀합니다. 장치의 항상 켜진 효과·드라이버·하드웨어 처리까지 모두 우회한다는 의미는 아닙니다. 통화에서 나온 뒤 설정을 바꿀 수 있습니다.
- 미디어 네트워크 목록에서 로컬 IPv4 인터페이스를 선택할 수 있습니다. 기본 ‘OS 자동’은 VPN 경로를 유지합니다. LAN과 VPN의 서브넷이 겹치는 경우 양쪽에서 실제 LAN 주소를 선택하면 이 앱의 미디어 소켓만 해당 인터페이스를 사용합니다. 선택 주소가 없어지면 오류를 표시합니다.
- ‘짧은 공유 입력 주기 우선’은 장치 범위 안에서 약5ms 주기를 요청합니다. 지원하지 않거나 요청이 거절되면 기존 공유 입력으로 복귀합니다. 입력이 불안정하면 통화에서 나온 뒤 이 항목만 끌 수 있으며 출력 저지연 설정은 유지합니다. 실제 선택 주기는 장치에 따라 다릅니다.
- 기본 수신은 RTP 시간순 재생과 Opus PLC입니다. 압축 패킷 목표 여유20ms, 전체 앱 큐 상한120ms이며 장치 요청 크기에 따라 실제 큐는 더 길 수 있습니다. 기존 PCM 경로(시작40ms/상한80ms)로 복귀할 수 있습니다.
- ‘재생: 자동 저지연 (시험)’은 20ms로 시작하고 안정 구간에서 최소 15ms까지 낮추며, 변동 시 최대 30ms 범위에서 적응합니다. 통화에서 나온 뒤 선택할 수 있습니다. 기본 선택은 기존 고정 20ms 방식이며, 자동 방식은 지연 감소와 PLC 증가의 절충이 있어 게임·외부망 장시간 검증이 더 필요합니다. [구현과 비교](docs/reports/RESPONSIVE-PLAYOUT-059.md).
- 자동 저지연에는 반복 공백의 빠른 재시작을 추가했습니다. 정상·단발성 공백의 여유는 유지하고, 수신 RTP 시간 3초 안에 재버퍼링이 3회 반복될 때 첫 재생 여유만 10ms로 낮춥니다. 정상 목표는 그대로이며 진단 화면·JSON에서 실제 적용 횟수를 확인할 수 있습니다.
- 큐를 보고 샘플 소비율을 미세 조절합니다. 최대+1%/−0.2%로 과잉 대기와 클록 차이를 보정하며, 피치 보존형 시간 신축은 아닙니다. 출력 요청 크기·보정 ppm·읽기 직후 큐를 표시합니다.
- 저지연 RTP 재생에서는 지원 장치의 공유 출력 버퍼를 전체 용량 대신 ‘엔진 한 주기＋5ms’까지 공급합니다. 시작은 기존 여유를 유지하고 버퍼 소진 없이2초가 지나면 낮춥니다. 낮춘 뒤 장치 버퍼 소진을 관측하면 해당 스트림은 전체 용량으로 안전 복귀하며, 초기화·시작 실패나 미지원 형식은 기존 NAudio 공유 출력으로 복귀합니다. 진단에 목표/용량·장치 버퍼 소진·안전 복귀 횟수를 표시합니다. ‘재생: 기존 방식’은 기존 출력 경로를 사용합니다.
- 기본 ‘재생: 저지연 우선’은 소비율 보정 목표20ms, 빈 큐 에피소드당 한 번10ms 보류, 공백 뒤 최신 큐 재시작을 사용합니다. 연속 PLC60ms 상한은 유지합니다. 오래된 음성 생략량은 별도로 표시합니다.
- 통화에서 나간 뒤 ‘재생: 기존 방식’을 선택하면 이전 시작 여유 학습으로 복귀합니다. 선택 기능인 지터 적응20~40ms는 기존 방식에서만 사용할 수 있습니다.
- RTT·손실·지터·재생 큐·부족 길이·장치 상태, 진단 JSON, 로컬/루프백 측정.
- 최대6명 메시 구조지만 그룹 실통화는 미검증입니다. 현재 자체 에코 제거는 없습니다.

RTT·API 추정값·루프백 값은 실제 입→귀 지연과 다릅니다. 0.5.8 동일 호스트 수신→디지털 루프백 비교에서는 PC 약5–6ms, 노트북 약79–81ms 개선을 관측했습니다. 물리 입→귀 지연이나 모든 환경의 개선을 보장하는 수치는 아닙니다. 순간 부족과 큐 변동은 남아 있습니다.

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

- [0.5.9 저지연 Opus64 기본값·검증](docs/reports/LOW-DELAY-CODEC-059.md)
- [0.5.8 수신 경로 비교·RAW 출력·LAN 경로 선택](docs/reports/END-TO-END-PATH-058.md)
- [수신 여유20/10ms 비교·출력 이후 대기 조사](docs/reports/RECEIVE-RESERVE-058.md)
- [직접 WASAPI·장치 시계·출력 경로 원인 대조](docs/reports/OUTPUT-CLOCK-058.md)
- [DTS 출력 효과 원인 확인·A/B/A·원상 복구](docs/reports/DTS-ENDPOINT-058.md)
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
- [0.5.6 공유 입력 주기 단축 구현·검증](docs/reports/SHARED-CAPTURE-PERIOD.md)
- [Mumble·SYNCROOM 참고 요소와 적용 우선순위](docs/REFERENCE-APPLICATION.md)
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

공유 출력 구현과 실측 범위는 [0.5.7 보고서](docs/reports/BOUNDED-SHARED-RENDER-057.md)를 참고하세요. 기존 배포 ZIP은 자동으로 갱신하지 않았으며 `run.ps1`은 현재 소스를 빌드합니다.

최신 출력 경로 비교와 겹친 VPN/LAN 경로 확인은 [0.5.8 보고서](docs/reports/END-TO-END-PATH-058.md)에 정리했습니다. LAN을 명시한 최종5분 통화는 양쪽 완료했으며, 노트북 출력은 한 차례 전체 버퍼로 안전 복귀했습니다.


극저지연 실험의 최신 장치 경로 개선·지정 LAN/TURN 검증·남은 실패는 [EXTREME-067](docs/reports/EXTREME-067.md)에 기록했습니다. 실험 모드에서는 물리 미디어 인터페이스를 명시적으로 선택하세요([실행 안내](docs/EXTREME-LATENCY.md)). 아직 안정판 전체 대체 판정은 아닙니다.


최신 경로 대조는 [EXTREME-070](docs/reports/EXTREME-070.md)입니다. PC 랜선은 메시 버디에 연결돼 있어 메인 공유기까지 완전한 유선 경로가 아니었습니다. 메인 AP 직접 연결·대역·WLAN 요청·어댑터 재초기화를 분리해 검사했으며, 최종 내부 원인과 장시간 통화 개선은 확정하지 않았습니다. [EXTREME-069](docs/reports/EXTREME-069.md)에는 이전 경로 검증을 기록했습니다. 두 수신 경로의 공통 지연 변동과 짧은 TURN 통과 결과를 기록했으며 장시간 검증은 남아 있습니다. 앱 공급 개선은 [EXTREME-068](docs/reports/EXTREME-068.md)에 정리했습니다. 이 실험 브랜치의 앱별 JIT 정책 변경으로 장시간 출력 소진은 없어졌지만 TURN 연속성 기준은 계속 실패했습니다. 서버→NIC→앱의 같은 RTP 패킷 대조와 남은 발생 구간을 보고서에 기록했습니다. 안정판 전체 대체 판정은 보류합니다.


현재 구현·검증·미확정 사항·다음 작업은 [현재 상태 문서](docs/CURRENT-STATUS.md)에 정리했습니다. 장치별 접속 정보와 원본 위치를 포함하는 개인 인계 문서는 로컬 `.local/handoff/`에 보존하며 Git에 포함하지 않습니다.


재개 후 양쪽 게이트웨이 동시 대조와 NIC 캡처 결과는 [EXTREME-071](docs/reports/EXTREME-071.md)에 기록했습니다. 정확히 대응한 로컬 게이트웨이 RTT 98.77ms 중 NIC 송수신 관측 경계 사이가 98.67ms였습니다. 해당 사건은 앱 처리보다 하위 Wi-Fi 분기로 좁혔으며 드라이버·장치·무선·AP 내부 구분은 남아 있습니다. 전체 음성 지연이나 안정판 승격 판정은 아닙니다.

후속 [EXTREME-072](docs/reports/EXTREME-072.md)에서는 설정 변경 없이 NIC/NDIS와 DPC/ISR를 동시에 대응했습니다. 수십 ms 사건을 장시간 DPC/ISR 실행으로 설명하는 증거는 없었으며, NIC 내부 큐·펌웨어·무선·AP 분리는 남았습니다. 2.4GHz 단독 사건과 양쪽 경로 공통 후보를 구분했습니다. 메인 공유기 직결 유선이 불가능해 다음 물리 대조에는 대체 Wi-Fi 어댑터나 독립 AP가 필요합니다.

독립 휴대폰 AP 대조는 [EXTREME-073](docs/reports/EXTREME-073.md), 대역을 2.4GHz로 맞춘 후속 결과는 [EXTREME-075](docs/reports/EXTREME-075.md)에 기록했습니다. 핫스팟에서도 로컬 지연이 재현돼 홈 AP 단독 원인으로 설명되지 않았습니다. PC 무선 경로·공통 RF 환경·AP별 대기는 여전히 미분리이며, 최종 원인 규명이나 안정판 승격을 뜻하지 않습니다. PC는 원래 AP로 복구했습니다.

같은 메인 AP의 PC Realtek·노트북 Intel 동시 대조는 [EXTREME-076](docs/reports/EXTREME-076.md)입니다. 실제 공통 분석138.53초에서 양쪽 모두 로컬 지연과 공통·개별 후보가 관측돼 Realtek 하나로 전체 현상을 설명하기 어려워졌습니다. 무선 환경·절전·AP/클라이언트 큐 내부 분리는 남았고, 노트북은 원래 메시 SSID로 복구했습니다.

앱 송신의 전역 잠금·피어 수명 정리는 [EXTREME-077](docs/reports/EXTREME-077.md), 패킷 저장소 할당 감소와 직접/전용 송신 대조는 [EXTREME-078](docs/reports/EXTREME-078.md)에 기록했습니다. 검증된 저장소 개선은 적용했고 직접 송신 기본값은 유지합니다. 전용 송신은 비교용 옵션이며, 입→귀 지연 개선량이나 장시간 안정판 승격은 아직 확정하지 않았습니다.

### 수신 상태 기반 프레임 시험

`./native/run.ps1 -Experiment opus-auto-frames`는 호환 상대 1명의 수신 상태로 2.5/5/10ms를 시험하며 효과가 없으면 복귀합니다. 기존 기본 구성은 유지합니다. 선택적 MMCSS·ReadyToRun 빌드와 IPv6 지원의 사용 조건 및 검증 범위는 [079 보고서](docs/reports/EXTREME-079.md)를 참고하세요.
