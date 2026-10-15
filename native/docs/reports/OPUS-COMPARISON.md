# 0.4.9 · Opus 모드 비교

이번 단계는 0.4.8 기준 보존과 Opus 모드 비교입니다. 기본 송신 설정은 기존 0.4.8이며, 수신 지터 버퍼·재생 큐·입출력 주기 정책은 변경하지 않았습니다. OCI 배포 변경도 없습니다.

## 확인한 인코더

설치된 SIPSorcery10.0.17의 기존 인코더를 실제 생성·인코딩한 뒤 검사용 reflection으로 확인했습니다: Concentus 관리형 `OpusEncoder`, `OPUS_APPLICATION_VOIP`, 목표54000bps, 복잡도9, VBR, lookahead312 samples=6.5ms. 제품 코드에서는 private 필드를 수정하거나 읽지 않습니다. 기존 선택은 원래 AudioEncoder로 그대로 위임합니다.

비교 선택은 Concentus2.2.2의 공개 API로 생성합니다. 목표 비트레이트와 실제 페이로드 비트레이트는 다릅니다. 모든 비교 선택은48kHz 모노,10ms, 복잡도9, VBR입니다.

| 선택 | 목표 비트레이트 | 보고된 코덱 선행 대기 | 합성 신호 정렬로 관측한 이동 |
|---|---:|---:|---:|
| 기존 0.4.8 | 라이브러리 기본54000bps | 검사용 검사6.5ms |6.42ms|
| VOIP64 |64000bps|6.5ms|6.42ms|
| 저지연64 |64000bps|2.5ms|2.50ms|
| VOIP96 |96000bps|6.5ms|6.50ms|
| 저지연96 |96000bps|2.5ms|2.50ms|

이 구성의 코덱 선행 대기 차이는 약4ms입니다. Mumble 문서에 나오는 감소량이나 전체 마이크→상대 출력 개선량으로 확대 해석하지 않습니다. 기존 54kbps와 저지연64kbps 비교는 모드와 비트레이트가 함께 바뀌므로 모드만의 비교에는 VOIP64↔저지연64 또는 VOIP96↔저지연96을 사용합니다.

## 완료한 검사

- 기존 인코더와 baseline 래퍼의20개 연속10ms 패킷이 바이트 단위로 동일함을 확인.
- 고정 합성 신호3초를 각5개 설정으로 인코딩하고 기존 디코더에서 모든 프레임480 samples로 디코딩. 정렬/코덱 대기 감소 검사2회 통과.
- 비교용4개 모드 각각 실제 WebRTC ICE/DTLS,10ms SRTP 디코딩, 양방향 진단 채널 및 RTCP 검사 통과. 반대 방향은 기존 인코더를 사용.
- 저지연64 실제 PC 마이크 약3초327프레임. 콜백 간격P95 10.60ms / 최대11.84ms. 원음 저장 없음.
- 재생 버퍼·송신 큐·RTP/RTCP 통계·측정 프레임 추적·10ms 검출 신호 회귀 검사 통과.
- Release 경고0/오류0, 최종 UI 실행과 설정 선택 영역 표시 확인.

합성 신호의 상관계수와 처리 시간은 주관적 음질 점수가 아닙니다. 실제 발화 음질, 두 PC 통화의 총 지연 개선, 외부망/부하 환경 성능은 이번 모드 비교로 입증되지 않았습니다. 기본값은 변경하지 않습니다.

## 사용자 비교 순서

1. 같은 두 PC·장치·회선·볼륨을 사용합니다. WASAPI 저지연 체크, 출력40/20ms, 노이즈 제거 등은 고정합니다. 두 PC 시계가 정확히 일치한다고 가정하지 않습니다.
2. 현재 앱에서 `Opus 기존 0.4.8 설정`으로3분 정도 통화하고 양쪽 진단 JSON을 저장합니다. 이전 0.4.8 배포도 로컬 archive에 보존했습니다.
3. 통화에서 나간 뒤 **한쪽 송신자만** `Opus VOIP · 64kbps`로 바꾸고 통화/진단을 저장합니다(A1). 수신자는 기존 설정을 유지합니다.
4. 같은 송신자만 `Opus 저지연 · 64kbps`로 바꿔 같은 길이로 통화/진단을 저장합니다(B).
5. VOIP64로 되돌려 반복합니다(A2). 반대 방향은 송신자를 바꿔 별도로 검사합니다.96kbps 비교도 동일한 방식입니다.
6. JSON의 `Encoder`와 실제 직접/TURN 경로, 장치/API 지연, 큐·부족 길이·송신 버림을 확인합니다. 경로·장치가 달라졌다면 별도 조건으로 분류합니다.
7. 통화 중 루프백 측정은 한쪽씩 반복하고 시계 범위/검출 여유를 함께 비교합니다.4ms 차이는 기존 검출 여유±20ms보다 작아 단일 루프백 결과만으로 입증할 수 없습니다. 실제 발화의 명료도·금속성·파열음도 비교해야 합니다.

WASAPI의 `공유 저지연 우선`과 Opus의 `저지연`은 서로 다른 선택입니다. 이번 비교에서는 하나씩 바꿉니다. 코덱 선택은 통화에 참가하기 전에 적용하며 통화 중 변경은 차단합니다.

## 재현 명령

```powershell
dotnet run --project native/TransportProbe -c Release -- --codec-api-inspect
dotnet run --project native/TransportProbe -c Release -- --opus-comparison native/.local/reports/opus-comparison.json
dotnet run --project native/TransportProbe -c Release -- --opus-profile-integration LowDelay64
dotnet run --project native/TransportProbe -c Release -- --opus-capture-test LowDelay64
```

코덱 선행 대기는 합산 지연 모델에 자동 가산하지 않습니다. 기존 큐·API 수치와의 중복 및 시각 기준을 검증하지 않은 상태이기 때문입니다.
이전 배포: `.local/archive/native-0.4.8-before-opus-comparison.zip`, 이전 소스: `.local/archive/native-0.4.8-source-tree.zip`.
비교 원본: `.local/reports/opus-comparison-049.json`, `opus-comparison-049-repeat.json`.
다음 구현은 RTP 시간축 수신 버퍼와 Opus PLC이며, 이번에는 적용하지 않았습니다.

2026-10-16: 노트북 SSH로 실제 양방향 A/B/A6회차·루프백30회 검출을 완료했습니다. 코덱 대기 차이는 확인됐지만 전체 지연은 일관되게 줄지 않았습니다. 상세: [두 PC 검사 보고서](OPUS-AB-REPORT.md).
