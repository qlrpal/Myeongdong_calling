# 명동 콜링 Native 0.3.3

Windows 네이티브 음성 통화 앱과 Ubuntu에서 실행할 연결 관리 서버입니다.
현재 버전은 기본 음성 통화와 진단을 구현한 개발 기준점입니다.
진행 내용, 검증 범위, 유지보수 항목과 이후 개발 순서는 [STATUS.md](STATUS.md)를 참고하세요.

## 소스 구조

```text
native/
  VoiceNative/      Windows 앱, WebRTC 엔진, 오디오 장치, 진단 화면
  TransportProbe/   전송·통계·재생 큐 검증 도구
  server/           Node.js 연결 관리 서버와 테스트
  build.ps1         Release 빌드 및 선택적 ZIP 생성
  run.ps1           개발 실행
  README.md
  STATUS.md         완료 내역, 검증 범위와 이후 개발 순서
```

`dist/`, `dist*.zip`, `.dotnet/`, `.nuget/`, `.local/`, `bin/`, `obj/`는 로컬 생성물이며 Git에 포함하지 않습니다.
이전 빌드·화면 캡처는 `.local/archive/`에 보관합니다. 실행 중이던 `dist-wasapi/`는 호환을 위해 유지할 수 있습니다.

## Windows 실행과 빌드

Windows 10 2004 이상, .NET 10 SDK가 필요합니다. 저장소 루트에서 실행합니다.

```powershell
./native/run.ps1
./native/build.ps1
./native/build.ps1 -Package
```

최신 빌드는 `native/dist/VoiceNative.exe`, ZIP은 `native/dist.zip`입니다.
다른 PC에는 `dist` 폴더 전체를 복사하세요. .NET 10 Desktop Runtime이 필요합니다.
새 실행 경로는 Windows 방화벽에서 앱의 UDP 수신을 허용해야 합니다.
의존성이 이미 복원돼 있다면 `./native/build.ps1 -NoRestore`를 사용할 수 있습니다.

기본 연결 서버 주소는 `https://qlrpal.ddns.net/`입니다. 두 앱에서 같은 방 이름으로 참가합니다.
로컬 서버를 쓰려면 앱 주소를 `http://localhost:3000/`으로 변경하세요.

## 서버 실행

Node.js 22 이상이 필요하며 외부 npm 패키지는 없습니다. `web/`에 의존하지 않습니다.

```powershell
node native/server/server.js
```

Ubuntu 실행과 HTTPS 프록시 설정은 [server/README.md](server/README.md)를 참고하세요.
현재 운영 주소는 Nginx의 HTTPS 443에서 내부 HTTP 3000으로 전달합니다.
서버는 방 참가와 SDP/ICE 신호를 관리하며 음성은 가능한 경우 PC 사이에 직접 전송됩니다.
TURN은 별도 서버이고 현재 운영 환경에는 미설정입니다.

## 오디오와 연결

- C#/.NET 10 Windows Forms, SIPSorcery 10.0.17, NAudio 3.0.0
- Opus 48kHz 모노, 20ms 프레임, RTP 타임스탬프 증가량 960
- 비트레이트는 라이브러리 기본값이며 510kbps를 강제하지 않음
- 마이크 입력은 WASAPI 공유 모드 / 이벤트 기반 / 요청 버퍼 20ms, 출력은 WASAPI 공유 모드 / 이벤트 기반 / 요청 버퍼 40ms
- 앱 재생 큐는 시작 시 40ms 확보, 240ms 초과 시 120ms를 남기고 오래된 부분만 제거
- 최대 6명 메시 연결 구현, 실제 검증은 같은 공유기의 두 PC 통화
- `ICallEngine`으로 화면과 통화 엔진 분리, 추후 커스텀 엔진 교체 가능

현재 자체 에코 제거가 없으므로 헤드셋 사용을 기준으로 검증합니다.
다자 통화, 인터넷 TURN, 장시간 안정성과 실제 종단 간 음성 지연은 추가 검증 대상입니다.
적응형 지터 버퍼, 재정렬·손실 복구·혼잡 대응은 후속 작업입니다.

