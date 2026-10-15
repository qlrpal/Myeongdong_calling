# 명동 콜링 Native

지연을 우선하는 Windows 음성 통화 앱과 OCI Ubuntu 연결 관리/TURN 서버입니다. 현재 작업 버전은0.4.9입니다.

```powershell
./native/run.ps1
./native/build.ps1 -Package
./native/verify.ps1
```

- [실행·빌드·소스 구조](native/README.md)
- [정적 분석·Git 준비 상태](native/docs/STATIC-ANALYSIS.md)
- [서버 운영](native/server/README.md)
- [최근 두 PC 비교 결과](native/docs/reports/OPUS-AB-REPORT.md)

소스와 재현 도구는 `native/`에 있습니다. 이전 웹은 로컬 `web/`에 보존하며 이 브랜치에 추가하지 않습니다. 배포 파일·진단 원본·실험 자료·캐시·비밀 키는 Git에서 제외합니다.
