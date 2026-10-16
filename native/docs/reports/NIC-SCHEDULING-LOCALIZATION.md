# DPC·ISR·스케줄링 대조 — 2026-10-16

**이번 통화의 큰 수신 간격에서는 긴 DPC/ISR 실행이나 통화 프로세스의 긴 runnable 대기가 동반되지 않았다.** 따라서 CPU를 오래 점유한 드라이버 실행으로40~70ms 간격을 설명할 근거는 약해졌다. NIC/펌웨어/드라이버 내부 큐 대기, 무선 스캔·절전, 공유기와 링크 지연은 아직 제외하지 못한다.

## 수집과 품질

PC 시각12:37:19~12:39:20 KST, 두 PC 약120초 통화. PC 유선/노트북 Wi-Fi, 직접 LAN, 고정20ms 여유/10ms Opus 설정 유지, 신호 주입 없음.

각 PC에서170초간 두 세션을 동시에 수집했다.

- WPR: `ProcessThread`, `Loader`, `DPC`, `Interrupt`, `CSwitch`, `ReadyThread`. 스택·주기적 CPU 샘플·파일/레지스트리 I/O·힙 수집은 추가하지 않았다. 프로세스/스레드 및 모듈 경로 메타데이터가 포함된다.
- 별도 ETW: 이전에 검증한 UDP/TCP 네트워크 메타데이터. 패킷 바이트와 음성은 저장하지 않는다.

처음 WPR에 네트워크 이벤트 제공자까지 함께 넣은 짧은 검증은 저장 실패로 제외했다. 최종 프로필은 커널 스케줄링 전용이고 네트워크는 별도 세션이다.10초 검증에서 실제 DPC/ISR·모듈 매핑과 저장 성공을 확인한 뒤 실통화를 진행했다.

| 항목 | PC | 노트북 |
|---|---:|---:|
| 스케줄링 ETL | 480,247,808바이트 | 142,606,336바이트 |
| 네트워크 ETL | 10,747,904바이트 | 23,855,104바이트 |
| 스케줄링 손실 이벤트/버퍼 | 0/0 | 0/0 |
| 네트워크 손실 이벤트 | 0 | 0 |
| WPR 종료 코드 | 0 | 0 |
| 앱 보고서 오류/기록 초과 | 없음/0 | 없음/0 |

네트워크 ETL은32MiB 순환 상한에 도달하지 않았다. 원본·추출 CSV·분석 JSON은 Git 제외 `.local/scheduling-054/`에 보존했다. CSV 추출물은 원본보다 커서 수GB 공간을 사용할 수 있다. 프로파일링 자체가 시간에 영향을 줄 수 있으므로 이전 실행과 사건 수가 다르다는 이유로 성능 개선/회귀라고 주장하지 않는다.

## 방법

네트워크는 `tracerpt -rts`와 기존 raw-QPC 분석기를 사용했다. 스케줄링은 Windows Performance Toolkit의 `xperf`에서 개별 DPC/ISR 및 thread 이벤트를 `-add_rawdata`로 추출했다. 원시 QPC와 주파수를 사용해 같은 호스트 앱 기록에 연결했고 UTC나 서로 다른 PC의 절대 시각을 빼지 않았다.

통화 시작5초·끝1초를 제외했다. DPC/ISR 실행 구간은 이벤트 종료 시각과 elapsed 값으로 구성했다. 중첩된 ISR/DPC를 CPU별 interval union으로 합쳐 이중 계산을 피했다. 다른 CPU의 동시 실행 시간을 한 CPU가 막힌 시간처럼 더하지 않았다.

실행 대기는 해당 통화 PID의 `ReadyThread`→`CSwitch` 및 runnable 상태로 선점된 thread→재실행 구간을 측정했다. 네트워크를 기다리며 blocked 상태였던 시간은 CPU 실행 대기로 계산하지 않았다. 이 수치는 managed 작업 큐 대기나 아직 실행 요청 자체가 안 된 DPC의 큐 대기를 포함하지 않는다.

양쪽 추적 시작부에 전체 통화보다 긴 elapsed를 가진 ISR 각1개가 있어 유효하지 않은 값으로 제외했다. 해당 이벤트는 통화 분석 구간 이전이었다. 이것을 긴 드라이버 실행의 증거로 쓰지 않았다.

## 전체 분석 구간

단위ms. Ready 대기는 관측된 통화 프로세스 thread의 runnable 대기다.

| 항목 | PC | 노트북 |
|---|---:|---:|
| DPC 분석 이벤트 | 417,165개 | 188,665개 |
| DPC 중앙값/P95/최대 | 0.004/0.055/0.681 | 0.006/0.074/1.465 |
| ISR 분석 이벤트 | 285,860개 | 163,399개 |
| ISR 중앙값/P95/최대 | 0.005/0.081/0.262 | 0.005/0.070/0.150 |
| runnable 대기 측정 | 91,530개 | 92,674개 |
| runnable 대기 중앙값/P95/최대 | 0.006/0.016/7.746 | 0.014/0.084/1.073 |