0.3.0은 입력을 WasapiRecorder로 전환하고 입력 콜백 간격 P95/최대를 기록합니다.
0.3.1은 진단용 Core Audio 지연 조회를 입력/디코더 콜백 잠금 밖으로 분리합니다.
0.3.2는 캡처 콜백의 동기 네트워크 송신을 별도 작업으로 분리합니다.
0.3.3은 최초 Opus 인코더 준비를 캡처 시작 전으로 옮기고 재생 부족으로 채운 무음 길이 합계와
최대 연속 부족 길이를 기록합니다. 시작 버퍼 확보·장치 고장 중 정지는 이 부족 길이에 포함하지 않습니다.
이 값은 앱이 부족한 PCM을 0으로 채운 길이이며 물리적으로 들린 끊김의 직접 측정값은 아닙니다.
송신 큐는 3프레임(오디오 60ms분)으로 제한하고 초과 시 가장 오래된 프레임을 버립니다.
이는 스케줄링/실제 송신 지연을 60ms로 보장하는 값이 아닙니다.
프레임 타임라인에서 RTP 타임스탬프를 지정해 버린 프레임의 시간도 유지하며,
기존 WebRTC의 SRTP 암호화 송신 경로를 사용합니다. payload ID는 협상 결과를 따릅니다.
송신 대기 프레임 수·큐 초과 버림·최대 대기 시간은 화면과 JSON의 Sender에 기록됩니다.
큐에서 버린 프레임은 네트워크 RTCP 손실과 별도의 앱 손실입니다.
`dotnet run --project native/TransportProbe -- --send-queue-test`로 큐 제한과 종료를 확인하고,
`--rtcp-integration` 검증은 실제 수신 RTP 타임스탬프와 암호화 전송·RTCP 보고를 함께 확인합니다.
입력 콜백 전체 처리 시간(잠금 대기·인코딩·송신 포함), WASAPI 불연속·타임스탬프 오류를 추가 기록합니다.
불연속 플래그는 캡처 스트림의 관측이며 네트워크 패킷 손실 수와 같지 않습니다.
기존 진단에는 최대 약 404ms의 수신 공백이 관측됐습니다. 평균 지터만으로 큰 공백을 숨기지 않도록
양쪽의 입력 콜백 공백과 상대편 패킷 도착 공백을 함께 비교하세요.
`dotnet run --project native/TransportProbe -- --capture-test`는 기본 마이크를 3초간 읽어
Opus 프레임 생성과 입력 콜백 간격을 확인하며 음성을 파일에 저장하지 않습니다.

## 직접 디버깅

1초마다 상대별 연결 상태·후보 주소·직접/TURN 경로·송수신 비트레이트·수신 지터를 표시합니다.
비트레이트는 직전 표본 이후 Opus 페이로드만 집계하며 통신 헤더를 제외합니다.
손실률은 상대가 마지막 RTCP 구간에서 보고한 우리 송신의 손실률이며 보고 경과 시간을 함께 확인하세요.
미측정 값은 0 대신 미측정으로 표시합니다.

마이크/출력 RMS는 최근 프레임, 코덱 처리 시간은 세션 평균입니다.
재생 큐·초과 큐 정리·버퍼 부족·디코딩 길이·출력 소비량도 표시합니다.
출력 소비량은 오디오를 출력 경로에 전달한 길이로, 실제 귀에 도달한 시점 측정은 아닙니다.
앱 큐와 ICMP ping은 마이크부터 상대 출력까지의 전체 지연을 뜻하지 않습니다.

- **진단 복사/저장:** 최근 1,200개 표본(약 20분), 2,000줄 이벤트와 네트워크 인터페이스를 JSON으로 내보내기
- **LAN UDP 검사:** 양쪽에서 서로의 내부 IPv4를 입력하고 30초 검사를 겹쳐 실행, UDP 42000 왕복 확인
- **경로·ping 검사:** OS 선택 송신 주소와 ICMP RTT를 확인해 Tailscale 같은 우회 경로와 비교

보고서에는 IP·닉네임이 포함되며 음성·세션 토큰·TURN 자격 증명은 기록하지 않습니다.
저장과 복사는 사람이 읽을 수 있는 UTF-8 한글 JSON을 사용하며 한글을 유니코드 이스케이프로 변환하지 않습니다.
특정 UDP 포트의 성공이 모든 ICE 포트의 성공을 보장하지는 않습니다.

## 검증 명령

```powershell
dotnet run --project native/TransportProbe
dotnet run --project native/TransportProbe -- --metrics-self-test
dotnet run --project native/TransportProbe -- --playout-self-test
dotnet run --project native/TransportProbe -- --lan-self-test
dotnet run --project native/TransportProbe -- --device-playout-test
node --test native/server/test/server.test.js
```

