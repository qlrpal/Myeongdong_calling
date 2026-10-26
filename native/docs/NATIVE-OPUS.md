# 네이티브 Opus 백엔드

관리형 Concentus의 프레임별 배열 할당을 줄이기 위해 공식 `Concentus.Native`와 `Concentus.Native.NetCore`1.5.2를 실행 프로젝트에 포함한다. SIPSorcery 소스 빌드의 의존성에는 추가하지 않는다. 패키지 버전과 콘텐츠 해시는 NuGet 잠금 파일로 관리한다.

프레임 길이·RTP 타임스탬프·재생 여유·WASAPI 주기·코덱 프로필은 이 변경으로 바꾸지 않는다. 목적은 코덱 처리 비용과 GC 압력을 줄이는 것이며 입→귀 지연이 특정 ms만큼 감소한다고 보장하지 않는다. 네트워크 공백을 없애는 수정도 아니다.

## 실행과 확인

기존 실행 명령을 그대로 사용한다. 시작 시 앱 폴더의 해당 아키텍처 `opus.dll`을 절대 경로로 로드하고 Concentus의 P/Invoke를 그 모듈에 연결한다. 현재 작업 폴더를 바꾸거나 그곳의 DLL을 복사·삭제하지 않는다. 지원 파일이 없거나 로드할 수 없으면 관리형 코덱으로 돌아가고 이유를 진단에 기록한다. 초기 선택은 프로세스 전체 수명 동안 유지한다.

```powershell
dotnet ./native/VoiceNative/bin/Release/net10.0-windows10.0.19041.0/VoiceNative.dll --codec-build-info ./codec-info.json
```

`NativeActive`, 실제 인코더/디코더 이름, 코덱이 반환한 버전 문자열, 네이티브 DLL SHA256과 fallback 이유를 확인한다. 포함된 DLL의 버전 문자열은 `libopus unknown`일 수 있다. 패키지 버전1.5.2를 실제 DLL의 자체 보고 버전으로 바꿔 적지 않는다. 두 호스트는 DLL 해시로도 대조한다. GUI 진단 내보내기와 제품/그룹 프로브에도 이 식별자가 포함된다.

## 대조 시험

환경 변수는 시험 프로세스를 시작하기 전에 지정하며 실행 중 변경하지 않는다.

```powershell
$env:VOICE_OPUS_BACKEND = 'managed' # 관리형 강제
# $env:VOICE_OPUS_BACKEND = 'native' # 네이티브 필수: 준비 실패 시 시험 실패
# $env:VOICE_OPUS_BACKEND = 'auto'   # 일반 실행: 네이티브 우선, 관리형 대체
# 여기서 같은 TransportProbe와 같은 인자를 실행한다.
Remove-Item Env:VOICE_OPUS_BACKEND
```

관리형/선택된 백엔드 사이의 양방향 Opus 패킷,2.5/5/10/20ms 길이,120샘플 lookahead, 리셋과 PLC를 제품 검사에서 확인한다. 실제 통화의 CPU·GC·PLC·부족은 두 호스트 대조로 별도 판정한다. 마이크·이어폰·DAC 지연과 네트워크 지터는 이 미세 벤치마크에 포함되지 않는다.

## 배포

`runtimes` 폴더를 포함한 빌드/배포 출력 전체를 복사한다. RID 지정 publish에서는 해당 플랫폼의 DLL이 출력 루트에 놓일 수 있으며 두 배치를 모두 지원한다. `Opus.LICENSE.txt`도 함께 배포한다. DLL 하나나 EXE만 복사하면 네이티브 자산이 빠질 수 있다.

공식 근거: [Concentus 패키지](https://www.nuget.org/packages/Concentus/2.2.2), [선택적 네이티브 패키지](https://www.nuget.org/packages/Concentus.Native/1.5.2), [상류 코덱 팩토리](https://github.com/lostromb/concentus/blob/master/CSharp/Concentus/OpusCodecFactory.cs).