양쪽에서 전체 구간의 가장 긴 개별 실행 모듈은 `tcpip.sys`였지만, 그 최대 시간만으로 통화 간격의 원인이라고 할 수 없다. 실제 긴 수신 간격과 겹치는 구간을 별도로 확인했다. GPU/프레임워크/네트워크 모듈의 누적 실행량 순위도 인과관계 증거로 쓰지 않았다.

## 큰 간격과의 동시 비교

이번 실행의30ms 초과 수신 간격은 PC→노트북7개, 노트북→PC9개였다. 양쪽 OS 이벤트 비교에서는15개가 송신 OS~수신 OS 사이 추가 변동,1개가 혼합으로 분류됐다.

| 대표 수신 구간 | 수신 간격 | 해당 구간 개별 DPC/ISR 최대 | 가장 바쁜 CPU의 union 실행 합 | 최장 연속 union 실행 | 통화 thread runnable 대기 최대 |
|---|---:|---:|---:|---:|---:|
| PC 수신 최대 | 67.866ms | 0.122ms | 5.126ms | 0.127ms | 0.028ms |
| 노트북 수신 최대 | 69.691ms | 0.088ms | 2.831ms | 0.088ms | 0.102ms |

각 호스트의 모든 큰 수신 구간을 통틀어 본 최대치는 다음과 같다.

| 항목 | PC | 노트북 |
|---|---:|---:|
| 개별 DPC/ISR | 0.142ms | 0.144ms |
| 한 CPU union 실행 합 | 5.126ms | 3.454ms |
| 연속 union 실행 | 0.151ms | 0.144ms |
| 통화 thread runnable 대기 | 0.190ms | 0.230ms |

이 값들에서는40~70ms 동안 CPU를 계속 막은 DPC/ISR이나 통화 thread의 동급 실행 대기가 관측되지 않았다. 전체 분석 구간의 PC7.746ms 대기는 큰 수신 간격과 겹친 최대값이 아니다. 해당 드라이버의 CPU 실행 시간이 짧다고 NIC/드라이버 전체를 정상 판정한 것은 아니다.

기존 WLAN Operational 로그도 시험 시간대에 읽었으나 양쪽에서 해당 시간대 이벤트가 반환되지 않았다. Operational 로그에 모든 스캔이 기록되는 것은 아니므로 “스캔 없음”으로 해석하지 않는다. 드라이버는 PC Realtek 유선1125.26.50.2025/Realtek Wi-Fi6001.16.172.0, 노트북 Intel AX201 23.160.0.4로 읽혔다. 버전만으로 업데이트 필요성이나 원인을 판단하지 않았다.

## 다음 범위

남은 경로는 NIC/드라이버 큐 및 장치 펌웨어, 무선 스캔·절전·혼잡, 공유기 처리 등이다. DPC가 큐에 들어간 뒤 실행될 때까지의 시간과 NIC 하드웨어 송수신 시각은 이번 수집으로 측정하지 않았다. SMI/펌웨어 지연도 별도 계측 없이는 제외할 수 없다.

노트북에는 유선 포트/어댑터가 없어 유선 대조는 진행하지 않는다. 다음 유의미한 검사는 양쪽에서 공유기까지의 동시 ping 및 WLAN 스캔/전원 상태 메타데이터를 맞춰 보는 것이다. 근거 없이 드라이버나 무선 절전 설정을 바꾸지 않았다. 앱의 상시 재생 큐 최적화는 이 순간 변동 원인 조사와 별개다.

## 도구와 검증

- 수집: [trace-scheduling.ps1](../../trace-scheduling.ps1), [VoiceScheduling.wprp](../../TransportProbe/Profiles/VoiceScheduling.wprp). 고유 instance name만 사용하며 실패 시 자신의 세션만 정리한다.
- 분석: [analyze_scheduling.py](../../TransportProbe/Analysis/analyze_scheduling.py). 누락/손실·불충분한 DPC/ISR 또는 target PID 스케줄링 자료를 거부한다.
- 프로필 구성 근거: Microsoft의 [SystemProvider 정의](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/systemprovider)와 [system keyword 목록](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/keyword--in-systemprovider-).

```powershell
# 관리자 PowerShell, 양쪽에서 상세 통화 기록과 함께 실행
./native/trace-scheduling.ps1 -Seconds 170 -OutputPath native/.local/reports/host.etl
# Windows Performance Toolkit 설치된 분석 PC에서
xperf -i HOST.etl -o HOST-dpc.csv -a dumper -provider "{ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}" -add_rawdata
xperf -i HOST.etl -o HOST-thread.csv -a dumper -provider "{3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c}" -add_rawdata
xperf -i HOST.etl -o HOST-stats.txt -a tracestats
python native/TransportProbe/Analysis/analyze_scheduling.py APP_JSON DPC_RAW_CSV THREAD_RAW_CSV UDP_JSON XPERF_STATS OUTPUT_JSON
python native/TransportProbe/Analysis/analyze_scheduling.py --self-test
```

실제 양쪽 수집·저장·정상 종료·손실0개를 검증했다. 분석 self-test는 중첩 ISR/DPC union, raw QPC 단위, 정확한 PID, runnable/선점 대기와 blocked sleep의 구분을 확인한다. 이번 단계에서 앱 오디오 알고리즘은 변경하지 않았다.
