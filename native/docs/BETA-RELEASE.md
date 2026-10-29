# 0.6.1-beta.1 배포 준비

## 만들기

Windows x64, .NET10 SDK와 Windows PowerShell에서 저장소 루트를 기준으로 실행한다. 클라이언트 빌드·패키징은 Node 없이 가능하며 C# [BuildTools](BUILD-TOOLS.md)가 처리한다. 전체 `verify.ps1`의 서버/자동 검사에는 Node.js22 이상이 필요하다.

```powershell
./native/verify.ps1 -Integration
./native/build.ps1 -Beta -Package
```

결과는 `native/dist-beta/`와 `native/dist-beta.zip`이다. 실행 환경을 포함하며 대상 사용자는 SDK·Node.js·.NET 설치 없이 ZIP 전체를 새 폴더에 풀고 `VoiceNative.exe`를 실행한다. 서버는 기존 주소를 사용한다. GitHub 업로드·태그·커밋·푸시는 이 명령이 수행하지 않는다.

`-Beta -FrameworkDependent -StageOnly`는 별도 .NET10 Desktop Runtime이 필요한 비교용 격리 출력이다. `-StageOnly`는 기존 배포 폴더를 바꾸지 않는다. 베타는 RID 복원이 필요해 `-NoRestore`와 함께 사용하지 않는다. RID별 잠금 파일은 `.local/`에 생성하며 소스 잠금을 덮어쓰지 않는다. 실제 publish 의존성을 소스 잠금의 버전과 비교한다.

## 배포 범위

- 통화·장치/마이크/상대 음소거·볼륨·단축키·자동 복구·미디어 인터페이스 선택은 유지한다.
- 상세 진단·JSON 복사/저장·출력 재시작·30초 제한 패킷 경로 기록은 베타 문제 수집을 위해 유지한다.
- 스크린샷 가상 참가자/미리보기 코드는 베타에서 컴파일하지 않는다. 실험 프레임·공통 출력 CLI와 시험 설정·음향/루프백/LAN 시험 UI는 베타에서 노출하지 않는다. 내부 오디오 정책을 재작성하거나 비교 후보를 기본값으로 승격하지 않는다.
- 테스트 실행기·서버 설치 스크립트·실험 보고서·Figma 원본·개인 진단·로그·PDB·개인 키를 패키지에 넣지 않는다. 공개 검증 문서와 검사 코드는 소스 저장소에 보존한다.
- NAudio 메타 패키지를 제거하고 실제 사용하는 Wasapi/WinMM에만 의존한다. WinMM이 요구하는 Midi와 Core는 유지한다. 사용하지 않는 Windows Runtime 투영 참조는 SDK 설정에서 제외한다. DLL을 임의로 골라 삭제하거나 WinForms에 광범위한 트리밍/AOT를 적용하지 않는다.
- 런타임 의존성 라이선스/고지와 출처를 포함한다. 일부 구형 패키지는 패키지 내 텍스트가 없어 공식 저장소 고지를 보완했으며 출처/참조/블롭 식별자를 `licenses/sources.json`에 보존한다. 전체 법률/보안 감사 완료를 의미하지 않는다.

## 패키지 확인

`release-manifest.json`은 베타 버전·RID·런타임 포함 여부·Git HEAD/미커밋 여부·수정 SDK·네이티브 Opus·각 파일 크기/SHA256을 기록한다. 개인 경로·설정·진단을 넣지 않는다. 목록 자체는 디지털 서명이 아니다.

빌드는 새 staging에 publish하고 필요한 문서/라이선스를 복사한 후 그 폴더의 실행 파일로 SDK·Opus 로드를 확인한다. ZIP을 다시 풀어 파일 목록과 해시를 확인한 다음에만 기존 베타 출력을 `.local/archive/`에 보존하고 교체한다. 기존 일반 `dist`는 이 베타 명령으로 변경하지 않는다.

```powershell
dotnet run --project native/BuildTools -c Release -- verify native/dist-beta
dotnet run --project native/BuildTools -c Release -- self-test
Get-FileHash native/dist-beta.zip -Algorithm SHA256
```

개발/사용자 문서의 실행 명령과 실제 베타 사용자용 문서는 분리한다. ZIP에는 `README.md`, `시작하기.md`, `베타안내.md`, 제3자 고지만 넣는다.

## 검증 범위와 복귀

실제 배포 폴더의 기본 화면/설정/진단 전환, SDK/Opus 로드와 ZIP 추출 확인은 패키징 검사다. localhost 암호화 전송·합성 회귀는 독립 PC·실제 게임·친구 회선·장시간 품질·물리 입→귀 지연 합격이 아니다. 최신 결과는 [현재 상태](CURRENT-STATUS.md)와 [124 보고서](reports/EXTREME-124.md)를 따른다.

베타용 버전/심볼/화면 조건은 `BetaRelease=true` publish에만 적용한다. 일반 소스 실행은 `./native/run.ps1`, 비교 후보는 개발 가이드의 명시적 옵션으로 유지한다. 이전 베타 ZIP은 `.local/archive/build-dist-beta-*/`에 보존한다. 사용자 설정은 앱 폴더 밖에 있으므로 새 폴더에 풀어 업데이트해도 유지한다.
