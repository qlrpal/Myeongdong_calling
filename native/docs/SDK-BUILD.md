# 기본 수정 SDK 빌드와 배포

일반 `run.ps1`, `build.ps1`, `verify.ps1` 및 `dotnet build/run/publish`는 수정 SIPSorcery를 자동으로 준비하고 사용한다. 별도 실험 명령이나 환경 변수, Python, 수동 DLL 교체는 필요 없다.

## 사용

```powershell
./native/run.ps1
./native/verify.ps1 -Integration
./native/build.ps1 -Package
```

Windows10 2004 이상·.NET10 SDK가 필요하다. 서버 검사/실행은 기존처럼 Node.js22 이상을 사용한다. 첫 빌드에는 GitHub의 고정 상류 아카이브 약142MiB와 잠금 NuGet 의존성을 내려받을 인터넷 연결이 필요하다. SDK 파일만 추출하며 캐시는 `native/.local/patched-sdk`에 둔다. 이후에는 입력과 DLL의 SHA-256을 검사하고 재사용한다. 캐시와 NuGet 패키지가 준비되어 있으면 다시 다운로드하지 않는다.

## 고정된 변경

- 상류 SIPSorcery10.0.17, 커밋 `08c2ed0ad72c423ca06950dee2e842488a219884`.
- 압축본 및 수신·ICE·SRTP 원본 파일의 SHA-256·패치 위치를 검증한다. 다른 소스에 조용히 적용하지 않는다.
- UDP 수신 실행 상태의 확인/설정을 짧은 잠금으로 보호한다. 비동기 등록·패킷 처리는 잠금 밖이다.
- 정상 UDP TURN 종료 시 인증된 Refresh(0)를 소켓 종료 전에 요청한다.100ms 송신 제한의 best effort이며 즉시 삭제·ACK를 보장하지 않는다. TCP/TLS는 변경하지 않는다.
- 인증 후에만 초기 RTP 수신 상태를 반영한다. 정상 초기 순번0 거부와 인증 실패 패킷의 상태 오염을 수정했다. 진단에는 협상된 암호화 프로필 이름만 표시한다. [SRTP 보호 검사](SRTP-PROTECTION.md).
- 이 수정들은 일반 SDK에서 항상 적용한다. 과거 `VOICE_SDK_RECEIVE_GATE`, `VOICE_SDK_TURN_RELEASE` 변수로 끌 수 없다. 실험용 수신 수명 기록 큐도 기본 DLL에는 넣지 않는다.

`native/sdk/RuntimeDependencies.props`는 앱·프로브·SDK의 런타임 버전을 공유한다. 각각의 `packages.lock.json`을 커밋하고 SDK 자체도 잠금 모드로 복원한다. 상류 SDK 프로젝트를 그대로 실행하지 않고 NET10 전용 빌드 정의로 고정된 소스를 컴파일한다. 앱의 정적 분석 정책은 유지하고 상류 소스의 코드 스타일에는 별도 빌드 정책을 사용한다.

## 원본 DLL 혼입 방지

원본 SIPSorcery NuGet DLL 참조를 제거했다. 일반 빌드가 준비한 수정 DLL을 앱과 프로브가 직접 참조하며 출력·publish 뒤에는 캐시 DLL과 SHA-256이 같은지 검사한다. 실패 시 원본으로 대체하지 않고 빌드를 실패시킨다. SDK 원문 라이선스도 출력과 배포 폴더에 복사한다. 프로젝트 폴더만 옮기거나 DLL 하나만 복사하지 말고 배포 폴더 전체를 사용한다.

선택적 `build.ps1 -ReadyToRun`에서는 SIPSorcery.dll만 R2R 변환 대상에서 제외해 검증한 바이트를 유지한다. 나머지 어셈블리는 기존 R2R 설정을 적용한다. SDK의 JIT 제거까지 보장하는 옵션은 아니다.

실제 앱이 로드한 버전은 UI를 열지 않고 확인할 수 있다.

```powershell
dotnet run --project native/VoiceNative -c Release -- --sdk-build-info native/.local/sdk-info.json
```

정상 식별자는 `InformationalVersion: 10.0.17+myeongdong.2`, `PatchRevision: udp1-turn1-rtp0`, `ProductionPatched: true`다. `verify.ps1`은 프로브의 실제 로딩도 검사한다. 기존 dist는 소스 변경만으로 갱신되지 않으므로 새 배포 파일이 필요하면 `build.ps1`로 다시 만든다.

## 실험 도구와의 구분

`native/sdk-experiment`는 원본/수정 A/B 대조를 재현하는 과거 진단용 경로다. 일반 실행에 필요하지 않으며 거기서 교체한 실험 DLL은 기본 수정 SDK 식별자와 다르다. 실행/배포 기본 적용은 SDK 변경의 연결을 완료한 것이며, 모든 환경의 끊김·PLC·물리 입→귀 지연 문제가 해결됐다는 의미는 아니다.
