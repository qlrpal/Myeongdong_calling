# 네이티브 앱 연결 관리 서버

Windows와 Ubuntu에서 Node.js 22 이상으로 실행합니다. 외부 npm 패키지는 없습니다.
이 폴더만 복사해 실행할 수 있으며 `web/`에 의존하지 않습니다.

저장소 루트에서 로컬 실행:

```bash
node native/server/server.js
```

Ubuntu에서 외부 연결 수신:

```bash
cd native/server
HOST=0.0.0.0 PORT=3000 node server.js
```

앱의 서버 주소에는 서버의 HTTPS 주소를 입력합니다. HTTPS 프록시는 별도 구성합니다.
상태 확인은 `GET /health`입니다. 연결 API는 `/api/config`, `/api/join`,
`/api/events`, `/api/signal`, `/api/leave`입니다.
SSE 프록시는 응답 버퍼링을 끄고 장기 연결을 허용해야 합니다.

`ICE_SERVERS` 환경 변수로 STUN/TURN 서버 목록을 JSON 배열로 설정할 수 있습니다.
미설정 시 기본 STUN만 제공합니다. 이 프로세스 자체는 TURN 서버가 아닙니다.
`TURN_URLS`와 `TURN_SECRET_FILE`을 지정하면 coturn REST 방식의 만료 자격 증명을 매 요청마다 발급합니다. 공유 secret은 서버에만 저장하고 앱에는 만료 username/credential만 전달합니다. 기본 유효 기간은1시간이며 `TURN_TTL_SECONDS`로600~86400초를 지정할 수 있습니다.

```bash
npm test
```

## OCI 운영 설정 · 2026-10-07

OCI Ubuntu에 coturn과 `voice-native.service`를 배포했습니다. 두 서비스와 `netfilter-persistent`는 자동 시작을 사용합니다. 기존 Nginx HTTPS443은 유지하고 연결 서버는127.0.0.1:3000으로 제한했습니다.

- TURN 주소: `turn:qlrpal.ddns.net:3478?transport=udp`
- OCI 보안 목록/NSG 및 Ubuntu INPUT: UDP3478, UDP49160–49259 수신 허용. OCI 규칙은 사용자가 추가했고 Ubuntu 규칙은 배포 스크립트가 저장합니다.
- 공인/내부 주소 매핑:168.107.31.70 /10.0.0.189.
- `/etc/voice-native/turn.secret`: 서버 전용 공유 secret. Git이나 앱에 복사하지 않습니다.
- `/etc/turnserver.conf`, `/etc/voice-native/server.env`: coturn/연결 서버 설정.
- 이전 파일은 `/var/backups/voice-native/`에 백업했습니다.

설치 후 UDP TURN-only 검사에서 relay→relay ICE/DTLS,10ms SRTP 음성 디코딩, 양방향 데이터 채널과 RTCP가 통과했습니다. 시험 RTCP RTT는21.54/22.99ms, 보고 손실0%였습니다. 동일 PC의 두 피어가 실제 OCI를 거친 검사이며 다른 회선의 친구와 실제 통화는 별도로 확인합니다.

이번 배포는 UDP TURN입니다. UDP 자체가 차단된 회사/학교망의 TCP/TLS 우회,1시간을 넘는 통화의 자격 갱신, 익명 공개 서비스의 계정/호출 제한은 후속 구현 대상입니다. 현재 자격 발급 API는 익명 방 참가 앱을 위한 공개 API이며 coturn에 세션·대역폭 상한을 적용했습니다.

배포 스크립트는 [deploy/install-turn.sh](deploy/install-turn.sh)입니다. coturn 설치 후 별도 업로드한 `server.js`, `ice-config.js`, `deploy/install-turn.sh` 디렉터리에서 다음과 같이 실행합니다. 마지막 PID는 최초 실행 때 기존 수동 Node 프로세스를 종료할 경우에만 지정합니다.

```bash
sudo bash deploy/install-turn.sh /home/ubuntu/Myeongdong_calling 168.107.31.70 10.0.0.189 qlrpal.ddns.net /home/ubuntu/.nvm/versions/node/v24.15.0/bin/node
sudo systemctl status coturn voice-native
```

Windows 중계 전용 검사:

```powershell
dotnet run --project native/TransportProbe -- --turn-integration https://qlrpal.ddns.net/
```

친구와는 기존 최신 앱에서 `https://qlrpal.ddns.net/` 및 같은 방 이름으로 참가합니다. 로그의 `TURN 중계 사용 가능`은 설정 수신 여부이고 피어의 `직접 연결`/`TURN 중계`가 실제 선택된 경로입니다. 사용자 PC의 공유기 포트포워딩은 필요 없습니다.

자격 증명 형식과 coturn 설정은 [coturn 공식 설명](https://github.com/coturn/coturn/wiki/turnserver)을 참고합니다.

## TURN 읽기 전용 부하 확인

서버의 소스 디렉터리에서 `bash deploy/inspect-turn.sh 10`으로 coturn 서비스 전체의 CPU 코어 사용량과 메모리를 관측할 수 있습니다(1~60초). 설정·방화벽·서비스를 변경하지 않습니다. null은 계측 불가 또는 카운터 초기화를 의미합니다. 결과는 서비스 전체 합계이며 개별 사용자 부하나 400pps 수용량을 검증하지 않습니다.

클라이언트의 선택 후보 UDP 표시는 TURN 클라이언트 구간의 전송 방식까지 증명하지 않습니다. 현재 배포는 UDP TURN URL을 제공하며 실제 우회 경로 검증에서는 URL의 transport와 전송 계측을 함께 확인해야 합니다. 자격 증명을 포함한 전체 설정 응답을 공개 로그에 남기지 마세요.
