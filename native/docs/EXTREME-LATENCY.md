# 극저지연 실험 인계

브랜치: `codex/extreme-latency`. Windows WASAPI 공유 모드에서 짧은 패킷과 제한된 재생 여유를 비교한다. 기본 실행과 배포용 dist는 유지한다.

## 구현한 모드

| 실행 옵션 | 패킷 프레임 | 설정 재생 여유 |
| --- | --- | --- |
| opus-adaptive | Opus 2.5ms | 5~20ms 적응, 기본 보간 유지 2.5ms, 전체 연속 PLC 상한 30ms |
| opus-adaptive-5 | Opus 5ms | 5~20ms 적응, 전체 연속 PLC 상한 30ms, 출력 안전 여유 1ms |
| opus-5 | Opus 5ms | 5ms |
| opus-2.5 | Opus 2.5ms | 2.5ms |
| opus-2.5-r10 | Opus 2.5ms | 10ms |
| opus-2.5-r10-h1 | Opus 2.5ms | 10ms, 기본 보간 유지 2.5ms, 출력 안전 여유 1ms |
| opus-2.5-r5-hold | Opus 2.5ms | 5ms, 빈 큐 타임스탬프 유지 최대 2.5ms |
| opus-zero | Opus 2.5ms | 0ms |
| pcm-1 | L16 PCM 1ms | 0ms |
| pcm-2.5-r10 | L16 PCM 2.5ms | 10ms |

실행 예: `./native/run.ps1 -Experiment opus-2.5` (기본 Release).

통화 양쪽에서 같은 실험 프리셋을 선택한다. 새 후보 실행 예: `./native/run.ps1 -Experiment opus-2.5-r5-hold`. 이 후보는 큐가 비었을 때 최대 2.5ms의 보간을 삽입하되 RTP 타임스탬프를 유지해 늦게 도착한 패킷을 살린다. 렌더 스레드를 기다리게 하지 않으며 연속 PLC 전체 상한도 유지한다. 삽입한 시간만큼 스트림 위상은 늦어질 수 있다.

현재 지연 우선 교체용 프로필은 **`opus-adaptive`**다. 양쪽 모두 이 브랜치의 최신 빌드에서 `./native/run.ps1 -Experiment opus-adaptive`로 실행한다. 2.5ms 송신, 약 5ms 기준 입력, 5~20ms 수신 적응, 엔진 주기+1ms 출력 여유를 사용한다. 10ms 이상을 보내는 상대는 호환 수신 여유를 별도로 적용한다. 새 빌드 안의 기존 Opus 모드와 혼용할 수 있지만, 예전 배포 바이너리가 짧은 패킷을 지원한다는 뜻은 아니다. 기본 모드로 돌아가려면 `-Experiment baseline`을 사용한다. 기존 음향/루프백 측정은 기존 모드에서만 지원하며, 이 프로필은 전용 파이프라인 시험 도구로 계측한다.

`opus-adaptive-5`는 패킷 빈도를 약 400→200개/초로 줄이는 별도 대조 프로필이다. 실행은 `./native/run.ps1 -Experiment opus-adaptive-5`다. 고정 5ms 여유인 `opus-5`와 다르며, 2.5ms 프로필보다 항상 지연이 짧다는 뜻은 아니다. 추가 비교는 [EXTREME-066](reports/EXTREME-066.md)에 기록한다. 기본 선택과 `opus-adaptive`의 설정은 바꾸지 않았다.

패킷 저장소·송신 메타데이터의 반복 할당 감소와 송신 방식 비교는 [EXTREME-078](reports/EXTREME-078.md)에 기록했다. 직접 송신 기본값은 유지한다. 비교 도구의 JSON에서만 `InlineSend:false, DedicatedSend:true`로 전용 스레드를 시험할 수 있다. 전용 큐는 실제 보고 입력 주기에 맞춰 버스트를 수용하고 상한16프레임을 유지한다. 모드·용량·MMCSS 등록·worker 오류는 `Sender` 진단으로 확인한다. 짧은 로컬 대조는 장시간/외부 통화 성능 보장이 아니다.

