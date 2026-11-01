# 선택적 SDK 경계 진단

일반 앱의 SDK나 통화 설정을 바꾸지 않고, 고정된 기존 수정 SDK의 별도 복사본에서만 계측한다. 사용자용 배포 기능이 아니라 개발 조사 도구다. Python은 이 도구에만 필요하며 일반 실행/빌드에는 필요하지 않다.

## 기록 경계

| Stage | 실제 의미 |
|---|---|
| socket-send-post | BeginSendTo 호출 직전. 전파/회선 출발이 아니다. |
| socket-send-end-observed | 완료 콜백에서 EndSendTo 반환을 관측. OS 완료 순간 자체가 아니다. |
| socket-send-rejected / socket-send-end-error | 해당 경로에서 관측한 SocketException/종료 오류 코드. 모든 SDK 오류를 포괄하지 않는다. |
| udp-receive-end | EndReceiveFrom 또는 동기 ReceiveFrom 반환 직후. OS 최초 도착이나 물리 NIC 수신이 아니다. |
| srtp-unprotect-enter / return | 기존 복호화 함수를 호출하기 전후와 원래 반환 코드. 인증/재전송 방지 동작을 바꾸지 않는다. |

RTP의 평문 헤더 SSRC·순번·타임스탬프, 로컬 포트, 길이, 상태, 이 호스트 QPC만 저장한다. PT111 Opus와 TURN ChannelData/Send·Data Indication 안의 같은 헤더만 대상으로 한다. 음성/암호문/키/토큰/상대 주소는 이 SDK 기록에 보관하지 않는다. 인증 전 헤더는 신뢰되지 않은 식별 후보이며 미디어 상태를 갱신하는 데 사용하지 않는다. 기존 참가자 JSON에는 별도로 주소/장치 등 개인정보가 포함될 수 있다.

고정 배열 최대64,000개·최대120초, TryEnter 실패/덮어쓰기 수를 기록한다. 기본 SDK에는 이 코드 자체가 포함되지 않는다. 진단 SDK도 Start 전에는 기록하지 않는다. 활성 송신은 완료 콜백 연결을 위해 패킷당 SendToken을 추가 할당하며, 기록 잠금·시계 조회 비용도 있다. 계측 비용0으로 간주하면 안 된다.

## 빌드·검증

먼저 정상 빌드를 완료해 수정 SDK 캐시/manifest와 최신 검사 도구를 준비한다. 저장소 루트에서:

```powershell
dotnet build native/TransportProbe/TransportProbe.csproj -c Release --no-restore
python native/sdk-diagnostics/prepare.py native/.local/sdk-boundary-current.json
$m = Get-Content native/.local/sdk-boundary-current.json -Raw | ConvertFrom-Json
& ($m.binary + '\TransportProbe.exe') --sdk-boundary-checks native/.local/sdk-boundary-check.json
python -m unittest discover -s native/TransportProbe/Analysis -p test_sdk_boundaries.py
```

복원에는 NuGet 네트워크 접근이 필요할 수 있다. 준비 스크립트는 캐시 DLL/수정 소스 해시와 고정 앵커를 확인한 뒤 `.local/sdk-boundary-<GUID>/`에만 복제·컴파일한다. 기본 SDK 캐시, 기본 실행 bin, dist를 교체하지 않는다. 진단 DLL은 별도 informational version `10.0.17+myeongdong.2.boundary1`과 `MyeongdongDiagnosticSdk=boundary1`로 구분한다. 실제 생성된 해시를 로컬 manifest에 남긴다.

## 양쪽 시험과 분석

같은 진단 바이너리의 TransportProbe를 양쪽 격리 시험 폴더에서 실행한다. `--pair-functional`의 기존 인자/합성 WAV를 사용하며, **각 자식 프로세스에만** `MYEONGDONG_TEST_SDK_BOUNDARY=1`을 전달한다. 일반 앱/시스템 환경 변수로 설정하지 않는다. 양쪽 계측 모드가 다르면 핸드셰이크가 실패한다. 기본 SDK에서 활성화를 요청하면 조용히 무시하지 않고 명시적으로 실패한다.

