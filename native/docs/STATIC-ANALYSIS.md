# 코드 정리와 정적 분석 · 2026-10-16

## 현재 Git 반영 기준 · 0.5.5 / 2026-10-17

현재 기능·검증 기준은 [0.5.5 적용 보고서](reports/LOW-LATENCY-055.md)입니다. 아래 0.4.9 분석 내용은 당시 정리 기록이며 현재 구현 범위를 뜻하지 않습니다.

Git 대상은 앱(`VoiceNative/`), 서버(`server/`), 재현 가능한 검증 도구(`TransportProbe/`와 PowerShell 스크립트), 문서(`docs/`), 솔루션·프로젝트·의존성 잠금 파일입니다. 검증 소스도 함께 올려야 새 체크아웃에서 `verify.ps1`을 실행할 수 있습니다.

빌드 결과(`dist/`, ZIP, `bin/`, `obj/`), 캐시, `.local/` 실험 자료, 개인 진단 JSON·ETL·패킷 캡처·덤프, 환경 파일·키, 이전 `web/`은 제외합니다. 원본은 로컬에 보존합니다. 기존에 추적 중인 생성 파일이 없는지와 신규 파일 후보의 암호·개인 키 패턴을 확인했고, `git diff --check`를 통과했습니다. 이는 전체 보안 감사 결과가 아닙니다.

저장소 루트에서 반영할 파일을 검토할 때는 다음 명령을 사용합니다.

```powershell
git status --short
git add .gitignore .gitattributes .editorconfig README.md native/
git diff --cached --stat
git diff --cached --check
```

커밋 메시지 예: `feat: adopt low-latency native playout policy (0.5.5)`

## 이전 정리 기록 · 0.4.9

0.4.9의 통화 파라미터와 기존 코덱 기본값을 유지한 정리입니다. 새 지터 버퍼·음질 처리·OCI 운영 배포는 포함하지 않습니다. 커밋·푸시·스테이징은 수행하지 않았습니다.

## 정리 내용

- 앱 소스를 Audio/Calling/Diagnostics/UI/Properties로 분류했습니다. namespace와 공개 통화 인터페이스는 유지했습니다.
- 자동 통화·A/B/A 실험은 앱 실행 파일에서 TransportProbe/Automation으로 옮겼습니다. 검증 도구는 독립 검사, 실제 전송 검사, 자동 실험으로 분리했습니다.
- 진단 JSON 옵션을 공유하고, 문자열 접두사 비교를 Ordinal로 명시했습니다. 화면 숫자는 사용자 문화권을 명시하고, 검증 실패 예외는 InvalidOperationException으로 구체화했습니다.
- 종료 정리는 취소된 이벤트 루프의 토큰으로 취소되지 않도록 CancellationToken.None을 명시했습니다. 자동 통화 검사는 연결/미디어 실패 시 보고서와 종료 코드1을 남깁니다.
- 권장 .NET 분석 설정, 솔루션, 일관된 포맷과 줄바꿈 설정, 패키지 잠금 파일 및 verify.ps1을 추가했습니다.
- 누적 진행 기록과 비교 보고서는 docs로 옮기고 현재 README를 줄였습니다. 과거 HISTORY는 현재 상태의 기준이 아닙니다.
- 오래된 ZIP은 .local/archive/old-packages에 보존했습니다. 새 배포는 빈 staging에서 만들며 기존 dist와 그 안의 사용자 진단 파일도 archive에 보존합니다. 진단 자료가 새 ZIP에 섞이는 것을 막습니다.
- 캐시·빌드·이전 웹·진단 원본·환경 파일·키 파일을 Git에서 제외합니다.

## 분석과 검사 결과

| 검사 | 결과 |
|---|---|
| .NET latest-recommended 분석 | 정리 전 앱45개·도구76개 경고. 정리 후 경고0/오류0 |
| 경고를 오류로 처리한 Release 빌드 | 통과 |
| 코드 포맷·불필요 using 정리 | 수행, 최종 포맷 검증 통과 |
| 독립 회귀 검사14개·Opus 비교 | 통과 |
| JavaScript 구문·서버 검사7개 | 통과 |
| LAN UDP·실제 ICE/DTLS/SRTP·RTCP·데이터 채널 | 통과 |
| 자동 통화 실패 경로 | 종료 코드1 및 실패 보고서 확인 |
| PowerShell 파싱·Bash 구문 | 통과 |
| NuGet 직접/전이 의존성 취약점 조회 | 현재 피드에서 알려진 취약 패키지 보고 없음 |
| Git 후보의 private-key/암호 패턴 | 발견 없음 |

NuGet 조회는 실행 시점의 공지 기준이며 코드 보안 감사나 모든 취약점 부재를 보증하지 않습니다. 서버 JavaScript는 구문 검사와 실행 회귀 검사 범위이며 별도 ESLint/타입 분석은 도입하지 않았습니다.

## 명시적으로 유지한 분석 예외

.editorconfig에 파일 단위로2개 규칙만 제외하고 이유를 기록했습니다.

- CallForm의 CA1859: 구체 엔진 형식을 쓰라는 미세 성능 제안 대신 ICallEngine 경계를 유지합니다.
- WindowsAudioDevice의 CA1001: native 자원은 비동기 Close에서 해제합니다. WaitHandle을 생성하지 않는 관리형 SemaphoreSlim은 대기 중인 복구 작업을 위해 남기는 기존 수명 정책을 유지합니다. 무조건 Dispose로 바꾸는 작업은 수명/경합 검증과 함께 별도로 진행해야 합니다.

따라서 경고0은 이 두 예외를 포함한 설정의 결과입니다. 정적 분석이 현재 종료/재연결 경합, 전체 실시간 성능 또는 실제 음질의 정확성을 증명하는 것은 아닙니다.

## 남은 기술 과제

- 수신 시간축 버퍼/PLC, 순간 버퍼 부족과 큐 변동.
- 참가·퇴장·장치 복구가 겹치는 상황의 상태/취소 수명 검증과 예외 시 부분 생성 자원 소유권 검토.
- 측정 분해능 개선, 출력 제출→루프백 검출 지연의 원인 분리.
- 익명 TURN 자격 발급의 접근 제한, 장기 통화 자격 갱신, UDP 차단망 대응.

이 항목을 이번 정리에서 구현 완료로 표시하지 않습니다. 서버 원격 프로세스와 OCI 규칙은 변경하지 않았습니다.

## 재현과 Git

```powershell
./native/verify.ps1
./native/verify.ps1 -NoRestore -Integration
./native/build.ps1 -NoRestore -Package
git diff --check
git add .gitignore .gitattributes .editorconfig README.md native/
git diff --cached --stat
```

마지막 두 Git 명령은 사용자가 검토 후 실행합니다. 기본 verify는 마이크/스피커를 열지 않습니다. Integration은 소켓을 사용하며, 두 PC 자동 실험은 명시적으로 실행해야 합니다. Git 상태에서 기존 파일 삭제와 새 경로 추가가 함께 보이는 것은 폴더 분류 때문이며 git add 후 Git이 이동 여부를 판별합니다.

상세 실행 로그와 개인 원본은 .local/reports에 보존하고 커밋하지 않습니다. 배포 파일은 dist/dist.zip이며 Git 대상은 소스·검사·문서·잠금 파일입니다.