PCM도 WebRTC에서 협상한 L16/SRTP를 사용한다. 별도 자체 UDP 전송은 구현하지 않았다. 실험 프리셋은 약 5ms 이상의 지원 공유 입력 주기(미지원 시 호환 입력), 최소 출력 패딩, 인코딩 후 직접 송신, 짧은 PLC 상한을 사용한다. 드라이버 절대 최소 입력 주기는 시험 도구 JSON의 `MinimumCapture: true`로만 명시적으로 요청한다. PCM 손실 구간은 무음으로 채운다.

0ms 설정은 전체 지연이 0이라는 뜻이 아니다. 장치 요청 크기와 시작 조건이 실제 큐를 결정한다. 재생 요청을 수용하기 위해 유효 큐 상한은 설정된 20ms보다 커질 수 있다. 최소 출력 모드는 부족 이벤트가 발생해도 자동으로 패딩을 늘리지 않는다.

## 검증 결과와 한계

- Release 빌드와 `verify.ps1` 정적 분석·회귀 검사 통과. 네 가지 실험 모드의 코덱, RTP 순서, 타임스탬프 순환, PLC와 큐 제한 검사 포함.
- 두 PC 최초 실험에서 모든 모드의 연결·전송·디코딩은 확인했다. 그러나 극단 모드의 버퍼 부족과 손실 보간이 커서 기본 적용할 품질은 확보하지 못했다.
- 큐 정리 위치를 재생 시점으로 수정했다. 이후 전체 재시험은 Opus 5ms 단계에서 ICE 실패하여 중단됐으며, 해당 결과로 성능을 판단하면 안 된다.
- 최신 Opus 2.5ms 단독 재시험은 양쪽 기능 검사에 통과했지만 버퍼 부족과 보간이 여전히 컸다. 시험 도구의 PASS는 연결과 유효 데이터 전송 확인이며, 연속 재생이나 지연 개선의 증명이 아니다.
- PC 입력 엔진은 실제로 10ms 주기로 동작했다. 2.5ms/1ms 패킷도 입력이 묶여 도착하므로 같은 크기의 장치 지연을 보장하지 않는다. 전체 마이크→상대 출력 지연은 이번 실험에서 측정하지 않았다.

원시 결과는 Git 제외 경로 `.local/extreme-061/`, `.local/extreme-061-r2/`에 있다. 최신 재시험 파일은 `pc-opus-r3-call.json`, `laptop-opus-r3-call.json`이다. 누적 카운터에는 준비 시간이 포함되므로 측정 구간은 첫/마지막 스냅샷의 차이로 계산해야 한다.

## 다음 작업 (미진행)

재개 후 입력 주기 대조와 PCM 실기 재검증은 [후속 보고서](reports/EXTREME-062.md)에 기록했다. 안정 모드와 동일 조건의 전체 지연·연속 재생 비교, UI 시각 검증과 게임 부하 장시간 검증은 남았다. 현재 결과로 극단 모드를 기본값으로 전환하지 않는다.

추가 큐 재현·입력 폴링 대조·짧은 유지 후보 반복 시험은 [추가 보고서](reports/EXTREME-063.md)에 기록했다. 수신 큐 재현: `TransportProbe.exe --extreme-replay INPUT.json OUTPUT.json`. 이 명령은 녹음 없이 기록된 도착·재생 순서에 합성 페이로드를 넣어 비교하며, 물리적 음성 지연을 측정하지 않는다.

직접 연결·TURN 반복 비교와 장시간 교체 검증은 [대체 후보 보고서](reports/EXTREME-064.md)를 참고한다. 최신 프로필은 현재 두 PC 1:1 환경에서 지연 우선 교체용으로 제공한다. 엄격한 출력 소진 0회 기준까지 모두 통과한 것으로 표시하지 않으며, 기존 기본값과 안정 브랜치·배포본은 유지했다.

