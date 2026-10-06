# 명동 콜링 Native 0.1.0

Windows 네이티브 음성 통화 앱과 Ubuntu에서 실행할 연결 관리 서버입니다.
현재 기준은 두 Windows PC에서 직접 연결과 WASAPI 출력으로 끊김 없이 통화한 버전입니다.

## 소스 구조

```text
native/
  VoiceNative/      Windows 앱, WebRTC 엔진, 오디오 장치, 진단 화면
  TransportProbe/   전송·통계·재생 큐 검증 도구
  server/           Node.js 연결 관리 서버와 테스트
  build.ps1         Release 빌드 및 선택적 ZIP 생성
  run.ps1           개발 실행
  README.md
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
- 마이크 입력은 WaveIn, 출력은 WASAPI 공유 모드 / 이벤트 기반 / 요청 버퍼 40ms
- 앱 재생 큐는 시작 시 40ms 확보, 240ms 초과 시 120ms를 남기고 오래된 부분만 제거
- 최대 6명 메시 연결 구현, 실제 검증은 같은 공유기의 두 PC 통화
- `ICallEngine`으로 화면과 통화 엔진 분리, 추후 커스텀 엔진 교체 가능

현재 자체 에코 제거가 없으므로 헤드셋 사용을 기준으로 검증합니다.
다자 통화, 인터넷 TURN, 장시간 안정성과 실제 종단 간 음성 지연은 추가 검증 대상입니다.
입력 WASAPI 전환, 적응형 지터 버퍼, 재정렬·손실 복구·혼잡 대응은 후속 작업입니다.

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
