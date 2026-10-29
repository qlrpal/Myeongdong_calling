# C# 빌드·패키징 도구

클라이언트 빌드는 **얇은 `build.ps1` → C# `BuildTools` → dotnet publish** 구조다. .NET10 SDK와 Windows PowerShell을 사용하며 Node나 추가 NuGet 라이브러리를 요구하지 않는다. 서버와 기존 자동 검사 실행기는 계속 Node를 사용한다. 도구는 통화 앱에서 참조하거나 배포 ZIP에 포함하지 않는다.

## 실행

기존 진입점/옵션을 유지한다.

```powershell
./native/build.ps1 -Beta -Package
./native/build.ps1 -Beta -FrameworkDependent -StageOnly
./native/build.ps1 -NoRestore -StageOnly
```

베타 기본은 Windows x64 실행 환경 포함이다. `StageOnly`는 기존 출력과 ZIP을 바꾸지 않는다. `NoRestore`는 앱 publish에 전달되며 도구 자체는 SDK로 부트스트랩한다. ReadyToRun·베타 RID 빌드에서는 restore를 생략할 수 없다.

검증만 하거나 CI에서 직접 실행할 수도 있다.

```powershell
dotnet run --project native/BuildTools -c Release -- verify native/dist-beta
dotnet run --project native/BuildTools -c Release -- self-test
dotnet run --project native/BuildTools -c Release -- build --native-root native --beta --package
```

`prepare DIRECTORY NATIVE_ROOT true|false`는 이미 publish한 격리 staging에 문서/라이선스와 manifest를 구성하는 개발용 진입점이다. `.local/publish-*`의 새 폴더만 허용하며 이미 준비한 결과는 덮어쓰지 않는다. 실패는 종료 코드1, 취소는130이다. 마지막 옵션 조합/복원/패키지 오류는 상세 원인과 함께 출력한다.

## 책임 분리

| 파일 | 역할 |
|---|---|
| `build.ps1` | 옵션 전달과 SDK 부트스트랩 환경 설정 |
| `BuildOptions` / `BuildRunner` | 옵션 검증, 격리 publish, ZIP 생성/재추출, 출력 교체 순서 |
| `PackagePreparer` | 잠금 버전 대조, 라이선스/고지, 실제 SDK·Opus 로드 확인, manifest 생성 |
| `PackageManifest` / `PackageVerifier` | JSON 자료형, 필수/금지 파일, 전체 목록과 스트리밍 SHA256 검증 |
| `WorkspaceFiles` | 작업 범위/링크 검사와 파일 복사 |
| `ProcessRunner` | 인수 개별 전달, 오류 코드, 제한 시간, 취소 시 자체 자식 프로세스 트리 정리 |
| `OutputTransaction` | 기존 폴더/ZIP 보존, 설치 실패 시 이전 출력 복구 시도 |
| `BuildLease` | 같은 소스 폴더의 동시 publish/출력 교체 방지 |
| `SelfChecks` | 경로·손상·인코딩·ZIP·옵션·교체 실패·타임아웃/취소 회귀 |

규칙은 PowerShell/Node에 중복하지 않는다. 새로운 배포 자산이나 금지 항목은 C# 정책과 해당 회귀를 함께 변경한다. NuGet 고지는 XML 파서로 읽고 외부 DTD/엔터티 로드를 금지한다. 파일/경로는 manifest와 실제 목록을 대조한 뒤 읽고, staging에는 링크를 허용하지 않는다.

## 보존과 검증

publish·라이선스·SDK/Opus·ZIP 재추출 검증이 성공하기 전에는 기존 배포본을 건드리지 않는다. 교체 중 오류가 생기면 새 결과를 staging으로 돌리고 보관한 이전 폴더/ZIP 복구를 시도한다. 복구까지 실패하면 오류를 모아 알리며 파일을 임의로 삭제하지 않는다. 중단된 staging은 `.local/`에 남겨 조사할 수 있다. 압축 처리 중의 취소는 다음 취소 확인 지점에서 반영되며 모든 단계를 즉시 중단한다는 보장은 아니다.

`self-test`는 임시 폴더의 자체 fixture와 자식 프로세스만 사용한다. 실제 오디오·통화·서버는 열지 않는다. `verify.ps1`와 `test-app.ps1` core에서도 이 검사를 실행한다. 기존 JS 패키징은 비교 검증 후 제거했으며 이력은 Git에 남아 있다. 이전 실험 보고서에 등장하는 `release/package.mjs` 명령은 당시 기록이다.