전송 검증은 물리 마이크를 열지 않고 두 피어의 ICE·DTLS/SRTP·Opus 전송을 확인합니다.
장치 재생 검사는 기본 출력에 WaveOut/WASAPI 각각 5초간 무음을 공급합니다.
서버 테스트가 프로세스 생성 권한으로 실패하는 환경에서는 `node --test --test-isolation=none native/server/test/server.test.js`를 사용합니다.

## 정밀 진단 (0.2.0)

### 출력 장치 복구 (0.2.1)

장치 분리/재설정 오류 시 출력 상태·오류·재시작 횟수를 표시하고 자동 복구를 한 번 시도합니다.
실패하면 장치를 연결한 후 `출력 재시작`으로 기본 출력 장치를 다시 엽니다.
WebRTC 연결과 마이크를 유지하며, 고장 중의 수신 오디오는 재생 큐에 쌓지 않습니다.
장치 복구 시에만 오래된 큐를 버리고 시작 여유를 다시 확보합니다.
짧은 버퍼 부족은 무음으로 채운 뒤 바로 이어 재생하며, 매번 40ms를 재충전하지 않습니다.
기기 재설정 중간에 시작한 복구는 실패할 수 있으므로 반복 자동 재시도 대신 수동 버튼을 제공합니다.

```powershell
dotnet run --project native/TransportProbe -- --buffer-recovery-test
dotnet run --project native/TransportProbe -- --device-restart-test
```

장치 재시작 검사는 실제 기본 출력을 무음 스트림 중 다시 열어 확인하며,
물리 USB 분리·드라이버 재설정 이벤트의 자동 복구를 대신하는 검증은 아닙니다.

RTCP RTT는 송신 보고의 NTP 식별자와 로컬 단조 시계의 송신 시각을 대응시키고,
수신 보고에서 상대 대기 시간(DLSR)을 빼서 계산합니다. 전체 음성 지연이 아닙니다.
계산 규약은 [RFC 3550 6.4.1](https://www.rfc-editor.org/rfc/rfc3550.html#section-6.4.1)을 따릅니다.
SSRC가 맞는 보고만 반영하며 미측정/음수/유효 기간을 벗어난 RTT는 새 값으로 사용하지 않습니다.
보고 경과 15초를 넘으면 화면에 오래된 보고로 표시합니다.

송신 손실은 상대 수신 보고, 수신 손실은 우리가 내보낸 RTCP 수신 보고를 사용합니다.
누적 손실은 늦게 도착한 패킷과 중복 수신으로 음수가 될 수도 있습니다.
순서 뒤바뀜/중복 탐지는 최근 순서번호 창을 사용하고 송신 SSRC 변경 시 창을 초기화합니다.
패킷 도착 간격과 코덱 처리 P95/최대는 최근 256개 관측치 기준입니다.

장치 이름·출력 포맷·API 보고 지연과 마지막 프레임 경과를 표시합니다.
API 지연은 장치/드라이버 추정치이며 마이크부터 상대 귀까지의 물리 지연이 아닙니다.
구간 재생 소비율은 단조 시계를 사용하고 약 1000ms/s가 정상 속도 기준입니다.
큐 정리·버퍼 부족이 새로 발생하면 이벤트 로그에 증가량을 기록합니다.
JSON 보고서 스키마는 2이며 기존 통계와 새 정밀 통계를 함께 저장합니다.

```powershell
dotnet run --project native/TransportProbe -- --precision-self-test
dotnet run --project native/TransportProbe -- --rtcp-integration
```

## 확인한 안정화 결과 (2026-10-07)

기존 WaveOut은 이 PC에서 5초 공급 중 약 4.02초만 소비해 큐가 누적됐습니다.
WASAPI는 약 5초를 소비하고 큐 정리는 0회였습니다.
두 PC 실제 통화에서는 약 174초에 초과 큐 정리·버퍼 부족 모두 0회,
수신 지터 약 3.39ms와 재생 큐 50ms가 관측됐고 사용자가 끊김 해소를 확인했습니다.
장치와 회선이 달라지면 결과도 달라지므로 전체 음성 지연을 보장하는 수치로 사용하지 않습니다.

## 라이선스

SIPSorcery는 BSD-3-Clause 기반의 추가 지역 사용 제한을 포함하며 NAudio는 MIT 라이선스입니다.
배포할 때 종속 패키지의 라이선스 고지를 포함해야 합니다.

- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery)
- [NAudio](https://github.com/naudio/NAudio)
