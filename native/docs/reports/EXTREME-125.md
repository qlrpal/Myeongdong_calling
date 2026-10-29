# EXTREME-125: C# BuildTools로 빌드·패키징 통합

2026-10-29 / `codex/extreme-latency`. 클라이언트 빌드의 Node 의존성을 없애고, 앱과 분리된 C# 빌드 도구 및 얇은 PowerShell 진입점으로 이전했다. 통화 앱·오디오·서버 코드는 변경하지 않았다. 커밋/푸시는 이번 작업에서 하지 않았다.

## 구현

- 외부 NuGet 라이브러리가 없는 `BuildTools` 콘솔 프로젝트와 빈 의존성 잠금, 솔루션 항목을 추가했다.
- `build.ps1`은 기존 옵션을 받아 도구로 전달하고 부트스트랩 환경만 설정한다. 옵션 검증, dotnet publish, 개발/베타 문서 구성, 라이선스/잠금 버전, SDK/Opus 로드, manifest, ZIP/추출/해시, 출력 보존/교체는 C#에 모았다.
- `PackageManifest`/`PackageFile`로 자료형을 명시하고, 숫자/불리언 문자열·누락 필수 속성·null 파일 항목을 거부한다. 기존 schema 1 정상 패키지는 그대로 읽는다. 해시는 파일 스트림으로 계산한다.
- 프로세스는 셸 문자열 대신 인수를 개별 전달하며 종료 코드·시간 제한·취소를 처리한다. 실패/취소 시 자신이 시작한 자식 프로세스 트리를 종료한다.
- workspace 범위와 링크를 검사한다. 교체 전 검증을 완료하고, 설치 단계 실패 시 이전 폴더/ZIP을 복구한다. 실패 자료는 staging/archive에 남긴다. 동시 빌드는 파일 잠금으로 막는다.
- 라이선스 메타데이터는 외부 DTD/엔터티를 막은 XML 파서로 읽는다. `release/`에는 사용자 문서와 라이선스 데이터만 남겼다. 기존 JS 패키징/검사 2파일은 비교 후 제거했다.
- verify 및 앱 검사 core는 C# self-test를 호출한다. 서버·두 PC/앱 자동 실행기는 기존 Node를 유지한다. BuildTools는 통화 앱에서 참조하거나 배포 ZIP에 넣지 않는다.

## 비교·검증

1. 기존 베타 패키지를 구 Node와 새 C# 검증기 양쪽으로 확인했다.
2. 같은 실행 환경 미포함 publish 원본을 격리 폴더 두 개에 복사해 각 도구로 준비했다. **48개 파일의 이름·바이트 수·SHA256 및 생성시각을 제외한 manifest가 완전히 일치**했다. 라이선스/고지 텍스트도 같은 해시였다. 각 도구가 상대 결과물을 검증했다.
3. 프로세스 PATH에서만 Node 경로를 제거하고 Node 명령이 없음을 확인한 상태로 다음을 완료했다. 시스템 PATH는 바꾸지 않았다.
   - 실행 환경 포함 베타 ZIP 생성·재추출 검증.
   - 실행 환경 미포함 베타 StageOnly.
   - 기존 일반 빌드의 NoRestore + StageOnly. 기존 일반 dist는 유지했다.
4. `verify.ps1 -NoRestore`의 정적 분석/제품/서버/기존 실행기 회귀를 통과했다. 최종 도구의 더 엄격한 manifest/동시 실행 잠금 검사를 추가한 뒤 도구 경고/오류0 빌드와 **58개 assertion**을 다시 통과했다.
5. 자체 검사에는 개발/개인/위험 경로, 손상·추가·누락·중복 파일, 잘못된 manifest 자료형, 한글 ZIP 왕복·ZIP 경로 이탈, 옵션 조합, 교체 단계 4곳의 실패 후 복구, 정상 교체/보관, 빌드 잠금 해제, 프로세스 실패·타임아웃·취소를 포함한다.
6. 최종 도구로 Node 없는 베타 ZIP을 다시 생성했다. 314개 manifest 대상 파일과 manifest 자체, 수정 SDK·네이티브 Opus를 확인했고 이전 베타를 보관했다. 구 Node 검증기로도 새 패키지를 확인했다.

첫 자체 검사 실행은 샌드박스의 임시 폴더 이동 차단으로 실패했고 동일 검사를 제한 밖에서 통과했다. 사용자 도구에 관리자 요구를 추가한 것은 아니다. 원본 로그/구 도구 비교용 보관은 Git 제외 `.local/buildtools-migration/`에 둔다.

실제 마이크·스피커·통화·게임 시험은 하지 않았다. ReadyToRun의 실제 publish는 이번 작업에서 실행하지 않았으며 옵션 전달/유효성 경로만 유지했다. 런타임 지연/품질/CPU 개선을 주장하지 않는다. 프로세스 취소 검사는 도구의 정상 취소 경로이며 OS 강제 종료의 모든 상황을 검증했다는 뜻은 아니다.

## 실행

```powershell
./native/build.ps1 -Beta -Package
dotnet run --project native/BuildTools -c Release -- verify native/dist-beta
dotnet run --project native/BuildTools -c Release -- self-test
```

클라이언트 빌드에는 .NET10 SDK와 Windows PowerShell만 필요하다. 전체 서버/자동 검사 실행에는 Node가 여전히 필요하다. 기존 빌드 옵션과 일반 소스 실행은 유지한다. 이전 패키지는 `.local/archive/`에 보존한다. 상세 구조와 실패/복구 동작은 [BuildTools 가이드](../BUILD-TOOLS.md).
