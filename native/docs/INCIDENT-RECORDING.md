# 자동 문제 기록

일반 통화에서도 진단 JSON의 `incident`에 통화당 첫 사건 주변의 메타데이터를 포함한다. 별도 관리자 권한·녹음·파일 자동 저장·OS 설정 변경은 없다. 기존 수동 `패킷 경로 기록 (30초)`은 별도이며 자동 기록이 전체 pipeline 추적을 상시 켜지는 않는다.

## 사용

1. 최신 소스를 `./native/run.ps1`로 실행한다. 기존 공개0.6.1-beta.1 ZIP에는 이 기능이 없다.
2. 통화 중 상세 진단의 `자동 문제 기록` 상태를 확인한다.
3. 문제 뒤2초 이상 통화를 유지하고 양쪽에서 `진단 저장`을 사용한다. 더 일찍 저장하면 사건 뒤 기록이 일부만 포함된다.
4. `incident.Trigger`, `TriggerAt`, `State`, `EndReason`, `Records`를 확인한다. 자동 기록은 **그 통화의 첫 사건만** 보존한다. 이후 더 심한 사건을 기록하려면 새 통화를 시작하거나 기존 수동30초 기록을 사용한다.

통화 종료 시 남은 기록을 보존하고 다음 참가 시 초기화한다. 강제 프로세스 종료는 메모리 기록을 잃는다. 앱은 자동 업로드하지 않는다.

## 시작·트리거·상한

- 통화 준비 중 기록을 시작하고 처음2초는 자동 트리거에서 제외한다. 모든 장치/연결 초기화가2초 안에 끝난다는 보장은 없다.
- 자동 트리거: 송신 큐 용량 초과/나이 제한 폐기, 송신 API 예외, 인증된 음성 콜백 간격50ms 초과, 캡처 간격30ms 초과, RTP 재버퍼, 사용자 지정 렌더의 출력 패딩 소진.
- 한 통화당8192개 값 형식 레코드의 고정 배열을 쓴다. 기록량이 많으면 오래된 배경 기록을 덮어쓴다.
- 사건이 발생하면 최대4096개 직전 기록(트리거 포함)을 유지하고 최대4096개 후속 기록 또는2초 중 먼저 도달하는 조건에서 동결한다. 다인/짧은 프레임에서 **사건 전후 몇 초 전체를 보장하지 않는다**. 후속 기록이 트리거/모든 사전 기록을 덮어쓰지 않는다.
- `Overwritten`은 순환 덮어쓰기와 사건 전 기록 축소 수다. `SkippedContention`은 기록 잠금 충돌로 놓친 수다. 오디오 스레드는 기록 잠금을 기다리지 않는다. 누락 카운터가0이어도 미계측 경계가 관측된 것은 아니다.
- `State`는 Rolling/AfterTrigger/Frozen/Stopped, `EndReason`은 duration-limit/capacity-limit/call-ended다. `CaptureUntil`은 예정된 상한이며 용량 제한/종료 시 실제로 그 시각까지 수집하지 않는다.

## 레코드 해석

`At`과`SourceAt`은 같은 프로세스의 단조 시각(초)이다. 배열은 기록 잠금 획득 순서이며 여러 스레드의 At 순서와 완전히 같지 않을 수 있다. 다른 PC 시각과 직접 차감하지 않는다.

|Stage|정보와 한계|
|---|---|
|capture-callback|샘플 수, 처리시간 DurationMs, 콜백 간격 QueueMs, 입력 Flags, 잠금 대기. SourceAt은 처리 진입 시각이며 장치 물리 수음 시각이 아니다.|
|frame-created|앱 Sequence, SampleOffset, Samples, 인코딩 시간 DurationMs, 유효할 때 첫 입력 샘플 QPC SourceAt.|
|send-drop|앱 Sequence/샘플 오프셋/길이, 버려질 때 나이 DurationMs, capacity 또는 expired.|
|send-dequeue|앱 Sequence/샘플 오프셋/길이, 생성 후 나이 DurationMs.|
|send-api-enter / send-api-return|상대ID·SSRC·RTP 타임스탬프와 앱 Sequence/샘플 오프셋 연결. return은 SDK API 반환이며 OS 소켓 전송 완료가 아니다.|
|send-api-error|앱 프레임과 상대 식별. SDK 호출 예외이며 패킷의 물리 미전송을 입증하지 않는다.|
|rtp-channel-observer|RTP 고정 헤더 식별만. SDK의 기존 처리 이후에 붙인 이벤트이며 NIC 수신·인증 전 시각이 아니다. 이 이벤트만으로 인증 성공을 판정하지 않는다.|
|authenticated-rtp-callback|인증/복호화 후 음성 콜백의 SSRC·RTP 순번·타임스탬프. DurationMs는 이전 음성 콜백과의 간격.|
|buffer-admission|타임스탬프·버퍼 수용/late/duplicate/invalid 등 결과·큐·처리/잠금 대기.|
|playout-read|요청 샘플 수, 해당 읽기의 PLC 음성시간 DurationMs, 큐·처리시간 WorkMs·잠금 대기 LockWaitMs. RTP 타임스탬프는 읽기 끝의 재생 위치이며 한 패킷의 실제 DAC 출력 시각이 아니다.|
|render-empty / render-submit|사용자 지정 공유 출력의 소진 단계/공급 샘플·공급 작업시간. 렌더 레코드의 샘플 단위는 SampleRate를 따른다. 상대별 실제 스피커 재생 완료가 아니다. 호환 출력 내부는 여기서 계측하지 않는다.|

대부분 샘플은48kHz이며 `SampleRate`를 확인한다. 필드를 사용하지 않는 단계에서는0/null이 기본값이다. 송신 Sequence는 앱 프레임ID이고 수신 Sequence는16비트 RTP 순번이므로 서로 그대로 비교하지 않는다. 송신은 SSRC+RTP 타임스탬프와 샘플 오프셋으로 연결한다.

`TriggerGen0/1/2`는 사건 순간 프로세스 GC 누적 횟수다. GC 정지 시간이나 그 사건의 원인이 아니다. ETW 스케줄링/DPC/ISR·소켓 완료·SRTP 인증/재전송 방지의 상세 거부 사유는 자동 수집하지 않는다. 필요하면 기존 한정 추적을 병행한다. 자동 기록만으로 NIC/무선/AP 내부 원인이나 물리 입→귀 지연을 확정하지 않는다.

출력의 `Render.AttemptedMixFormat`은 사용자 지정 공유 경로에서 조회한 장치 믹스 형식이고, `RequestedEnginePeriodMs`는 초기화에 요청한 주기다. 초기화 전 실패 시 null일 수 있다. 최종 출력 형식/적용 주기와 구분한다.

음성·페이로드·비밀값은 이 기록에 넣지 않는다. 일반 진단의 다른 항목에는 기존처럼 IP·닉네임·장치 정보가 있으므로 공개 공유 전 확인한다.
