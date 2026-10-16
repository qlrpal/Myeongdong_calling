# 명동 콜링 Native

지연을 우선하는 Windows 음성 통화 앱과 OCI Ubuntu 연결 관리/TURN 서버입니다. 현재 작업 버전은0.5.5입니다.

개발 우선순위는 **지연 감소 → 음질 → 부가 기능**입니다. 개인 마이크·이어폰 설정은 최적화 범위에서 제외합니다. WebRTC로 출발하되 이후 전송 구현 교체 가능성을 유지하며, 진단은 실제 통화 개선을 결정하는 데 필요한 범위로 제한합니다.

```powershell
./native/run.ps1
./native/build.ps1 -Package
./native/verify.ps1
```

- [실행·빌드·소스 구조](native/README.md)
- [0.5.5 저지연 기본 정책·5분 검증](native/docs/reports/LOW-LATENCY-055.md)
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
