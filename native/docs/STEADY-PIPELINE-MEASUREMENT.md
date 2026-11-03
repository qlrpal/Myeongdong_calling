# 평상시 소프트웨어 대기 측정

[출력 장치 시계 대조144](reports/EXTREME-144.md)는 `TransportProbe --render-presentation-device NEW-REPORT.json`으로 실행한다. 실제 렌더 PCM의 상관 위치를 IAudioClock 관측 사이에서 보간하고 송출 위상을 장치 시계에 정렬한다. 루프백 캡처 QPC를 쓰지 않으며, 신뢰 검사/PCM 경계 자체 검사는 `--render-presentation-checks`다. timing `complete`와 회차별 `continuityPassed`를 모두 읽어야 한다. 이 도구의 수신 입장→장치 시계는 마이크·코덱 내용상 지연·네트워크·물리 입→귀가 아니며, 명시적으로 설치한 observer 비용이 포함된다. 일반 실행에서는 observer를 설치하지 않는다.

[위상/수신 여유 재현142](reports/EXTREME-142.md)은 성공한45초 상세 pair 보고서의 도착/read 잠금 시각을 합성 Opus로 재생한다. `TransportProbe --recorded-reserve-replay PARTICIPANT.json OUTPUT.json`으로 실행한다. 원래 초기 상태·PCM·실시간 CPU·장치 출력 복원이 아니며, 후보의 큐 감소와 전체 PLC/폐기를 함께 비교하는 도구다. 낮은 하한 후보는 제품 기본값으로 채택되지 않았다.

렌더 소진과 PLC는 [사건 분리141](reports/EXTREME-141.md)처럼 별도로 확인한다. 기존 기록을 읽는 명령은 다음과 같다. 다른 PC 시각을 빼거나 큐를 실제 입→귀로 바꾸지 않는다.

```powershell
python native/TransportProbe/Analysis/analyze_playout_failures.py PARTICIPANT.json OUTPUT.json
```

`counterEvents`는250ms 표본 사이의 증가 구간이며 정확한 패킷 시각이 아니다. `renderDrains`는 보존된 소진 앞뒤의 소스 상태이고, `snapshotIntervalInsideCapsule=false`인 나중의 PLC를 첫 캡슐만으로 설명하면 안 된다. 실패/미완료 보고서는 정상 기준선 분석에서 거부한다.

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


## 장시간 관측과 시작 정착 분리

검사 실행기의 `--observe-seconds`는5~3600초를 지원한다.300초를 넘는 기준선 진단은1초 간격이고 그 외 창은250ms다. 상세 `--trace-baseline`은 계속45초 이하만 허용한다. 긴 관측은 상세 패킷 기록을 무제한 늘리는 모드가 아니며,1Hz 표본 사이의 정확한 사건 시각은 판별하지 못한다.

`--warmup-seconds 60`으로 장치/미디어 준비 뒤 관측 전 정착을2~60초로 지정한다. 양쪽이 같은 값을 써야 한다. 이 시간은 baseline 자원/품질 표본 구간 밖이며 `warmupSeconds`, `beforeWarmup`, `afterWarmup`으로 별도 보존한다. 시작 실패나 정착 중의 문제를 없었던 것으로 판단하지 않는다. 생명주기/재참가 검사는 여전히 관측 뒤에 실행한다. 일반 앱의 시작 지연을 늘리는 설정이 아니다.

```powershell
# 양쪽 기존 pair 실행 명령에 같은 값을 추가한다.
# 정상 구간 상세 비교
# --observe-seconds 45 --warmup-seconds 60 --trace-baseline
# 저계측30분 관측
# --observe-seconds 1800 --warmup-seconds 60

# 워밍업 재고 산술48조건: 빈 새 출력 파일명을 쓴다.
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --warmup-transfer-checks native/.local/warmup-new.json
```

마지막 도구는 실제 Opus/RTP 버퍼와 결정적인 장치 패딩 산술만 사용한다. 규칙적 도착·네 위상·128/256/480샘플 읽기·미디어 시작1/3초·워밍업 유무를 비교한다. `TotalInventoryMs`는 소스 큐와 모델 패딩의 재고 합이며 물리 지연이 아니다. 처음부터 낮은 패딩을 쓰는 대조는 안전성이 검증된 제품 정책이 아니다. 실패한 정착/PI/5ms 후보는 활성 제품 코드에 남기지 않았다. 상세 결과는 [145](reports/EXTREME-145.md), [146](reports/EXTREME-146.md), [147](reports/EXTREME-147.md)에 있다.
