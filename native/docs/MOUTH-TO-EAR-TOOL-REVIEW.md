# 입→귀 측정 도구 코드 검토

2026-11-09. 정적 검토이며 설치·실장치 측정·정확도 검증 결과가 아니다. 기존 미커밋 계획 문서는 변경하지 않았다.

## 결론

NIST의 음성 지연 추정기는 독립적인 사후 교차 분석 후보로 적합하다. PTT 측정 프로그램 전체를 제품에 추가하기보다, 기존 TransportProbe의 표본 추적과 별도의 오프라인 분석을 연결한다. 입력·출력 음성이 공통 녹음 시간축에 있어야 하며, 서로 다른 PC 녹음의 시작 시각을 빼는 것으로 대체하지 않는다.

## 확인한 소스

- [mouth2ear](https://github.com/usnistgov/mouth2ear/tree/a6fd5a02fdf2ea7cb1a41d7ef54c871dec5bbf66): 검토 리비전 `a6fd5a02fdf2ea7cb1a41d7ef54c871dec5bbf66`.
- [m2e.py](https://github.com/usnistgov/mouth2ear/blob/a6fd5a02fdf2ea7cb1a41d7ef54c871dec5bbf66/mcvqoe/mouth2ear/m2e.py): `process_audio`는 WAV를 읽고 `ITS_delay_est(..., "f")`로 클립의 고정 지연을 추정한다. 실제 녹음 없이 분석 부분의 분리가 가능하나 어댑터 검증이 필요하다.
- **보정 주의:** 같은 파일의 `dev_dly` 기본값은 **31ms**이고 분석에서 이를 빼는 경로가 있다. 이것을 우리 장치 보정값으로 사용하면 안 된다. 원시 추정치와 독립 검증한 측정계 보정치/불확도를 별도 저장하고, 대상 마이크·헤드폰의 실제 지연은 입→귀 측정에서 제거하지 않는다.
- [ITS_delay.py](https://github.com/usnistgov/mcvqoe-base/blob/b8f452b1e55bee46e6b4f37ee1ae7bbae674bee7/mcvqoe/delay/ITS_delay.py): 검토 리비전 `b8f452b1e55bee46e6b4f37ee1ae7bbae674bee7`. 고정/가변/자동 선택 모드, 최소 상관 임계값, 검색 범위를 제공한다. 내부 8kHz 변환의 시간 격자는 0.125ms이며 실제 정확도가 ±0.125ms라는 뜻은 아니다. 문서상 최소 음성 길이는148ms다. 추정 불가 때 `(0,0)`이 반환되는 경로가 있어 이를 지연0 성공으로 집계하지 않는다. 가변 모드에는 무효 구간의 지연을 주변 유효 구간에서 외삽하는 단계가 있으므로 모든 구간을 직접 관측한 것으로 표시하지 않는다.
- [sliding_delay.py](https://github.com/usnistgov/mcvqoe-base/blob/b8f452b1e55bee46e6b4f37ee1ae7bbae674bee7/mcvqoe/delay/sliding_delay.py): 기본4초 창/2초 간격. 짧은 순간 스파이크의 개별 마감을 그대로 측정하는 도구가 아니다. 창 길이·신뢰도·유효 음성·시계 드리프트의 별도 처리가 필요하다.
- [setup.py](https://github.com/usnistgov/mouth2ear/blob/a6fd5a02fdf2ea7cb1a41d7ef54c871dec5bbf66/setup.py): mcvqoe-base/numpy/pandas/plotly 등에 의존. 제품 런타임에 도입하지 않고 별도 분석 환경으로 격리하는 후보. 코드 복사 시 각 파일의 고지/사용 조건을 보존한다.

## 기존 도구와의 차이

- [RenderLatencyProbe](../TransportProbe/Automation/RenderLatencyProbe.cs): 샘플 해상도 정규화 상관 탐색과 감쇠/무음 회귀가 이미 있다. 같은 호스트 수신→디지털 루프백의 실험이며 물리 음향 측정이 아니다. 이 검출기를 그대로 재사용해도 노이즈 제거·음성 코덱·반향·반복 마커에서의 신뢰도가 보장되지는 않는다.
- [RenderPresentationProbe](../TransportProbe/Automation/RenderPresentationProbe.cs): PCM 위치→장치 스트림 시계의 대응. 실제 귀/마이크/원격망은 미측정이다.
- [CrossAppProbe](../TransportProbe/Automation/CrossAppProbe.cs): 수신 디지털 루프백과20ms 검출 양자화라는 한계를 기록한다. 새 정밀 입→귀 측정으로 이름만 바꾸지 않는다.

## 권장 구성과 검증 순서

1. 기존 PCM/패킷 추적을 유지하고 오프라인 WAV 분석 입출력 형식을 추가한다. 가능하면 공통 녹음 장치의2채널로 원음/수신음을 분리한다. 단일 마이크 혼합 녹음은 더 낮은 비용의 예비 측정이며, 원음·도착음 분리와 누설/반향 거부를 먼저 검증한다. NIST 분석기는 두 신호를 요구하므로 혼합 녹음에서 두 신호가 저절로 분리된다고 가정하지 않는다.
2. 음성 대역의 반복별 식별 가능한 입력과 원본 샘플 범위를 지정한다. 재생 명령 시각을 원음의 물리 발생 시각으로 쓰지 않는다. 녹음기까지의 음향 거리 차이를 기록한다.
3. 알려진 지연의 합성 WAV로 기존 상관기와 NIST를 독립 비교한다. 무음/다른 신호/반복 오검출/반향/클리핑/감쇠/잡음/Opus/드리프트/시간축 생략·중복을 포함한다. 검출 성공률과 편향·오차 분포를 확인하기 전 소수점 자릿수로 정확도를 주장하지 않는다.
4. 실제 원음→수신 출력의 공통 녹음과 내부 타임라인을 같은 마커로 연결한다. 시작 직후/정상 구간/침묵 후/장애 회복을 분리한다. 녹음·상세 기록을 끈 대조로 관측 부담도 확인한다.
5. 총지연, 각 호스트 내부 구간, 미분리 잔여, 측정 불확도를 따로 보고한다. 비동기 PC 시각 차감과 RTT/2를 정확한 편도 지연으로 표현하지 않는다. 잔여를 전부 네트워크로 돌리지 않는다.

현재 선택된 Python에는 numpy/scipy가 없어 실행 검증하지 않았다. 다른 Python 환경의 설치 여부는 조사하지 않았다. 새 패키지 설치, 장치 설정 변경, 녹음, 앱 코드 수정은 하지 않았다.

## 다른 후보

- [Open Sound Meter](https://opensoundmeter.com/), [지연 추정 릴리즈 안내](https://opensoundmeter.com/releases): 분리된 기준/측정 입력을 제공할 수 있을 때 수동 교차 검증 후보. 현재 입출력 구성에서의 호환성은 미시험.
- [REW timing reference](https://roomeqwizard.com/betahelp/help/html/makingmeasurements.html): 기준 경로 대비 지연 측정 후보. 음성 통신의 비선형/시변 경로에서 스윕 검출과 시계 차이 영향을 별도 검증해야 한다.

두 도구의 실제 소스 검토·설치·장치 호환성 검증은 이번 범위에 포함하지 않았다. 즉시 제품 의존성으로 추가할 근거는 없다.
