# 출력 지연 원인 대조: 직접 WASAPI·장치 시계·루프백

후속 승인된 A/B/A에서 DTS endpoint 효과의 영향을 확인하고 원상 복구했다. 최신 결론은 [DTS 원인 확인 보고서](DTS-ENDPOINT-058.md)를 우선한다. 아래는 설정 변경 승인 전 조사 기록이다.

2026-10-19. 앞서 노트북에서 관측한 제출→루프백 콜백 약135ms를 조사했다. 앱 기본 통화 정책·서버·장치 설정·게임 설정은 변경하지 않았다. 합성 신호와 음향 입력은 상한이 있는 임시 메모리에서 분석했으며 원음은 저장하지 않았다.

## 확인된 경계

**RTP·코덱·재생 큐·리샘플러·채널 변환을 모두 제외한 직접 WASAPI 출력에서도 노트북의 큰 관측 지연이 재현됐다.** 현재 앱의 수신 큐나 Opus 디코딩이 그 차이를 설명하는 주원인이라는 가설은 이 대조와 맞지 않는다.

다만 장치 시계·루프백은 Windows의 관측 지점이다. 실제 스피커가 소리를 낸 시각을 보장하지 않으며, 특정 APO·드라이버 또는 루프백 수집 경로 중 하나를 단독 원인으로 확정하지 않았다.

## 방법

- 장치 기본 IEEE float 형식으로 합성 chirp6개를 만든 뒤 네이티브 `IAudioRenderClient` 버퍼에 직접 넣었다. RTP·압축·NAudio 공급자·리샘플러를 사용하지 않았다.
- 공유/event, RAW/GameChat, 해당 장치의 최소 보고 주기를 유지했다. 이 독립 대조는 전체 버퍼를 공급하므로 제품의 제한 공급과 동일한 설정은 아니다.
- 각 신호의 실제 버퍼 Release 시각, 스트림 내 프레임 번호, `IAudioClock.GetPosition`·주파수·QPC, 대응 루프백 위치를 기록했다.
- 장치 시계 주파수 단위를 샘플 수로 가정하지 않았다. 예를 들어 노트북은384000units/s,48000frames/s다. 위치를 주파수로 나누어 시계 QPC에 연결했다.
- 같은 출력에서 일반20ms, RAW20ms, 일반5ms 루프백을 순차 비교하고 일반20ms를 다시 확인했다. 이어 별도 네이티브 캡처에서 `AUTOCONVERTPCM`·`SRC_DEFAULT_QUALITY` 없이 동일 장치 형식으로 반복했다.

## 핵심 결과

아래는 **장치 시계로 대응시킨 신호 시각→루프백 QPC / 콜백 수신 시각** 차이의 중앙값이다. 실제 물리 지연 표가 아니다.

| 노트북 캡처 경로 | 루프백 QPC 차이 | 콜백 차이 | 유효 신호 |
|---|---:|---:|---:|
| 일반20ms A1 |80.05ms |99.20ms |6/6 |
| RAW20ms |80.06ms |100.22ms |5/6 |
| 일반5ms |80.08ms |100.20ms |4/6 |
| 일반20ms A2 |80.04ms |99.96ms |6/6 |
| NAudio 캡처·자동 변환 제외 |80.07ms |99.19ms |6/6 |

직접 출력 버퍼의 Release→콜백 수신은 노트북 약144ms였다. 제품의 약135ms와 공급량·스트림 초기화가 같지 않아 두 값의 차이를 추가 지연으로 계산하지 않는다. 장치 시계로 환산한 시점도 DAC 시각으로 사용하지 않는다.

PC의 네이티브 캡처 대조에서는 QPC 차이 약2.13ms, 콜백 차이 약−12.68ms였다. 일부 시각이 실제 콜백보다 미래여서 **PC의 장치/루프백 QPC를 절대 출력 지연으로 신뢰할 수 없다는 기존 제한**이 다시 확인됐다. 음수 지연을 성능 이득이나 물리 현상으로 해석하지 않는다.

장치 시계의 구간 진행률은 양쪽 대체로1에 가까웠고, 해당 직접 출력 회차의 장치 버퍼 소진은0회였다. 시계의 진행률이 정상이라는 사실은 고정 오프셋까지 정확하다는 보장은 아니다. 불연속·상관도 미달 신호는 표에서 제외했다.

## 원인 가설별 판정

| 가설 | 대조 결과 |
|---|---|
| RTP 큐·디코딩·앱 리샘플러가 큰 차이를 만든다 | 이들을 제거해도 재현되므로 큰 차이의 주원인이라는 설명과 맞지 않음 |
| NAudio 캡처 내부 대기 또는 자동 변환 플래그가 만든다 | 이를 제거한 네이티브 캡처에서도 재현 |
| 루프백 요청 버퍼20ms가 전부 원인이다 |5ms 요청으로 바꿔도 차이가 유지됨 |
| 캡처에 RAW만 적용하면 해결된다 | RAW 캡처에서도 유지됨 |
| 특정 Windows 장치 경로·APO·드라이버 또는 수집 지점의 영향 | 남아 있는 범위. 현재 자료로 세부 원인 하나는 확정하지 못함 |

