# 명동 콜링 (Voice Room)

같은 방 이름으로 입장한 사람들이 브라우저에서 음성 통화를 하는 웹앱입니다. Node.js 서버가 방과 연결 신호를 관리하고, 음성은 WebRTC로 참여자 사이에 전송합니다.

저장소: [qlrpal/Myeongdong_calling](https://github.com/qlrpal/Myeongdong_calling) · 앱 버전: **0.1.0** · 문서 기준: **2026-10-06**

## 이것만 설치하면 실행됩니다

**프로젝트 소스가 있다면 서버에는 Node.js 24.15.0만 설치하면 됩니다.** 이 배포본에 포함된 npm 버전은 **11.12.1**입니다. npm을 별도로 설치하거나 `npm install`을 실행할 필요가 없습니다.

설치 기준은 아래 한 조합으로 고정합니다. 아래쪽의 다른 버전 목록은 호환성 검증 기록입니다.

| 항목 | 설치 기준 | 별도 설치 여부 |
| --- | --- | --- |
| OCI 서버 OS / CPU | Ubuntu 24.04.5 LTS / x64 | 검증한 기존 서버 환경 사용 |
| 서버 런타임 | **Node.js 24.15.0 Linux x64** | **필수 설치 1개** |
| npm | **11.12.1** | 위 Node.js 배포본에 포함 |
| 앱 소스 | `voice-room` **0.1.0** | 저장소의 소스 파일 필요 |
| 외부 npm 패키지 | **0개** | 설치할 패키지 없음 |
| 서버 내장 모듈 | Node.js 24.15.0에 포함 | 별도 설치 없음 |
| 사용자 브라우저 | WebRTC·마이크 API 지원 브라우저 | 사용자 기기에서 접속, 서버에 설치하지 않음 |

프로젝트 폴더에서 다음 명령으로 실행합니다.

```bash
node --version    # v24.15.0
npm --version     # 11.12.1
npm test          # 현재 테스트 17개
npm start         # http://localhost:3000
```

`npm` 명령 없이 **`node server.js`만으로 실행해도 됩니다.** 빌드 과정, 데이터베이스 및 외부 패키지 설치 과정은 없습니다. Windows에서는 같은 Node.js 버전의 Windows x64 배포본을 설치하고 `npm.cmd`를 사용하세요.

### OCI에서 다른 사람도 접속해 통화하려면

위 설치 목록은 앱 프로세스 실행 기준입니다. 외부 기기에서 마이크를 사용하는 통화 서비스에는 **HTTPS 주소와 유효한 TLS 인증서**가 추가로 필요합니다. Node.js만 설치하고 `http://공인IP:3000`을 여는 것으로는 마이크를 사용할 수 없습니다.

| 용도 | 추가 준비 | 현재 프로젝트의 버전 명세 |
| --- | --- | --- |
| 외부 접속·마이크 접근 | HTTPS 프록시 또는 HTTPS 제공 서비스 + TLS 인증서 | 제품·버전 미선정, 배포 구성 필요 |
| 직접 연결이 막힌 네트워크의 통화 | TURN 서버 또는 TURN 서비스 | 제품·버전 미선정, 실제 중계 미검증 |
| 서버 부팅 후 자동 실행 | Ubuntu의 systemd | 운영체제에 포함, 별도 npm 패키지 불필요 |

HTTPS를 기존 인프라에서 제공하면 서버에 별도의 HTTPS 제품을 설치할 필요는 없습니다. 직접 구성한다면 Nginx 등을 추가해야 합니다. **HTTPS·TURN까지 포함해 외부 실통화가 완료된 설치 명세는 아직 확정하지 않았습니다.** 해당 제품의 버전은 검증한 것처럼 기재하지 않습니다.

### 설치하지 않아도 되는 항목

- **Git:** `git clone`으로 소스를 받을 때만 필요합니다. ZIP 다운로드·파일 업로드로 소스를 전달하면 서버 실행에는 필요하지 않습니다.
- **Python 3.12 이상:** 여러 Node.js 버전을 다운로드해 검증하는 `scripts/verify_versions.py`에만 필요합니다. 앱 실행과 `npm test`에는 필요하지 않습니다.
- **Docker·WSL:** 앱 실행에는 필요하지 않습니다. WSL은 Linux 호환성 검증에 사용한 환경입니다.
- **Express·Socket.IO·React·데이터베이스·PM2·별도 테스트 프레임워크:** 현재 앱에서 사용하지 않습니다.

기본 STUN 주소 `stun:stun.l.google.com:19302`는 코드에 설정된 외부 네트워크 서비스이며, 서버에 설치하는 패키지가 아닙니다.

## 구현 범위

- 닉네임과 방 이름으로 입장, 방당 최대 6명
- 실시간 음성 통화, 참여자 목록과 연결 상태 표시
- Opus 510kbps 목표 설정: 수신 SDP `maxaveragebitrate=510000`, 송신 `maxBitrate=510000`
- 지원 브라우저의 음성 수신 버퍼 목표 20ms: `RTCRtpReceiver.jitterBufferTarget`
- 통화 나가기 및 연결 종료 시 마이크 해제
- 마이크 권한 거부, 입장 실패, 서버 연결 종료 안내

카메라, 화면 공유, 채팅, 음소거, 사용자 계정은 구현하지 않았습니다. 브라우저에 `echoCancellation`, `noiseSuppression`, `autoGainControl`을 모두 `false`로 요청합니다. 장치나 운영체제 자체의 음성 처리는 별개입니다.

510kbps 설정은 고정 전송량을 보장하지 않습니다. Opus 가변 비트레이트와 네트워크 적응에 따라 실제 수신량은 달라집니다. 헤더·전송 오버헤드는 별도이며, 비트레이트 설정에 실패하는 브라우저는 기본 설정으로 통화를 유지하고 안내합니다. 변경을 적용하려면 **양쪽 PC 모두 페이지를 새로고침한 뒤 통화방에 다시 입장**해야 합니다. 스테레오 입력이나 무손실 전송을 강제하지 않습니다.

수신 버퍼 20ms는 브라우저에 전달하는 목표값입니다. 네트워크와 브라우저의 최소 버퍼 조건에 따라 실제 대기는 더 길어질 수 있고, 전체 음성 전달 지연을 20ms로 보장하지 않습니다. API 미지원 또는 설정 거부 시 기본 버퍼로 통화를 유지합니다. 낮은 버퍼는 지연을 줄이는 대신 늦은 패킷에 따른 끊김이 늘 수 있습니다. [API 설명](https://developer.mozilla.org/en-US/docs/Web/API/RTCRtpReceiver/jitterBufferTarget).

## 버전 및 실행 환경

| 항목 | 명세 | 비고 |
| --- | --- | --- |
| 앱 | `voice-room` / `0.1.0` | `package.json` 기준, 비공개 npm 패키지 |
| Node.js | `>=22` | `package.json`의 서버 실행 요구사항 |
| 설치 기준 Node.js | **24.15.0** | 아래 검증한 버전 중 실제 설치 기준으로 선택한 버전 |
| 확인한 Node.js | **22.0.0, 22.7.0, 22.8.0, 22.23.0, 23.6.0, 24.0.0, 24.15.0** | 버전별 실행·테스트 결과는 아래 검증 상태 참고 |
| npm | 별도 최소 버전 지정 없음 | Node.js에 포함된 npm 사용, 10.5.1~11.12.1의 아래 명시 조합 검증 |
| JavaScript | ES Modules | `type: module`, 별도 컴파일·번들링 없음 |
| 서버 운영체제 | Windows 11, Ubuntu 24.04 x64 | Windows, WSL Ubuntu 24.04.4, OCI Ubuntu 24.04.5에서 검증 |
| OCI 아키텍처 | x64 | OCI 실서버에서 실행·테스트 검증 완료; ARM64 미검증 |
| 클라이언트 | WebRTC·마이크 API·SSE 지원 브라우저 | Codex 내장 브라우저(Chromium 사용자 에이전트 154.0.0.0)에서 합성 음원 통화 검증; 독립 Chrome·Edge·Firefox·Safari 및 실물 마이크 미검증 |

**공통 테스트 명령:** 현재 `npm test`는 아래 명령을 실행하며, 검증한 모든 Node.js 버전에서 동일하게 사용합니다.

```bash
node --test
```

기존 `--test-isolation=none` 옵션은 Node.js 22에서 지원되지 않아 제거했습니다. 기본 프로세스 격리를 사용합니다. `engines: >=22`는 선언된 요구사항이며, 실제 검증은 아래 표의 버전 조합에 한정됩니다. [Node.js 테스트 실행 문서](https://nodejs.org/download/release/v24.20.0/docs/api/cli.html#--test).

## 의존성 명세

**외부 npm 런타임 의존성 0개, 개발 의존성 0개.** 현재 `dependencies`와 `devDependencies` 선언이 없으며 `npm install` 없이 실행할 수 있습니다. Express, Socket.IO, React, 데이터베이스, 별도 테스트 프레임워크를 사용하지 않습니다.

| 구분 | 의존성 | 용도 및 버전 관리 |
| --- | --- | --- |
| 서버 | `node:http` | HTTP 서버·SSE, Node.js 내장 버전 사용 |
| 서버 | `node:fs/promises` | 정적 파일 읽기, Node.js 내장 버전 사용 |
| 서버 | `node:crypto` | 세션 ID·토큰 생성 및 토큰 비교, Node.js 내장 버전 사용 |
| 서버 | `node:url` | 모듈 파일 경로 처리, Node.js 내장 버전 사용 |
| 테스트 | `node:test`, `node:assert/strict`, `node:vm` | 테스트 실행·검증·클라이언트 모의 환경, Node.js 내장 버전 사용 |
| 브라우저 | `getUserMedia`, `RTCPeerConnection` | 마이크 입력·음성 연결, 브라우저 구현 사용 |
| 브라우저 | `fetch`, `EventSource` | HTTP 요청·SSE 신호 수신, 브라우저 구현 사용 |
| 외부 네트워크 | `stun:stun.l.google.com:19302` | 기본 STUN 설정, 별도 설치 없이 이용하는 외부 서비스 |
| 운영 시 별도 준비 | HTTPS 프록시·TLS 인증서 | 외부 기기의 마이크 접근에 필요, 앱에 포함되지 않음 |
| 네트워크에 따라 필요 | TURN 서버 | 직접 연결 불가 시 음성 중계, 앱에 포함되지 않음 |

프록시·TURN 제품 및 버전은 현재 프로젝트에서 지정하지 않습니다. Nginx와 coturn 등을 선택할 수 있으며, 실제 설치 버전과 설정은 배포 환경에서 별도로 관리해야 합니다.

## 실행

### Linux / OCI

Node.js **24.15.0 Linux x64**를 설치합니다. 아래처럼 `git clone`으로 소스를 받는 경우에만 Git도 필요합니다. ZIP 또는 파일 업로드로 소스가 이미 준비되어 있다면 `git clone` 단계는 생략합니다. ARM64는 이번 검증 대상에 포함되지 않았습니다.

```bash
git clone -b develop https://github.com/qlrpal/Myeongdong_calling.git
cd Myeongdong_calling
node --version
npm --version
npm test
npm start
```

### Windows PowerShell

프로젝트 폴더에서 실행합니다.

```powershell
node --version
npm.cmd --version
npm.cmd test
npm.cmd start
```

기본 접속 주소는 **http://localhost:3000** 입니다. 종료하려면 터미널에서 `Ctrl+C`를 누릅니다. npm 없이 `node server.js`로 실행해도 됩니다.

## 통화 테스트

1. 접속 후 닉네임과 방 이름을 입력합니다.
2. `통화방 입장`을 누르고 마이크 접근을 허용합니다.
3. 다른 브라우저 창 또는 기기에서 같은 방 이름으로 입장합니다.
4. 양쪽 연결 상태가 `연결됨`인지 확인하고 목소리가 전달되는지 확인합니다.
5. `통화 나가기`를 눌러 참여자 목록에서 사라지고 마이크 사용이 종료되는지 확인합니다.

같은 PC의 두 창은 연결 확인에 사용할 수 있습니다. 실제 양방향 음성 확인에는 별도 기기 두 대를 사용하세요. 에코 제거를 꺼 두었으므로 헤드폰 사용을 권장합니다. 방 이름은 대소문자를 구분하며 문자·숫자·밑줄·하이픈 1~40자, 닉네임은 공백 제거 후 1~24자입니다.

## 환경 변수

| 변수 | 기본값 | 설명 |
| --- | --- | --- |
| `HOST` | `127.0.0.1` | 서버 바인딩 주소. 기본값은 로컬 접속만 허용 |
| `PORT` | `3000` | Node.js HTTP 서버 포트 |
| `ICE_SERVERS` | `[{"urls":"stun:stun.l.google.com:19302"}]` | 브라우저에 전달할 STUN/TURN 서버의 JSON 배열 |

Linux에서 포트를 바꾸는 예:

```bash
HOST=127.0.0.1 PORT=3000 npm start
```

TURN 설정 예시입니다. 주소와 계정을 실제 값으로 바꾸세요.

```bash
export ICE_SERVERS='[{"urls":"stun:stun.l.google.com:19302"},{"urls":"turn:turn.example.com:3478","username":"example-user","credential":"example-password"}]'
npm start
```

PowerShell에서는 다음 형식으로 지정합니다.

```powershell
$env:ICE_SERVERS = '[{"urls":"stun:stun.l.google.com:19302"},{"urls":"turn:turn.example.com:3478","username":"example-user","credential":"example-password"}]'
npm.cmd start
```

`ICE_SERVERS`는 `/api/config`를 통해 브라우저에 제공됩니다. 설정한 TURN 자격 증명도 클라이언트에 노출되므로, 공개 서비스에서는 단기 자격 증명 발급 방식이 필요합니다. 환경 변수와 실제 계정 정보는 Git에 커밋하지 마세요.

## OCI 운영 구성

권장 연결 경로는 다음과 같습니다.

```text
브라우저 ── HTTPS 443 ── OCI의 TLS 프록시 ── HTTP 127.0.0.1:3000 ── Node.js
브라우저 ══ WebRTC 음성 ══ 상대 브라우저
                   └── 직접 연결이 불가능하면 TURN으로 중계
```

1. 공인 IP와 인터넷 접속 경로가 있는 OCI Linux 인스턴스를 준비합니다.
2. Node.js 24.15.0을 설치하고 위 실행 명령으로 앱을 확인합니다.
3. 도메인·유효한 TLS 인증서를 준비하고 HTTPS 프록시에서 Node.js로 요청을 전달합니다.
4. OCI의 적용 중인 보안 목록 또는 NSG와 OS 방화벽에서 웹 서비스용 TCP 80·443을 허용합니다. 80은 HTTP 리다이렉트 또는 인증서 발급 방식에 따라 사용합니다.
5. Node.js는 기본값 `127.0.0.1:3000`으로 유지하고, systemd 등으로 단일 프로세스를 상시 실행합니다.
6. 다른 네트워크의 두 기기로 통화를 확인하고 직접 연결이 막히면 TURN을 추가합니다. TURN 포트 및 릴레이 포트 범위는 선택한 TURN 설정에 맞춰 OCI와 OS 방화벽 양쪽에 허용해야 합니다.

**마이크 접근에는 HTTPS가 필요합니다.** 개발용 `localhost`는 예외지만 `http://공인IP:3000` 또는 일반 LAN IP의 HTTP 접속만으로는 마이크를 사용할 수 없습니다. `HOST=0.0.0.0`으로 변경해도 이 제약은 해결되지 않습니다. [마이크 API 보안 조건](https://developer.mozilla.org/en-US/docs/Web/API/MediaDevices/getUserMedia).

프록시는 아래 조건을 충족해야 합니다.

- 원래 `Host` 헤더를 Node.js에 전달합니다. POST 요청은 브라우저 `Origin`의 호스트와 비교합니다.
- `/api/events` SSE 응답을 버퍼링하거나 캐시하지 않습니다.
- 장시간 연결을 허용합니다. 서버는 10초마다 SSE heartbeat를 전송합니다.
- WebSocket을 사용하지 않으므로 WebSocket Upgrade 설정은 필요하지 않습니다.

OCI에 프록시와 앱을 올리는 것만으로 모든 네트워크에서 음성 연결이 보장되지는 않습니다. [OCI 보안 목록](https://docs.oracle.com/en-us/iaas/Content/Network/Concepts/securitylists.htm), [WebRTC TURN 안내](https://webrtc.org/getting-started/turn-server).

## 구조 및 운영 제한

```text
package.json          버전·명령·Node.js 요구사항
server.js             정적 파일, 방 관리, SSE 및 연결 신호 API
public/index.html     통화 화면
public/style.css      화면 스타일
public/app.js         마이크, WebRTC 연결 및 통화 종료 처리
test/server.test.js   실제 HTTP 서버 통합 테스트
test/client.test.js   모의 마이크·WebRTC를 사용하는 클라이언트 테스트
artifacts/            화면 미리보기
```

여러 명의 통화는 참여자 쌍마다 연결하는 mesh 방식입니다. 방의 참여 인원이 늘면 각 클라이언트의 연결 수와 업로드 사용량도 늘어납니다. 미디어를 중앙에서 전달하는 SFU 서버는 없습니다.

방과 세션은 서버 메모리에만 저장됩니다. 서버를 재시작하면 통화 세션이 종료되며, 여러 Node.js 프로세스로 방 상태를 공유하지 않습니다. 현재는 **단일 프로세스**로 운영해야 합니다. 서버 연결이 끊기면 마이크를 종료하고 다시 입장하도록 안내하며, 자동 재입장은 구현하지 않았습니다.

로그인·방 비밀번호·방 소유자 권한은 없습니다. 방 이름을 아는 사람은 입장할 수 있습니다.

## 검증 상태

**현재 저지연 변경 검증:** Node.js 24.15.0의 Windows 및 OCI Ubuntu x64에서 테스트 **17개 통과**. 실제 내장 브라우저의 양쪽 음성 수신기에서 `jitterBufferTarget=20` 적용을 확인했습니다. 5초간 합성 음원 검사에서 구간 평균 버퍼 대기는 양쪽 **34.0ms**, 패킷 손실은 **0개**였습니다. 재생 중단 횟수는 해당 브라우저 통계에 없어 판정하지 않았습니다. 이 수치는 로컬 합성 음원 결과이며 실제 두 PC의 전체 지연이나 개선 폭을 보장하지 않습니다. [측정 결과](artifacts/low-delay-20ms.json), [OCI 테스트 로그](artifacts/low-delay-oci-tests.txt), [검증 화면](artifacts/low-delay-20ms.jpg).

새로고침·재입장 후 30~60초 이상 말하면서 Edge의 `edge://webrtc-internals/`에서 버퍼 지연과 끊김을 비교하세요. 구간 평균 버퍼 대기는 `ΔjitterBufferDelay / ΔjitterBufferEmittedCount × 1000`으로 계산합니다. 값이 계속 증가하는 `packetsLost`, `interruptionCount` 및 `totalInterruptionDuration`도 함께 확인합니다. 통계 미지원 값을 0으로 간주하지 않습니다.

**510kbps 변경 당시 검증:** Node.js 24.15.0 / Windows에서 테스트 **14개 통과**. 실제 내장 브라우저의 양쪽 Opus SDP와 `RTCRtpSender` 설정에서 **510000 bits/s** 적용을 확인했습니다. 2초간 합성 음원 실측 수신량은 각각 **261.12 / 261.13kbps**였으며, 설정값과 실제 전송량이 다름을 확인했습니다. 이 값은 실제 두 PC의 마이크 통화 측정값과 별개입니다. [510kbps 결과](artifacts/bitrate-510kbps.json), [510kbps 검증 화면](artifacts/bitrate-510kbps.jpg).

이전 96kbps 설정에서는 같은 합성 음원 검사로 양쪽 약 **96.35kbps**를 측정했습니다. [96kbps 결과](artifacts/bitrate-96kbps.json), [96kbps 검증 화면](artifacts/bitrate-96kbps.jpg).

OCI에는 기존 앱 파일을 백업한 뒤 프런트엔드 변경을 반영했으며, 외부 HTTPS에서 제공되는 `app.js`의 해시가 검증한 파일과 일치함을 확인했습니다. 서버 프로세스를 재시작하지 않았습니다. Git 커밋·푸시는 별도로 필요합니다.

**아래 21개 조합의 로그는 96kbps 변경 이전의 기본 통화 구현에 대한 검증 기록입니다.**

2026-10-06 기준, 아래 **7개 Node.js 버전 × Windows·Ubuntu WSL·OCI 실서버의 3개 환경**, 총 **21개 조합**에서 각각 자동 테스트 **11개 통과**를 확인했습니다. 각 버전의 공식 포터블 배포본을 사용했으며, 다운로드한 아카이브의 SHA-256을 공식 `SHASUMS256.txt`와 대조했습니다. 시스템에 설치된 Node.js를 교체하지 않았습니다.

| Node.js | 번들 npm | Windows 11 x64 | Ubuntu 24.04.4 WSL x64 | OCI Ubuntu 24.04.5 x64 |
| --- | --- | --- | --- | --- |
| 22.0.0 | 10.5.1 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 22.7.0 | 10.8.2 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 22.8.0 | 10.8.2 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 22.23.0 | 10.9.8 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 23.6.0 | 10.9.2 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 24.0.0 | 11.3.0 | 11/11 통과 | 11/11 통과 | 11/11 통과 |
| 24.15.0 | 11.12.1 | 11/11 통과 | 11/11 통과 | 11/11 통과 |

버전별로 `npm test`와 `node --test`를 모두 실행했으며, 별도로 `node server.js` 실제 시작, HTTP 화면과 설정 API 응답, `HOST`·`PORT`·`ICE_SERVERS` 적용, 내장 모듈 import, 서버·클라이언트 구문 검사도 통과했습니다. Node.js 22 계열의 경계 버전과 기존 문서의 옵션 변경 시점인 23.6.0을 포함한 대표 버전 검증이며, 22 이상 모든 패치 버전을 전수 검사한 것은 아닙니다.

상세 결과: [Windows 로그](artifacts/compatibility-windows.json), [Linux 로그](artifacts/compatibility-linux.json), [OCI 로그](artifacts/compatibility-oci.json), [수정 전 실패 기록](artifacts/compatibility-windows-before.json).

### OCI 실서버 검증 상태

제공된 OCI 인스턴스의 **Ubuntu 24.04.5 / x64 / Python 3.12.3** 환경에서 위 7개 버전의 검증을 완료했습니다. 시스템에 `node`·`npm`은 없어 임시 폴더의 공식 포터블 런타임을 사용했습니다. 모든 버전에서 테스트, 실제 서버 시작, HTTP 페이지·설정 API, 환경 변수 적용, 내장 모듈 및 구문 검사가 통과했습니다.

약 1GB RAM의 서버에서 준비 작업의 메모리 사용을 줄이기 위해 스트리밍 다운로드·gzip 압축 해제를 사용했습니다. 검증 작업에만 임시 systemd 서비스를 사용하고 **MemoryMax=192MiB, CPUQuota=25%, 다운로드 동시 작업 1개**로 제한했습니다. 런타임 준비 중 메모리 제한으로 종료된 시도 이후 방식을 수정해 최종 실행은 정상 종료했습니다. 이 제한은 검증 프로세스에만 적용하며 앱의 최소 RAM 명세를 의미하지 않습니다. 서버 재부팅, 시스템 Node.js 설치, 운영 서비스 설정 변경 및 공개 배포는 수행하지 않았습니다.

결과 로그를 로컬 프로젝트로 가져온 뒤 OCI의 임시 테스트 폴더를 삭제했고, 임시 systemd 서비스가 제거된 상태와 SSH 정상 응답을 확인했습니다.

버전 검증 도구는 기본적으로 런타임을 한 번에 하나씩 준비하며, 다운로드를 파일로 직접 저장해 전체 아카이브를 메모리에 올리지 않습니다. Linux에서는 gzip 배포본을 사용해 xz 해제 시의 큰 메모리 사용을 피합니다. 완료 표시가 없는 중단된 압축 해제 캐시는 다시 준비하도록 처리합니다. 작은 인스턴스에서는 `--download-workers 1`을 유지하세요.

- 서버: 정적 파일 제공, 방 입장과 인원 제한, 신호 전달 및 대기열, 방 간 격리, 세션 인증, 나가기와 SSE 종료 시 참여자 제거
- 클라이언트: 연결 협상, 이른 ICE 후보 버퍼링, 음성 처리 옵션 비활성화 요청, 입장 실패·통화 종료·서버 연결 종료 시 자원 해제, 이미 나간 참여자의 신호 무시
- 브라우저: 초기 화면 및 잘못된 방 이름 입력 안내 확인. Codex 내장 브라우저에서 실제 앱의 두 클라이언트가 합성 음원으로 WebRTC 연결 및 양방향 음성 RTP 패킷 수신, SSE 신호 교환, 나간 참여자 제거 및 송신 트랙 종료를 통과했습니다. [브라우저 결과](artifacts/compatibility-browser.json), [검증 화면](artifacts/compatibility-browser.jpg).

클라이언트 단위 테스트는 마이크와 WebRTC를 모의 객체로 대체합니다. 별도의 브라우저 진단은 실제 WebRTC를 사용하되 마이크 입력을 `AudioContext`의 합성 음원으로 대체합니다. **실물 마이크와 스피커를 사용하는 기기 두 대의 통화, 독립 Chrome·Edge·Firefox·Safari, ARM64, 공개 HTTPS 경로 및 TURN 중계는 아직 직접 검증하지 않았습니다.**

### 버전 검증 재실행

다음 도구는 개발·검증용이며, 앱 실행에는 필요하지 않습니다. Python 3.12 이상 및 공식 Node.js 배포 서버에 대한 인터넷 접속이 필요합니다. 캐시와 결과 로그를 생성하고, 서버 시작 테스트는 루프백 주소의 임시 포트만 사용합니다. 시스템 Node.js 설치는 변경하지 않지만 다운로드·압축 해제에는 CPU, 메모리와 디스크 여유가 필요합니다.

```powershell
python scripts/verify_versions.py --output artifacts/compatibility-windows.json
```

```bash
python3 scripts/verify_versions.py --cache-dir /tmp/voice-room-node-compat --output artifacts/compatibility-linux.json
```

브라우저 검증용 서버는 운영 앱과 분리되어 있고 localhost:3001에서만 실행합니다.

```bash
node scripts/browser-check.mjs
```

http://localhost:3001/diagnostic 에 접속해 `호환성 테스트 실행`을 누릅니다. 실제 마이크 권한을 요청하지 않으며, 결과를 `artifacts/compatibility-browser.json`에 저장합니다. `npm start`는 이 진단 페이지를 제공하지 않습니다.
# 통화 중 디버그 확인

## 웹 저지연 실험: 패킷 간격과 마이크

입장 전 **음성 패킷 간격 요청**에서 10ms(기본 실험값) 또는 기존 20ms를 선택합니다. 양쪽 모두 같은 값을 선택하고 재입장하세요. Opus 오디오 SDP의 `ptime`·`maxptime`에 요청을 넣으며, 브라우저가 SDP를 거부하면 기존 협상으로 복구합니다. SDP 수락과 실제 적용은 별개이므로 디버그의 **수신 패킷 빈도**로 확인하세요. 계속 말할 때 약 100개/s는 10ms, 약 50개/s는 20ms에 해당합니다. **평균 수신 간격 추정**은 관측 시간/수신 패킷 수이며, 정확한 인코더 프레임 길이나 전체 음성 지연은 아닙니다. 무음·손실·도착 몰림의 영향을 받습니다.

마이크에는 `latency: { ideal: 0 }`, `sampleRate: { ideal: 48000 }`을 요청합니다. 장치·브라우저가 지원하는 범위에서만 적용되며 강제 조건이 아닙니다. 디버그에는 로컬 마이크 `getSettings()`에서 보고하는 지연·샘플레이트·채널 수만 표시합니다. 실제 마이크가 없는 합성 음원 검증으로 장치 지연 개선을 입증할 수는 없습니다.

2026-10-06 Chromium 154 합성 음원, 같은 PC에서 양방향 연결 후 5초 대기하고 10초 구간 측정: 20ms 요청은 약 51개/s·실제 버퍼 31.4/34.4ms, 10ms 요청은 약 101개/s·실제 버퍼 31.7/34.7ms였습니다. 패킷 간격 단축은 확인됐지만 이 실험에서 버퍼 감소는 확인되지 않았습니다. 양쪽 패킷 손실 0, 끊김 카운터는 미제공입니다. 실제 Edge 두 PC 결과와 전체 음성 지연을 보장하지 않습니다. 원본은 `artifacts/packet-20ms.json`, `artifacts/packet-10ms.json`입니다. Windows Node 24.15.0과 OCI Node 24.15.0에서 전체 테스트 24개 통과. OCI 적용은 서버 재시작 없이 원본 백업 후 수행했습니다.

참고: [Opus RTP 패킷 간격 규격](https://www.rfc-editor.org/rfc/rfc7587.html), [마이크 지연 제약](https://developer.mozilla.org/en-US/docs/Web/API/MediaTrackConstraints/latency).

**수신 버퍼 비교**에서 `0 / 5 / 10 / 20ms`를 선택하면 선택된 통화 탭의 수신기에 재입장 없이 적용합니다. 적용 결과와 수신기 읽기값을 확인한 뒤 10~30초 동안 실제 대기와 소리를 비교하세요. 내 PC가 받는 방향만 변경하므로 반대 방향은 상대 PC에서도 설정해야 합니다. 새로운 상대에게도 유지되며, 퇴장·새로고침 후 기본 20ms로 돌아갑니다. 0ms는 최소 버퍼 요청이며 실제 지연 0ms를 보장하지 않습니다. 끊기면 20ms로 복원하세요.

통화 페이지 아래의 **통화 디버그 ↗** 링크 또는 `/debug` 주소를 같은 브라우저의 별도 탭으로 여세요. 통화 페이지는 업데이트 후 새로고침하고 재입장해야 합니다. 디버그 페이지 자체는 마이크를 켜거나 방에 참여하지 않습니다.

상대별 수신 비트레이트, 실제·목표 버퍼 대기, `RTCRtpReceiver.jitterBufferTarget` 읽기값, 앱 요청 버퍼, 선택된 연결의 RTT, 지터, 손실 및 끊김을 표시합니다. 비트레이트·버퍼·손실·추가 끊김은 직전 갱신 이후 구간을 계산하며, 누적 끊김은 별도로 표시합니다. 첫 측정과 미지원 값은 `—`입니다. 설정한 20ms는 힌트이며 실제 대기나 전체 음성 지연을 보장하지 않습니다.

같은 출처·브라우저 프로필의 통화 탭만 표시되며, 여러 탭은 선택할 수 있습니다. 통계는 브라우저 안에서만 전달됩니다. **현재 통계 JSON 저장**으로 표시된 값을 파일로 저장할 수 있습니다. 인증 토큰, SDP 전체, 후보 IP 주소, 음성 데이터는 포함하지 않습니다. 닉네임·방 이름은 포함됩니다.