추가 대조 [EXTREME-065](reports/EXTREME-065.md)에서는 기존/후보 모두 연속성 기준을 넘는 상태가 관측됐다. 064의 좋은 조건에서 나온 14~16ms를 현재 모든 환경에 적용하지 않는다. 빠른 여유 축소는 채택하지 않았고, 6초 대기를 유지한다.

## 최신 장치 경로와 LAN 실행

장치 버퍼 조기 반환, 첫 소프트웨어 재생 경로 사전 준비, 입력/출력 이벤트 대기 상한과 할당 감소의 대조는 [EXTREME-067](reports/EXTREME-067.md)에 기록한다. 실험 프로필은 지원 장치에서 5~10ms 공유 입력을 사용하며, 입력/출력용 작업 저장소는 재생 큐를 추가하지 않는다. JSON의 `StagedDeviceBuffers`, `PeriodWatchdog`, `RenderPeriodWatchdog`로 각 후보를 따로 비교할 수 있다. 장치 오류 시 기존 호환 경로와 실제 실행 여부를 진단에서 확인한다.

자동 시험 도구가 미디어 인터페이스 선택을 전달하지 않던 누락도 수정했다. 064~066의 ‘직접 연결’은 ICE 구분이며 순수 물리 LAN 고정을 검증한 결과가 아니다. 현재 노트북에는 같은 서브넷의 Tailscale 우선 경로가 있다. 이 두 PC 사이 LAN 통화에는 앱의 미디어 네트워크 목록에서 물리 인터페이스를 선택하거나 다음처럼 실행한다.

```powershell
# PC
./native/run.ps1 -Experiment opus-adaptive -MediaAddress 172.30.1.88
# 노트북
./native/run.ps1 -Experiment opus-adaptive -MediaAddress 172.30.1.30
```

다른 네트워크에서는 해당 기기의 현재 로컬 주소를 선택한다. 시스템 VPN/라우팅과 HTTPS 연결 관리는 바꾸지 않는다. 시험 도구도 선택적으로 마지막 IPv4 인자를 받는다.

```text
TransportProbe.exe --route-extreme-soak SERVER ROOM REPORT ROLE WAVE MODE SECONDS direct|relay [MEDIA_IPV4]
```


실험 출력은 첫 미디어 준비를 이벤트로 최대 100ms 기다린 뒤 시작한다(미도착 시 무음 시작). 정상 재생에 추가하는 고정 버퍼가 아니다. `PrimingWaitMs`와 `PrimedWithMedia`로 확인한다. 초기/실행 중 드문 출력 소진과 TURN 부족 기준 초과가 남아 있어 안정판을 대체할 검증 완료 상태는 아니다. 정확한 결과와 NIC 계측 제한은 [067 보고서](reports/EXTREME-067.md)를 참고한다.


## 068 런타임 개선과 현재 한계

[068 결과](reports/EXTREME-068.md)에서 단일 메서드 최적화 후보는 장시간 소진이 남아 제거했다. 현재 이 브랜치는 `Directory.Build.props`로 VoiceNative와 TransportProbe의 앱별 단계별 JIT를 비활성화한다. OS나 설치된 .NET 전체의 설정은 바꾸지 않는다. 초기 컴파일 비용·동적 PGO 포기와 교환되는 변경이며 기존 배포본에는 적용하지 않았다.

Git에서 빌드/실행하면 생성 runtimeconfig에 자동 반영된다. 수동 교체 시에는 DLL만 복사하지 말고 해당 실행 파일의 runtimeconfig도 함께 교체한 뒤 앱을 완전히 재시작한다. 기존 실행 프로세스에는 소급 반영되지 않는다.

