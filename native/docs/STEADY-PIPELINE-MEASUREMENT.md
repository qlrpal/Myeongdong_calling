# 평상시 소프트웨어 대기 측정

간헐적 공백 조사와 별도로 평상시 앱 내부 대기를 비교하는 오프라인 분석이다. 실제 입→귀 측정 도구는 아니다. [실제 대조와 기각 결과134](reports/EXTREME-134.md).

두 Windows 참가자를 동일 소스/빌드/합성 WAV/수신 여유/출력 구성으로 준비한다. 기존 `TransportProbe/Runner/pair-checks.mjs run`에 양쪽 같은32자리 실행 ID, 각각 `--role a`/`--role b`, `--observe-seconds 45 --trace-baseline --reserve-ms 20`을 전달한다. `--server`, `--route direct` 또는 `relay`, `--shared false`, 필요하면 각 호스트의 `--media-address`를 명시한다. 기존 실행 문서의 방화벽/준비 조건을 따른다. 시스템 라우팅이나 장치 설정을 바꾸지 않는다. 별도 SDK 경계 계측은 끄고, 전체 상세 기록은 최대45초만 사용한다.

```powershell
python native/TransportProbe/Analysis/analyze_steady_pipeline.py A.json A-stages.json
python native/TransportProbe/Analysis/analyze_steady_pipeline.py B.json B-stages.json
python native/TransportProbe/Analysis/analyze_paired_software.py A.json B.json paired-stages.json
python -m unittest discover -s native/TransportProbe/Analysis -p 'test_*.py'
```

입력은 두 `participant.json`이며 기능 검사가 완료되고 상세 기록의 축출이 없어야 한다. 분석은 안정된 상대 하나의 기준선을 대상으로 한다. 다인 결과에 그대로 사용하지 않는다. 판별 불가능한 반복 RTP 식별자는 조용히 하나를 고르지 않고 실패한다. 양쪽 빌드·합성 입력 해시·경로·출력 구성·여유·시간이 다르면 쌍 분석을 거부한다.

`tx`는 장치 보고 첫 샘플/콜백부터 조립·인코딩·송신 큐·API까지, `rx`는 인증 콜백/입장부터 디코딩·첫 PCM 공급까지의 호스트 내부 기간이다. `actual`의 장치 주기/패딩/큐 목표는 요청값과 구별해서 읽는다. `wholeBaselineQuality`는 정상 창 필터를 적용하지 않은 기준선 진단 표본 차이다. `frameCounts`와 `matchedFrames`/`matchedAudioMs`로 제외·대응 범위를 함께 읽는다.

쌍 분석의 `sampleWeightedMeanMs`는 **같은 프레임의 두 호스트 내부 기간 합**이다. 전송과 공급 이후 실제 출력이 빠지며 장치 샘플 시각도 음향 시각이 아니다. 프레임 첫 샘플·렌더 단위에 따른 양자화, 정상 프레임의 생존 편향이 남는다. RTT·중앙값 합·교차 PC 절대 시각 차이로 입→귀 수치를 만들지 않는다. 상세 계측 자체의 할당/잠금 비용이 있으므로 여기의 CPU·GC를 일반 앱 비용과 동일시하지 않는다.

동일 조건 A/B/A로 한 변수만 바꾸고 두 방향과 PLC 음성시간·늦은 폐기·재버퍼·캡처 불연속·렌더 부족·송신 폐기까지 비교한다. 송신 단계만 감소하거나 한 방향만 유리하면 기본값 승격 근거로 삼지 않는다. 원본/계산 결과는 개인 정보가 포함될 수 있으므로 Git 제외 위치에 저장한다. 기본 실행으로 복귀할 때는 진단 프로세스를 종료하고 `./native/run.ps1`을 사용한다. 이번 기각 후보용 환경 변수는 현재 코드에 남아 있지 않다.