## DTS·Realtek 효과 확인과 제어 한계

실제로 시험한 노트북 기본 출력 endpoint의 읽기 전용 등록 정보를 확인했다. Realtek SFX/MFX/EFX와 DTS MFX/EFX(`dtsapo64.dll`)가 등록돼 있었다. RAW도 항상 켜진 endpoint 효과를 모두 우회하는 것은 아니므로 후보가 된다. **등록돼 있다는 사실만으로 DTS가80ms를 만들었다고 결론 내리지 않는다.**

해당 스트림의 `IAudioEffectsManager.GetAudioEffects`는 PC와 노트북 모두 빈 목록을 반환했다. 따라서 준비한 ‘effects-off’ 회차에서도 실제 비활성화한 효과는0개다. 결과가 같다는 이유로 APO 원인을 배제하지 않는다. 전역 효과·레지스트리·서비스·드라이버 상태는 변경하지 않았다.

별도로 생성한 미초기화 DTS·Realtek APO 인스턴스의 읽기 전용 `GetLatency`는0을 반환했다. 이는 활성 장치 체인의 인스턴스도 아니고 해당 장치 형식으로 초기화된 상태도 아니므로 실제 지연0의 근거로 사용하지 않는다. 활성 체인 지연을 이 값으로 보정하지 않는다.

## 독립 대조에서 실패한 항목

- 프로세스 전용 루프백은 양쪽에서 시험 신호가 전혀 잡히지 않았다. 유효0/6이며 무음으로 수집됐다. 장치 루프백과의 지연 비교 근거로 사용하지 않는다.
- 노트북 스피커→내장 마이크 음향 시험은 RAW 입력과 일반 입력 모두 유효0/6이었다. 최대 상관도도 약0.18 수준으로 낮아 검출 기준0.7에 미달했다. 작은 숫자가 나온 우연한 일치를 물리 지연으로 채택하지 않았다. 마이크·스피커 음량·효과·연결 상태는 변경하지 않았다.
- 새 도구의 초기 메타데이터 peak 계산은 빈 버퍼에서 실패했다. 빈 입력을 처리하도록 수정했으며 해당 실패 실행은 근거에서 제외했다. 최종 도구는 실행 완료와 유효 신호 확보를 구분하고, 필수 회차의 유효 신호가4개 미만이면 성공으로 종료하지 않는다.

## 결론과 남은 확정 조건

현재의 큰 관측 차이를 앱의 RTP 큐·코덱·변환 버퍼 탓으로 보고 계속 낮추는 것은 근거가 없다. 앱을 제외해도 남는 **Windows 장치 출력/루프백 경로의 차이**까지 재현·범위를 좁혔다.

아직 ‘DTS가 정확히80ms를 만든다’거나 ‘실제 스피커 출력이135ms 늦다’고 확정하지 못했다. 특정 장치 효과에 대한 통제된 켜짐/꺼짐 대조 또는 신호를 안정적으로 검출하는 독립 물리 측정이 필요하다. 장치별 항상 켜진 효과는 현재 스트림 API로 제어되지 않았고, 개인 장치 설정 변경은 기존 작업 범위에서 제외했으므로 임의로 전역 효과를 끄지 않았다.

## 재현

```powershell
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --output-clock-device OUTPUT.json
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --output-clock-focused-device OUTPUT.json
# 스피커→마이크 경로가 실제로 성립하고 동일 기본 샘플률인 환경에서만 해석
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --output-clock-acoustic-device OUTPUT.json
python native/TransportProbe/Analysis/analyze_output_clock.py SUMMARY.json INPUT.json
```

원본·실패 실행·읽기 전용 효과 조회는 Git 제외 경로 `native/.local/output-clock-058/`에 보관한다. 시스템/장치 설정 변경 없이 진단 구현만 추가했다. 배포 ZIP과 서버는 변경하지 않았다.

최종 Release 빌드는 경고0·오류0이며, 기본 회귀·서버7개·실제 ICE/DTLS/SRTP/RTCP 및 RTP 재생 통합 검사를 통과했다. 종료 후 PC와 노트북의 통화·진단 프로세스가 모두0개임을 확인했다.

## API 근거

- [IAudioClock.GetPosition: 주파수와 QPC 단위](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)
- [Windows 루프백의 수집 위치](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [RAW 스트림 옵션과 항상 켜진 처리의 한계](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-audclnt_streamoptions)
- [스트림 효과 API](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nn-audioclient-iaudioeffectsmanager)
- [APO GetLatency](https://learn.microsoft.com/en-us/windows/win32/api/audioenginebaseapo/nf-audioenginebaseapo-iaudioprocessingobject-getlatency)
