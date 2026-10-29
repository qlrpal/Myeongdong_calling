# EXTREME-124: 베타 배포 정리·패키징

2026-10-29 / `codex/extreme-latency` / **0.6.1-beta.1**. 기존 미커밋 작업을 보존하고 베타 배포물을 별도로 준비했다. 커밋·푸시·태그·GitHub Release 업로드·서버 변경은 하지 않았다.

## 정리

1. **의존성**: 미사용 NAudio 메타 참조를 제거했다. 실제 Wasapi/WinMM 및 WinMM의 필수 Midi/Core는 유지한다. 앱/프로브 잠금에서 Asio·Dmo·WinForms·메타 패키지가 빠졌다. 나머지 버전은 올리지 않았다.
2. **SDK 참조**: WinForms/COM/PInvoke만 사용하는 소스에서 사용하지 않는 Windows Runtime 투영 참조를 SDK 설정으로 제외했다. Microsoft.Windows.SDK.NET.dll·WinRT.Runtime.dll을 임의 삭제하지 않고 의존성 그래프에서 제외했다. 대상 Windows API 버전·WASAPI/암호화/Opus 동작은 그대로다.
3. **코드**: 미리보기/가상 참가자 코드를 `CallForm.Preview.cs`로 분리하고 베타에서 컴파일 제외한다. 베타의 실험 CLI와 시험 설정·음향/루프백/LAN 시험 UI를 제외했다. 개발 빌드의 비교 경로와 회귀 소스는 보존한다. 진단 저장·출력 재시작·미디어 인터페이스·제한 패킷 기록은 유지한다.
4. **에셋/문서**: RID win-x64 publish로 대상 플랫폼의 네이티브 Opus만 포함한다. 서버 설치 자료·실험 보고서·개발 파일·사용자 원본을 배포하지 않는다. 이미지/웹 프레임워크를 새로 추가하지 않았다. 사용자용 시작/베타 안내와 개발 문서를 분리했다. 필수 런타임·라이선스·고지는 보존한다.
5. **배포 수명**: 새 staging → SDK/Opus 로드 → 패키지 목록/해시 → ZIP 재추출 검증 후 교체한다. 이전 베타는 `.local/archive/`로 보관한다. 일반 `dist`와 그 안의 사용자 진단은 유지한다. RID별 잠금은 `.local/`에 두고 소스 잠금을 덮어쓰지 않는다.

광범위한 IL 트리밍/AOT, 코덱·프레임·여유 조정, 수정 SDK 재작성은 적용하지 않았다. CPU나 실제 음성 지연이 감소했다고 주장하는 변경이 아니다.

## 배포 크기 관측

| 조건 | 파일 수 | 크기 |
|---|---:|---:|
| 정리 전 격리 publish, 실행 환경 미포함 | 172 | 45,602,197 bytes |
| 정리 후 베타 비교 publish, 실행 환경 미포함 | 49 | 14,387,453 bytes |
| 최종 베타, 실행 환경 포함 | 315 | 136,998,234 bytes |
| 최종 베타 ZIP, 실행 환경 포함 | 1 | 59,209,688 bytes |

실행 환경 미포함끼리 비교하면 약68% 감소했다. 최종 ZIP은 사용자의 .NET 설치를 없애기 위해 .NET10 Windows Desktop 실행 환경을 추가했으므로 그 비교 publish보다 크다. 비교용 격리 출력 수치는 정리 중 측정 시점의 문서/레이아웃을 포함한다. 런타임 모듈을 수동으로 잘라내지 않았다.

최종 ZIP SHA256:

```text
00da733d978c6afddf686faaba5f046a9c5f144d91d67bd1925451482fb97ee1
```

## 검증

- 의존성 잠금 재검증과 경고를 오류로 처리한 Release 빌드: 경고/오류0.
- `verify.ps1 -Integration` 전체 통과: 제품/오디오 합성/설정/수명/읽기 위치, 서버, 검사 실행기, 새 패키지 정책 회귀와 localhost IPv4/IPv6 암호화 전송·가변 프레임.
- 패키징 회귀: 개발/개인 파일·위험 경로, 손상 파일, 추가 파일, 누락 필수 자산, 잘못된 manifest 판정을 확인했다.
- 최종 ZIP을 새 폴더에 풀어 필수 자산·전체 파일 목록/SHA256을 확인했다. manifest에 버전·RID·runtime 포함·소스 HEAD/dirty·SDK·Opus를 기록한다. 서명 기능은 아니다.
- 실제 배포 폴더의 exe로 수정 SDK `10.0.17+myeongdong.2 / udp1-turn1-rtp0`와 네이티브 Opus를 로드했다. 실행 환경 포함/미포함 두 publish 경로를 확인했다.
- 배포 exe의 실제 입장·설정·진단 화면을 확인했다. 실험 도구 제외와 진단 유지, 고급 영역의 줄어든 빈 공간을 확인했다. 잘못된 방 이름 안내/전체 선택과 수정 뒤 안내 해제를 통화 연결 없이 확인했다. 검사용 창을 닫았고 최종 화면 확인 전후 사용자 설정 바이트가 같았다.
- Git 후보의 UTF-8·대용량/바이너리·원본 진단/생성물 경로·개인 키 표식과 Markdown 링크를 점검했다. 전체 보안/취약성/법률 감사라는 의미는 아니다.

원본은 Git 제외 `native/.local/ui-polish-20261029/beta-*`, `.local/publish-*`, `.local/package-check-*`에 보존한다. 실제 마이크·스피커·친구 통화·외부망/TURN·게임·물리 장치 탈착·새 PC·물리 입→귀·장시간 품질 시험은 이번 작업에서 하지 않았다. 새 안정판 승격이나 환경별 끊김 해결 판정은 아니다.

## 사용·재현

공유할 파일은 `native/dist-beta.zip`이다. 전체를 새 폴더에 풀고 `VoiceNative.exe`를 실행한다. 기본 베타는 실행 환경 포함이며 SDK·Node.js·별도 .NET 설치가 필요하지 않다. 사용자용 안내·제한·문제 제보 방법을 ZIP에 넣었다.

```powershell
./native/build.ps1 -Beta -Package
node native/release/package.mjs verify native/dist-beta
```

소스/개발 빌드로 돌아갈 때는 `./native/run.ps1`을 사용한다. 일반 기존 배포본은 `native/dist`에 유지했다. 자세한 절차는 [베타 가이드](../BETA-RELEASE.md).
