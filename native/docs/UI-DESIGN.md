# Figma Make UI 적용

사용자가 전달한 `src.zip`의 App.tsx/index.css를 기준으로 기존 WinForms에 입장·통화·장치 설정·진단 화면을 적용했다. React/Vite 실행 환경이나 웹 렌더러를 추가하지 않는다. 통화 엔진·Opus·WASAPI·프레임·수신 여유·연결/복구 정책은 이 UI 변경으로 수정하지 않았다.

## 화면과 기능

- 입장: 닉네임/방 이름, 참가·새 방, 연결 설정. 서버 입력은 설정으로 이동한다.
- 통화: 실제 방 이름/현재 인원, 나/상대 카드, 상대 마이크 상태, 상대별 펼침 볼륨0–200%·내 쪽 음소거, 마이크·설정·연결 상세·나가기. 작은 창에서는 참가자 목록이 스크롤되며 하단 조작부를 유지한다. 상대 마이크 상태는 기존 암호화 채널의 선택적 필드로 공유하며, 구버전/미수신/3초 초과/연결 끊김은 미확인으로 표시한다.
- 설정: 실제 장치 목록, 새로고침, 현재 서버 주소. 통화 중 장치 변경은 기존의 퇴장/재참가 동작이다.
- 진단: 첫 상대 기준 실제 경로·신선한 RTCP RTT/수신 손실·지터·PLC 오디오 시간·앱 큐·입력/수신 레벨. 상대가 없으면 미측정이다. 전체 음성 지연으로 표시하지 않는다.
- 고급 도구: 기존 코덱/모드/패킷 기록/측정·UDP/경로 검사·JSON 저장·출력 재시작은 상세 영역에 보존했다. 펼치면 지표 카드를 숨겨 도구 공간을 확보한다.

시안의 가상 서버 주소·고정 참가자/진단 숫자·상태 선택 메뉴는 일반 실행에 넣지 않았다. 실제 서버는 기존 사용자 설정/기본 HTTPS 주소를 사용한다. 상대 마이크 상태 공유는 후속 구현이며 자동 발화 감지는 아직 제공하지 않는다.

색은 원본의 base `#111316`, window `#171a1f`, surface `#1d2127`, accent `#70d6a2` 등을 사용한다. 원본 CSS의 대체 글꼴인 맑은 고딕을 사용하며 온라인 폰트를 내려받지 않는다. 창 테두리는 Windows 표현을 유지한다. 콤보박스는 네이티브 선택 동작 위에 어두운 항목/화살표를 표시하고, 토글은 둥근 스위치로 그린다. 비활성 설정도 회색 글씨로 읽을 수 있다.

진단/로그 본문에는 얇은 전용 스크롤바를 사용한다. 갱신 시 양방향 읽기 위치와 텍스트 선택을 보존한다. 로그는 맨 아래에서 읽고 있을 때만 새 항목을 따라가며, 이전 내용을 읽으면 위치를 유지한다. 2,000개 제한으로 오래된 로그를 제거할 때도 남아 있는 읽기 행을 기준으로 보정한다. 설정/참가자 패널의 네이티브 스크롤바에는 어두운 Windows 테마를 적용한다. 통화 헤더의 복사 버튼·상태·인원은 방 이름 옆에 배치하며 좁은 영역에서는 두 줄로 배치한다. [보완·검증 보고서](reports/EXTREME-120.md).

## 실행과 미리보기

```powershell
./native/run.ps1
```

기존 dist는 별도 게시 전까지 변경하지 않는다. 다음 명령은 네트워크/마이크를 열지 않는 화면 렌더링이다. call 이미지는 명시적인 시험 데이터이며 실제6인 통화 검증이 아니다.

```powershell
./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.exe --ui-preview ./native/.local/join.png
./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.exe --ui-preview-call ./native/.local/call.png
./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.exe --ui-preview-call-small ./native/.local/call-small.png
./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.exe --ui-preview-settings ./native/.local/settings.png
./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.exe --ui-preview-diagnostics ./native/.local/diagnostics.png
```

미리보기는 기본/최소 크기와 배치 검사용이다. 프로그램이 종료되기 전에 파일을 읽지 않도록 호출 환경에 따라 Start-Process -Wait를 사용한다. 출력 경로의 부모 폴더를 먼저 만든다. [적용·검증 보고서](reports/EXTREME-119.md).
