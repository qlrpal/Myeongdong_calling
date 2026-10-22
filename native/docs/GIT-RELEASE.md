# Git 소스 배포 · 극저지연 실험 브랜치

현재 브랜치는 `codex/extreme-latency`입니다. 제품 기반 버전0.6.0에 극저지연 실험·송신 수명·할당 감소 변경이 누적된 상태입니다. 새 안정판 태그를 확정하지 않았습니다. [현재 상태](CURRENT-STATUS.md)를 먼저 참고하세요. 소스와 재현 가능한 검사·문서를 함께 커밋하며, 실행 파일 ZIP 배포와는 별도입니다.

## 반영 범위

| 경로 | 내용 |
| --- | --- |
| 루트 README·Git/편집기 설정 | 진입 안내·생성 자료 제외·줄바꿈 규칙 |
| `native/VoiceNative/` | Windows 앱·오디오·통화·진단·UI 및 패키지 잠금 파일 |
| `native/TransportProbe/` | 제품 기능 검사·실제 통신 검사·자동 시험·분석 도구 및 잠금 파일 |
| `native/server/` | 연결 관리 서버·TURN 설치 스크립트·서버 회귀 검사 |
| `native/docs/` | 사용자 가이드·구현 및 검증 요약·과거 기록 |
| `native/*.ps1`, `native/*.cmd`, 솔루션·빌드 설정 | 실행·빌드·검증·외부 시험 진입점 |

이번 변경은 극저지연 프리셋·공유 오디오·진단/분석 도구·송신 수명·패킷 저장소 최적화와 관련 회귀를 포함합니다. 새 소스 파일을 빠뜨리면 빌드 또는 검증이 실패하므로 수정 파일만 골라 올리지 않습니다. 직접 송신 기본값은 유지하며 전용 sender는 비교 옵션입니다.

## 제외·보존

`web/`, `native/.local/`, `native/dist/`, `native/dist.zip`, `bin/`, `obj/`, SDK·NuGet 캐시·node_modules·IDE 사용자 상태, 환경 파일·키, 진단 JSON·로그·ETL·캡처·덤프는 Git 대상이 아닙니다. 생성물과 원본 진단 자료는로컬에 보존합니다. 개인 인계는 `.local/handoff/`로 분리하고 `docs/HANDOFF*.md`도 제외합니다. 공개 실험 요약은 `docs/reports/`에 포함하며 실제 SSID/BSSID 대신 역할 이름을 사용합니다. `.env.example`과 의존성 잠금 파일은 소스에 포함합니다.

## 반영 명령

저장소 루트에서 실행합니다. 아래 커밋·푸시는 사용자가 배포를 결정할 때 수행하는 명령이며 이번 정리 작업에서 실행하지 않았습니다.

```powershell
git status --short
git add .gitignore .gitattributes .editorconfig README.md native/
git diff --cached --stat
git diff --cached --check
git commit -m "perf: optimize experimental native audio pipeline"
git push -u origin HEAD
```

`git add -f`로 제외 파일을 강제 추가하지 않습니다. GitHub Release 등에 실행 파일을 올릴 경우에는 별도로 만든 `native/dist.zip`을 자산으로 사용합니다. 이 ZIP은 .NET 10 Desktop Runtime이 필요합니다.

## 새 체크아웃 검증

.NET 10 SDK·Node.js 22 이상과 Windows 10 2004 이상이 필요합니다.

```powershell
./native/verify.ps1
./native/verify.ps1 -NoRestore -Integration
./native/build.ps1 -NoRestore -Package
```

첫 검사는 잠금 파일에 따라 의존성을 복원합니다. 기본 검사는 마이크를 여는 실통화를 시작하지 않습니다. 네트워크 통합 검사는 localhost UDP와 WebRTC를 사용합니다. 실제 두 PC·장치 시험은 별도 자동 시험 명령이며 환경·방화벽·장치가 필요합니다.

기능·검증·남은 범위는 [현재 상태](CURRENT-STATUS.md)와 [078 보고서](reports/EXTREME-078.md)를 참고합니다. 게임 부하·그룹/외부 장시간 시험은 남았고 집 네트워크 내부 원인 조사는 보류했습니다. 국소 시험 통과를 입→귀 지연 개선이나 안정판 승격으로 바꾸지 않습니다.

## 이번 정리 확인 · 2026-10-22

Git 반영 후 전체 소스 후보213개를 확인했다. 후보에 실행 바이너리·대용량 생성물·개인 키 패턴·개인 사용자 경로·실제 SSID/MAC 식별자가 없고 공개 Markdown 내부 링크가 모두 후보 파일로 연결됨을 확인했다. 패턴 검사이며 전체 보안 감사는 아니다. 개인 인계·이전 dist/ZIP·원본 캡처는 삭제하지 않고 로컬 보존했다. 스테이징·커밋·푸시는 실행하지 않았다.

후보 파일만 Git 제외 임시 소스 폴더로 복사하고 기존 bin/obj 없이 잠금 복원·Release 빌드·제품 회귀·서버8개 검사를 통과했다. 빌드 경고/오류0. 설치된 SDK와 기존 NuGet 캐시를 사용했고 이 복원에서는 취약성 네트워크 조회를 생략했다. 완전히 빈 캐시·다른 PC 검증이나 실행 파일 패키징 시험은 아니다. 복사 이후에는 이 검증 결과를 적는 문서만 갱신했다.

## 이전 정리 확인 · 2026-10-21

Git 반영 후보 174개 파일에서 실행 파일·캐시·진단 원본 혼입이 없고, 개인 키 및 제공받은 시험용 SSH 비밀번호 문자열이 없는지 확인했습니다. 주요 진입 문서의 상대 링크와 `git diff --check`도 통과했습니다. 패턴 검사이며 전체 보안 감사를 뜻하지 않습니다.

후보 파일만 `.local/` 아래 별도 소스 폴더에 복사해 기존 `bin/obj` 없이 잠금 의존성 복원·Release 빌드를 수행했습니다. 기존 NuGet 캐시를 사용했고 이 복원에서는 취약성 네트워크 조회를 생략했습니다. 빌드 경고·오류 0, 제품 기능 검사와 서버 8개 검사를 통과했습니다. 별도 PC·빈 캐시 환경의 검증과는 구분합니다.
