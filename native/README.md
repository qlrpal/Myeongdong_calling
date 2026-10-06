# Windows 네이티브 통화 프로토타입

C# / .NET 10 Windows Forms 앱입니다. 브라우저나 WebView를 사용하지 않습니다.
SIPSorcery 10.0.17로 WebRTC ICE·DTLS/SRTP 연결을 구성하고,
NAudio 3.0.0 기반 장치 어댑터로 기본 마이크와 스피커를 사용합니다.

## 실행

Windows 10 1809 이상과 .NET 10 SDK가 필요합니다.
저장소 루트에서 서버와 앱을 각각 실행합니다.

```powershell
node web/server.js
```

```powershell
dotnet run --project native/VoiceNative
```

또는 저장소 루트에서 `./native/run.ps1`로 실행합니다.
현재 빌드된 실행 파일은 `native/dist/VoiceNative.exe`입니다.
이 배포 폴더는 .NET 10 Desktop Runtime이 필요한 프레임워크 종속 빌드이며,
다른 PC로 옮길 때는 `dist` 폴더 전체를 복사하세요.

앱 두 개 또는 Windows PC 두 대에서 같은 서버 주소와 방 이름으로 참가합니다.
기본 서버 주소는 `http://localhost:3000/`입니다.
다른 PC에서 접속하려면 서버 PC에서 다음처럼 수신 주소를 변경하고,
클라이언트에 해당 PC의 IP 또는 서버의 HTTPS 주소를 입력합니다.

```powershell
$env:HOST = '0.0.0.0'
node web/server.js
```

서버의 TCP 3000 포트와 앱의 UDP 통신을 방화벽에서 허용해야 합니다.
외부 서버의 연결 관리에는 HTTPS를 사용하세요.
이어폰/헤드셋을 사용하세요. 현재 자체 에코 제거 기능이 없습니다.

## 연결과 오디오

- 기존 `web/server.js`의 방 참가·SSE·SDP/ICE 신호 API를 재사용합니다.
- 최대 6명 메시 연결입니다. 각 상대와 별도 연결하며 마이크 캡처는 공유합니다.
- 서버는 기본적으로 STUN만 제공합니다. TURN은 서버의 `ICE_SERVERS`로 설정합니다.
- TURN이 구성되면 연결 탐색에서 중계 경로를 사용할 수 있습니다. 춘천 서버에 TURN을 설치하거나 배포한 상태는 아닙니다.
- Opus 48kHz, 모노 입력, 20ms 음성 프레임입니다. RTP 타임스탬프는 프레임당 960 증가합니다.
- 비트레이트는 현재 라이브러리 기본값입니다. 510kbps를 강제하지 않습니다.
- 현재 오디오 장치 접근은 WaveIn/WaveOut 경로입니다. WASAPI 저지연 구현은 후속 단계입니다.
- 재생 장치 버퍼 목표는 40ms이며 수신 큐가 80ms를 넘으면 오래된 오디오를 제거합니다. 전체 통화 지연을 40ms로 보장하지 않습니다.
- 적응형 지터 버퍼·재정렬·손실 복구·혼잡 제어는 후속 검증/구현 대상입니다.

`ICallEngine`이 화면과 통화 구현을 분리합니다. 커스텀 엔진은 이 인터페이스를 구현해 교체할 수 있습니다.
`WindowsAudioDevice`가 오디오 장치 구현을 분리합니다. 후속 WASAPI 작업에서 장치 인터페이스를 확장할 예정입니다.

## 검증

```powershell
dotnet run --project native/TransportProbe
```

물리 마이크를 열지 않고 두 네이티브 피어가 ICE·DTLS 연결을 수립해
Opus 20ms 합성 음성 프레임을 SRTP로 보내고 디코딩하는지 확인합니다.
이는 앱 전체의 SSE 연결·실물 장치·인터넷 TURN·그룹 통화 검증을 대신하지 않습니다.
실제 음질과 종단 간 지연은 아직 측정하지 않았습니다.

## 라이선스

SIPSorcery는 BSD-3-Clause 기반의 추가 지역 사용 제한을 포함합니다. NAudio는 MIT 라이선스입니다.
각 패키지의 `LICENSE.md`를 참고하세요. 배포 시 종속 라이브러리의 라이선스 고지를 포함해야 합니다.
소스와 API: https://github.com/sipsorcery-org/sipsorcery
