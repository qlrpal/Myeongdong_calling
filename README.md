# 명동 콜링 Native

지연을 우선하는 Windows 음성 통화 앱과 OCI Ubuntu 연결 관리/TURN 서버입니다. 현재 작업 버전은0.6.0입니다.

현재 브랜치 `codex/extreme-latency`는 극저지연 실험과 앱 최적화를 포함합니다. 기본 실행과 실험 구성은 구분하며 안정판 전체 대체 판정은 보류합니다. [현재 상태·검증·다음 작업](native/docs/CURRENT-STATUS.md), [극저지연 실행 안내](native/docs/EXTREME-LATENCY.md).

개발 우선순위는 **실제 지연 감소 → 게임 중 연속성 → CPU·GC·스케줄링 부담 → 음질 → 부가 기능**입니다. 개인 마이크·이어폰 설정은 최적화 범위에서 제외합니다. WebRTC로 출발하되 이후 전송 구현 교체 가능성을 유지하며, 진단은 실제 통화 개선을 결정하는 데 필요한 범위로 제한합니다.

주 사용 환경은 게임과 동시 실행입니다. 오디오는 **WASAPI 저지연 공유 모드**를 목표로 하며 게임 소리와 통화가 함께 재생되어야 합니다. 독점 모드와 ASIO 지원은 현재 구현 범위에서 제외하고, 오디오 인터페이스 없이 일반 Windows 장치에서 최적화합니다.

```powershell
./native/run.ps1
./native/run.ps1 -SharedOutput # 통화당 공통 출력 비교 후보
./native/build.ps1 -Package
./native/verify.ps1
```

- [두 호스트 3명·6명 시험](native/docs/TWO-HOST-GROUP-TEST.md)
- [최신 기준선·송신 대조·미해결 항목](native/docs/reports/EXTREME-100.md)
- [실행·빌드·소스 구조](native/README.md)
- [공통 출력 후보·기존 경로 복귀](native/docs/SHARED-OUTPUT.md)
- [통화 앱 사용법](native/docs/USER-GUIDE.md)
- [0.6.0 구현·검증](native/docs/reports/PRODUCT-060.md)
- [Git 배포 범위·명령](native/docs/GIT-RELEASE.md)
- [0.5.9 저지연 Opus64 기본값·검증](native/docs/reports/LOW-DELAY-CODEC-059.md)
- [0.5.8 실제 수신 경로 비교·RAW 출력·LAN 경로 선택](native/docs/reports/END-TO-END-PATH-058.md)
- [수신 여유20/10ms 비교·출력 이후 대기 조사](native/docs/reports/RECEIVE-RESERVE-058.md)
- [직접 WASAPI·장치 시계·출력 경로 원인 대조](native/docs/reports/OUTPUT-CLOCK-058.md)
- [DTS 출력 효과 원인 확인·A/B/A·원상 복구](native/docs/reports/DTS-ENDPOINT-058.md)
- [0.5.5 저지연 기본 정책·5분 검증](native/docs/reports/LOW-LATENCY-055.md)
- [0.5.7 공유 출력 대기 축소·안전 복귀](native/docs/reports/BOUNDED-SHARED-RENDER-057.md)
- [0.5.6 공유 입력 주기 단축 구현·검증](native/docs/reports/SHARED-CAPTURE-PERIOD.md)
- [정적 분석·Git 준비 상태](native/docs/STATIC-ANALYSIS.md)
- [서버 운영](native/server/README.md)
- [네트워크 시험 종합 보고서](native/docs/reports/NETWORK-TEST-SUMMARY.md)
- [ETW MCP·Pktmon 재시험](native/docs/reports/ETW-MCP-PKTMON-LOCALIZATION.md)
- [최근 두 PC 비교 결과](native/docs/reports/OPUS-AB-REPORT.md)
- [RTP 시간축 재생·PLC 구현과 검증](native/docs/reports/RTP-PLAYOUT.md)
- [부하·연속 통화 결과와 진단 수정](native/docs/reports/RTP-STRESS-SOAK.md)
- [소비율 보정·큐 회복 구현과 검증](native/docs/reports/PLAYOUT-CLOCK.md)
- [적응 여유 구현·고정/적응 비교](native/docs/reports/ADAPTIVE-RESERVE.md)
- [지연 발생 구간 추적](native/docs/reports/PIPELINE-LOCALIZATION.md)
- [양쪽 OS 송수신 경계 비교](native/docs/reports/DUAL-OS-LOCALIZATION.md)
- [DPC·ISR·스케줄링 동시 대조](native/docs/reports/NIC-SCHEDULING-LOCALIZATION.md)
- [양쪽 공유기 ping·WLAN 이벤트 대조](native/docs/reports/WLAN-GATEWAY-LOCALIZATION.md)
- [무통화·통화·무통화 대조](native/docs/reports/TRAFFIC-ABA-LOCALIZATION.md)
- [합성 UDP·실제 통화 순서 대조](native/docs/reports/UDP-CALL-LOCALIZATION.md)
- [실제 송신 패턴 재생·등간격 대조](native/docs/reports/UDP-PATTERN-LOCALIZATION.md)
- [무선 재시도·신호·전원 지표 대조](native/docs/reports/WLAN-RADIO-LOCALIZATION.md)
- [무선 통계 조회 꺼짐·켜짐 대조](native/docs/reports/WLAN-OBSERVER-LOCALIZATION.md)
- [통계 조회 없는 WLAN ETW·UDP 대조](native/docs/reports/WLAN-ETW-REPLAY-LOCALIZATION.md)

소스와 재현 도구는 `native/`에 있습니다. 이전 웹은 로컬 `web/`에 보존하며 이 브랜치에 추가하지 않습니다. 배포 파일·진단 원본·실험 자료·캐시·비밀 키는 Git에서 제외합니다.
