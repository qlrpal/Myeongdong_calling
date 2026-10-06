# 명동 콜링 Native

Windows 네이티브 음성 통화 앱과 Ubuntu 연결 관리 서버입니다.
현재 개발 기준점은 **0.3.3**입니다. 이 `native` 브랜치는 네이티브 프로젝트를 관리합니다.

- [실행·빌드·진단 안내](native/README.md)
- [완료 내역·검증 범위·다음 개발 순서](native/STATUS.md)

```powershell
node native/server/server.js
./native/run.ps1
```

배포 빌드와 ZIP 생성:

```powershell
./native/build.ps1 -Package
```

소스는 `native/VoiceNative/`, 검증 도구는 `native/TransportProbe/`, 서버는 `native/server/`입니다.
실행 결과는 `native/dist/`, 배포 ZIP은 `native/dist.zip`에 생성하며 Git에 포함하지 않습니다.
기존 웹 자료는 로컬 `web/`에 보관하고, 이 브랜치에서는 Git에 추가하지 않습니다.