계측은 참가 전 시작하고 기준선의 첫 사건 동결 또는 기준선 종료/실패 때 멈춘다. 사건 동결 확인은250ms 검사 주기에 종속된다. 파일 직렬화는 오디오 정리 후 수행한다. 결과 `sdkBoundary.data`와 `baselineIncident`를 함께 분석한다.

양쪽 시험 프로세스에 `MYEONGDONG_TEST_SDK_BOUNDARY_FULL_BASELINE=1`도 전달하면 자동 사건 캡슐이 먼저 동결돼도 SDK는 기준선 종료까지 기록한다. 최대120초/64,000개 제한은 유지되므로 긴 참가 대기나 긴 기준선을 무제한 보존하지 않는다. `sdkFullBaseline`을 보고서와 핸드셰이크에 포함하며, 자동 사건 캡슐 자체의 창은 늘리지 않는다. 이 선택은 기록 비용/기간도 바꾸므로 서로 다른 선택을 성능 단일 변수 대조로 섞지 않는다.

```powershell
python native/TransportProbe/Analysis/analyze_sdk_boundaries.py PC.json LAPTOP.json OUTPUT.json
```

앱 송신 식별자는 앱 프레임 번호이므로 송신 API↔소켓은 SSRC/타임스탬프로 연결하고, SDK 송신↔수신/앱 수신은 RTP 순번까지 연결한다. 다른 PC의 QPC를 직접 빼지 않는다. 수신 공백과 같은 패킷 쌍의 송신 간격을 비교하는 것은 가능하지만 편도 지연 측정은 아니다.

누락은 창 경계·기록 충돌·송신 실패·SDK/OS/네트워크 등 여러 원인이 가능하다. 특히 `peerPostsWithoutUdpObservationInMatchedWindow`를 물리 손실로 읽지 않는다. `udpWithoutAppObservationInCapsuleWindow`도 인증 결과와 기록 누락을 함께 본다. 선택되지 않은 PT, 모든 소켓 예외, OS/드라이버 내부 큐, 물리 무선 송수신은 측정하지 않는다. Npcap/ETW도 별도 소프트웨어 관측 경계이며 전파 시각으로 대체할 수 없다.

두 PC의 Npcap과 서버 UDP3478 캡처를 별도로 확보했다면 다음 분석기로 같은 패킷 쌍의 구간을 연결할 수 있다. Windows의 Wireshark/tshark 설치가 필요하다. 원본 캡처에는 제한된 암호화 패킷 prefix/STUN 메타데이터가 있으므로 SDK 메타데이터 기록과 구분하고 Git에 올리지 않는다.

```powershell
python native/TransportProbe/Analysis/analyze_turn_capture_chain.py A.json B.json A.pcapng B.pcapng OCI.pcap OUTPUT.json
python -m unittest discover -s native/TransportProbe/Analysis -p test_turn_capture_chain.py
```

`events`는 자동 사건 캡슐의50ms 초과 콜백 간격, `captureEvents`는 전체 캡처의50ms 초과 수신 관측 간격이다. 후자는 SDK/앱 보존 창 밖 자료도 포함하므로 null 경계를 추정해 채우지 않는다. 반복 식별자는 유일 대응으로 인정하지 않는다. 서버 체류는 같은 서버 캡처 시계의 수신→송신 관측 차이이며, 인터넷 편도 지연은 계산하지 않는다. [실제 동시 캡처 결과132](reports/EXTREME-132.md).

## 복귀

진단 프로세스를 종료하고 정상 `native/run.ps1` 또는 기존 배포본을 실행한다. 정상 SDK를 바꾸지 않았으므로 DLL 수동 복구는 필요 없다. 다른 PC의 기존 시험 폴더를 임시로 사용했다면 활성 시험 프로세스가 없는지 확인하고, 사전에 보관한 그 폴더만 복구한 뒤 해시를 비교한다. 시스템 라우팅/장치/전원 계획 변경은 이 도구에 필요 없다.