세 종류의 5분 시험 및 추가 90초 시험에서 두 기기 출력 소진은 모두 0회였고 공급 최대는 약 1~2.6ms였다. 직접 LAN은 이번 명목 연속성 기준을 통과했지만, 2.5ms 및 5ms TURN은 부족/PLC/폐기 기준에 실패했다. 따라서 5ms를 기본으로 승격하거나 추가 버퍼로 덮지 않았다. 특정 RTP 쌍에서 큰 간격 증가가 OCI 소프트웨어 발신 캡처→노트북 NIC indication 사이에 관측됐으며, 특정 회선·장치 하나로 확정한 결과는 아니다.

고급 공급 계측은 TransportProbe의 실험 JSON에 `TraceRenderWork:true`로 켠다(`StagedDeviceBuffers:true` 필요). 기본 프로필은 상세 스레드 CPU 계측을 끈다. `Render.Execution.RecentWork`는 최대 128건의 느린 공급/소진 기록이며, Windows 스레드 CPU 시간이 0이라고 CPU 작업이 없다는 뜻은 아니다. 분석 도구는 `Analysis/analyze_render_work.py`, `analyze_nic_rtp.py`, `analyze_turn_boundaries.py`, `analyze_delay_segments.py`이고, 각각 `--self-test`를 제공한다. TURN 미디어의 STUN Send/Data Indication 및 ChannelData를 모두 고려한다. 부분 NIC 대응에서 중간 패킷 전체의 부재를 확정하지 않는다.

[069 경로 검증](reports/EXTREME-069.md): 서버 발신 시각을 기준으로 두 수신 경로의 공통 변동을 비교했다. 짧은 TURN 시험은 통과했지만 장시간 실패의 해결 또는 안정판 승격으로 판정하지 않는다.


[070 토폴로지·무선 경로 대조](reports/EXTREME-070.md): 기존 PC 이더넷 이후 메시 무선 백홀이 확인됐다. 2.4GHz의 지속 지연은 로컬 게이트웨이 대조에서도 재현됐고, 어댑터 재초기화 뒤 회복됐지만 AX 자체 원인으로 판정하지 않는다. 이 보고서의 RTT는 전체 음성 지연이 아니며 음성 기본값·배포본을 바꾸지 않았다.

## 079 선택적 자동 프레임 시험

```powershell
./native/run.ps1 -Experiment opus-auto-frames
./native/build.ps1 -ReadyToRun -StageOnly
```

자동 프리셋은 2.5ms로 시작하며 호환되는 상대 1명의 실제 수신 피드백으로 5/10ms를 시험하고 효과가 없으면 복귀한다. 다자·피드백 만료 시에는 현재 길이를 유지한다. 양쪽 최신 빌드를 사용한다. 기존 UI 진단 폴링이 제어를 구동하므로 별도 엔진 호스트도 GetDiagnostics를 주기적으로 호출해야 한다.

시험 도구의 기존 실험 JSON에 `ProAudioCapture:true`, `CriticalAudioPriority:true`를 추가하면 스레드별 MMCSS 대조를 할 수 있다. 자동 프레임은 2.5ms Opus 적응 여유 구성에 `AdaptiveFrames:true`를 추가한다. 이 옵션들은 기본 false이며 run.ps1의 별도 스위치는 아니다. 우선순위 성공·오류는 실행 진단을 확인한다.

ReadyToRun은 선택적 win-x64 빌드다. StageOnly는 기존 dist 대신 격리 출력 경로를 반환한다. NoRestore와 함께 사용하지 않는다. 배포 대상에는 .NET10 Desktop Runtime이 필요하다. 개선량은 측정 전이며 크기가 증가한다.

미디어 선택은 이제 전역/ULA IPv6도 지원한다. 주소의 실제 연결 가능성과 방화벽은 별도 조건이다. 선택 후보의 UDP 표시는 TURN 서버로 가는 클라이언트 구간까지 검증한 것이 아니다. 상세 조건·시험 한계는 [079 보고서](reports/EXTREME-079.md)를 참고한다.
