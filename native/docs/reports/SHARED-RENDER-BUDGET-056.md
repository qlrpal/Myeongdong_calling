# WASAPI 공유 출력 버퍼 구조 확인

2026-10-19. 설치된 `NAudio.Wasapi 3.0.0`의 nuspec이 가리키는 커밋 `23922d2a87cf9508883a939af8b1495a0d35cb5a`를 기준으로 확인했다. 최신 main 구현으로 대체해서 추정하지 않았다.

## 약22ms의 의미

`WasapiPlayer.CurrentLatency`는 재생 중 `AudioClient.CurrentPadding / OutputWaveFormat.SampleRate`를 반환한다. 재생 전이나 COM 오류에서는 `AverageLatency`로 복귀한다. `AverageLatency`는 저지연 경로에서 선택한 엔진 주기를 정수 ms로 나타낸다. 따라서 API의 약22ms는 현재 엔드포인트 버퍼에 대기하는 오디오 양이며, 실제 DAC·이어폰·공기 전달을 포함한 물리 출력 지연이 아니다.

소스: [동일 커밋 WasapiPlayer.cs](https://github.com/naudio/NAudio/blob/23922d2a87cf9508883a939af8b1495a0d35cb5a/src/NAudio.Wasapi/WasapiPlayer.cs), `CurrentLatency` 및 `AverageLatency`.

## 실제 PC 장치 협상 결과

음성을 재생하지 않고 기본 출력 장치의 공유 스트림만 초기화해 조회했다. `Start`는 호출하지 않았고 장치 형식·볼륨·게임 설정은 변경하지 않았다. 같은 형식이라도 실제 재생 상황의 안정성까지 검증한 것은 아니다.

| 항목 | 값 |
|---|---:|
| 장치 형식 |Realtek HD Audio 2nd output, 44.1kHz float stereo |
| 지원 최소/기본/기본단위/최대 주기 |모두448프레임 |
| 실제 최소 공유 주기 |10.15873ms |
| 저지연 공유 버퍼 용량 |984프레임 =22.31293ms |
| 일반 공유40ms 요청 시 버퍼 용량 |1764프레임 =40ms |
| 초기화 후 StreamLatency |0（이 값을 물리 지연0으로 해석하지 않음）|

기존 게임 시험의 약22.313ms API 보고값은 위 저지연 공유 버퍼 전체 용량과 일치한다. 이 장치는 현재 형식에서5ms 공유 주기를 지원하지 않는다. `WithLatency(40)`를20으로 바꿔도 이미 활성화된 IAudioClient3 경로의 선택 주기·984프레임 용량이 그대로일 수 있으므로 요청 숫자 변경만으로10–20ms를 줄였다고 주장할 수 없다.

## 채우기와 리샘플링

동일 커밋의 `PlayThread`는 시작 전에 `BufferSize` 전체를 채운 뒤 시작한다. 이후 이벤트마다 `BufferSize - CurrentPadding`만큼 공급자에게 요청하여 빈 공간을 모두 채운다. 추가40ms 애플리케이션 큐가 발견된 것이 아니라, WASAPI가 허용한 출력 버퍼를 가득 유지하는 정책을 확인한 것이다. 엔진 주기10.16ms와 전체 용량22.31ms는 서로 다른 값이다.

PC는48kHz 음성에 출력44.1kHz를 사용하므로 앱이 `WdlResamplingSampleProvider`를 연결한다. [동일 커밋 리샘플러](https://github.com/naudio/NAudio/blob/23922d2a87cf9508883a939af8b1495a0d35cb5a/src/NAudio.Core/Wave/SampleProviders/WdlResamplingSampleProvider.cs)는 출력 요청에 맞춰 `ResamplePrepare`로 필요한 입력량을 구하고 즉시 읽는다. 이 래퍼에는 별도10/20/40ms 타이머나 프레임 큐가 없다. 내부 필터·분수 샘플 상태까지 지연0이라고 확인한 것은 아니다. 과거 기록의 재생 요청 약10.167ms는48kHz 측 요청량이며, 장치448프레임의10.15873ms와 구분한다.

## 판단

다음 후보는 **엔진 주기는 유지하면서 출력 목표 padding을 버퍼 전체보다 작게 유지하는 방식**이다. 예를 들어 한 주기와 안전 여유만 채우도록 제한하면 대기를 줄일 가능성이 있지만, 첫 프리필만 줄이고 이후에 전부 채우면 지속 효과가 없다. 시작과 정상 공급을 함께 제어해야 한다. 버퍼 용량과 주기 차이 약12.15ms를 그대로 실측 절감량으로 발표해서는 안 된다.

현재 NAudio 플레이어에는 이 목표 padding을 설정하는 공개 API가 없다. 실제 적용에는 출력 어댑터 변경이나 라이브러리 수정이 필요하다. 따라서 이번에는 앱 재생 경로를 바꾸지 않았다. 후보를 구현한다면 실험 경로로 격리하고, 공유 이벤트 방식·기존 복귀 경로를 유지하며 늦은 출력 요청에서 장치 언더런이 늘어나는지 실제 장치에서 비교해야 한다. 기존 RTP 큐의 무음 부족만으로 장치 출력 언더런을 판정하지 않는다.

## 재현과 검증

```powershell
dotnet build native/TransportProbe/TransportProbe.csproj -c Release --no-restore
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --render-budget native/.local/game-capture-056/pc-render-budget.json
```

진단 명령을 추가했으며 PC에서 실행해 위 값을 확인했다. 음성·마이크·독점 모드는 사용하지 않았다. 빌드 경고0·오류0, 소스 diff 공백 검사를 통과했다. 현재 결과는 PC 출력만 검증했으며 노트북 출력에는 일반화하지 않는다.

## 후속 구현

공유 출력 공급량 제한·초기 안정화·장치 버퍼 소진 시 안전 복귀를0.5.7에 구현했다. 최신 동작과 검증 범위는 [0.5.7 보고서](BOUNDED-SHARED-RENDER-057.md)를 참고한다. 위의 “이번에는 앱 재생 경로를 바꾸지 않았다”는0.5.6 조사 당시의 상태다.
