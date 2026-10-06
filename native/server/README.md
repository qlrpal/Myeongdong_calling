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
TURN 자격 증명은 참가 앱에 전달되므로 운영 서비스에서는 만료되는 자격 증명 발급을 별도로 구현해야 합니다.

```bash
npm test
```

Ubuntu 서버에 자동 배포하거나 기존 프로세스를 교체하지는 않았습니다.
