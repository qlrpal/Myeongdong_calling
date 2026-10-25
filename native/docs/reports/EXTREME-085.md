# EXTREME-085: 외부 친구 없이 수행한 장시간·시계 편차·실제 SRTP 경계 대조

2026-10-23. 친구 환경 재시험은 보류하고 로컬 검증을 확장했다. 원본 진단·dist·서버·OS 설정을 변경하지 않았다. 이번 단계는 검증 도구와 분석 분류 보강이며, 추가 제품 오디오 정책을 임의로 승격하지 않았다.

## 장시간 가상 통화

`--adaptive-network-long-checks REPORT.json`을 추가했다. 각 조건300초,총49개(245분 가상 미디어 시간)를 실행한다. 실제 벽시계 통화 시간이 아니다. 기존 손실·정지·재정렬·출력 요청 대조 외에488샘플 출력에서±200ppm,10/20ms 교대 출력에서±500ppm 송수신 시계 편차를 추가했다. 피드백과 도착·출력은 정밀 가상 이벤트 시계로 진행한다. 기본 단기 명령도49개120초 조건을 사용한다.

49개가 현재 선언된 경계·정상 구간·복구 기준을 통과했다. 이 판정에는 마지막20초PLC10ms/부족10ms/폐기20ms 이하의 허용량이 있으므로 무손실/무폐기 판정이 아니다. 새 출력 기하의 초기 학습 기준도084와 동일하게 별도 평가한다.

자동 모드의 시계 편차 결과(300초 합계):

| 조건 | PLC | 복구 폐기 | 마지막20초PLC/폐기 |
| --- | ---: | ---: | ---: |
| +200ppm·488샘플 |0ms|82ms|0/5ms|
| -200ppm·488샘플 |60ms|약24.33ms|5/0ms|
| +500ppm·가변 출력 |5ms|160ms|0/10ms|
| -500ppm·가변 출력 |142.5ms|15ms|10/0ms|

±200ppm·488샘플 조건의 공급 부족은 초기 약10.17ms이며 마지막20초에는0이었다.±500ppm·가변 출력 조건의 부족은0이었다. 느린 송신 시계에서 소량의PLC,빠른 송신 시계에서 주기적 폐기가 남았다. 장시간 극저지연 시계 보정의 잔여 한계다. 실제 사용자 장치의 발진기 편차를 측정한 값이나083의 실제 끊김 원인 확정이 아니다. 현재 시계 제어의 보호대역과 아주 짧은 큐 목표를 후속 대조할 근거로 남긴다.

## 실제 WebRTC/SRTP의 관측 경계 대조

기존 실제 캡처/SRTP 반복 전환 검사 뒤에 제어된 경계 검사를 추가했다. 로컬 ICE/DTLS/SRTP로 실제 패킷을 보내고,SDK 복호화 뒤에 명시적 시험 게이트를 둔다.

- 합성 소스120프레임 중5개는 송신 API 호출 자체를 생략한다.
- 실제 복호화된115개 중5개는 수신 관측 기록 이전의 게이트에서 제외한다.
- 추가5개는 수신 기록에는 남기고 재생 버퍼 입장만 생략한다.
- 버퍼 입장 순서1쌍을 뒤집고1개를 중복 전달한다.

이는 실제 무선/인터넷 유실 주입이 아니다. 송신 API·SDK 콜백·버퍼 입장이라는 소프트웨어 관측 경계를 분석기가 구분하는지 검사한다. 페이로드나 음성을 저장하지 않는다.

분석 도구에 `seenWithoutAdmissionRecord`와 `observedWithoutQueuedAdmission`를 추가했다. 입장 기록이 없는 구형/불완전 기록은 해당 값을null로 표시한다. 거부된 입장 결과와 기록 자체의 부재를 구분한다. 앱 송신 순서와 실제RTP sequence는 다르다는 제한은 유지한다.

실행 결과는 예상대로 송신 앱 순서 공백5,수신 기록에 나타나지 않은 내부 전송5,수신 관측은 있지만 버퍼 입장 기록이 없는 패킷5,패킷 길이 불일치0이었다.전체 회귀·서버8개·IPv4/IPv6와 실제 캡처/SRTP 전환도 통과했고 빌드 경고·오류는0이었다. 분석기는 기록 양끝의 겹치지 않는 범위를 제외하고,방출된 기록이 있으면 누락 판정을 거부한다.

## 재현 명령

저장소 루트에서:

```powershell
./native/verify.ps1 -NoRestore -Integration
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --adaptive-network-long-checks native/.local/reports/adaptive-network-long.json
python native/TransportProbe/Analysis/analyze_packet_pair.py --self-test
python native/TransportProbe/Analysis/analyze_packet_pair.py native/.local/reports/packet-boundary-integration/sender.json native/.local/reports/packet-boundary-integration/receiver.json native/.local/reports/packet-boundary-integration/compared.json
```

원시 검증 로그와 장시간JSON은 Git 제외 `.local/extreme-085/`,합성 경계 fixture는`.local/reports/packet-boundary-integration/`에 있다. 외부 두PC나 실제 마이크/스피커·게임 부하를 요구하지 않는다. 실제 친구 통화 전체 해결 여부는 계속 미확정이다.
