# 두 PC 자동 기능 검사

PC와 노트북이 같은 시험에 참가해 양방향 전달·마이크 음소거 및 상대 상태 공유·각자의 퇴장/재참가·정리를 확인한다. 기존 [로컬 자동 검사](APP-CHECKS.md)를 통과한 다음 실행한다. 게임·물리 입→귀 지연·장시간 연속성 품질 검사를 대신하지 않는다.

## 실행

두 PC에 같은 소스/빌드와 Windows 공유 입력/출력 장치가 필요하다. .NET10 SDK·Node.js22 이상이 기본 준비 조건이며 첫 실행은 복원·빌드한다. 기존 통화를 먼저 종료한다. 합성 파형으로 마이크 PCM을 교체하고 작은 앱 볼륨으로 재생한다. 사용자의 실제 목소리는 전송/저장하지 않는다. 실제 장치·캡처 주기는 사용하지만 물리 마이크·귀 지연은 측정하지 않는다.

PC에서 실행 ID를 만든다. 이 ID를 노트북에도 복사한다. 한쪽이 너무 먼저 시작하지 않도록 양쪽 빌드를 미리 완료하는 것이 좋다.

```powershell
$run = [guid]::NewGuid().ToString('N')
$run # 표시된 32자 ID를 노트북에도 입력
./native/test-pair.ps1 -Server https://qlrpal.ddns.net/ -RunId $run -Role a -Route direct
```

노트북:

```powershell
$run = 'PC에서_만든_32자_ID'
./native/test-pair.ps1 -Server https://qlrpal.ddns.net/ -RunId $run -Role b -Route direct
```

복원 캐시가 준비됐으면 `-NoRestore`, 같은 소스로 이미 빌드했으면 `-NoBuild`를 양쪽에 붙일 수 있다. `-NoBuild`는 현재 소스를 자동 반영하지 않는다. 준비된 바이너리만 사용하는 경우 .NET10 Desktop Runtime과 Node만 필요하다. 빌드가 끝난 뒤 상대 참가 대기는 최대45초다. 단계/상대 확인도 각45초, 전체 참가자 프로세스 제한은기본360초이며 관측 시간을 늘린 경우 `max(360, 관측초+150)`초다. 상대 준비가 늦어 실패하면 원본을 보존하고 새 실행 ID로 다시 시작한다. 자동 재시도로 실패를 숨기지 않는다.

PowerShell 파일 실행 정책으로 진입점이 차단되는 환경은 정책을 바꾸지 않고 Node 진입점을 직접 사용할 수 있다. 예를 들어 이미 빌드한 노트북 역할은 다음과 같다. 포터블 Node를 사용할 때는 `node` 대신 해당 실행 파일의 전체 경로를 지정한다.

```powershell
node ./native/TransportProbe/Runner/pair-checks.mjs run --server https://qlrpal.ddns.net/ --run $run --role b --route direct --shared false --no-build
```

서로 다른 역할 a/b, 같은 실행 ID·경로·출력 방식·실제 DLL과 합성 WAV 해시를 사용해야 한다. 통화 중 설정이 다르면 중단한다. 시험 방은 `pair-<ID>`이며 다른 사용자가 들어오면 멤버 검사에서 실패한다. 시스템 시계의 차이를 음성 지연으로 계산하지 않는다. 기존 DTLS 데이터 채널의 단계 완료/확인 신호로 진행하므로 추가 제어 서버·포트가 필요 없다.

### 경로와 출력 대조

- 직접 연결 시험은 양쪽 `-Route direct`. TURN을 제외한 후보에서 실제 직접 경로 선택을 확인한다. 같은 LAN, VPN, 원격 인터넷 직접 경로를 자동으로 동일시하지 않는다.
- 강제 중계 시험은 새 실행 ID로 양쪽 `-Route relay`. 서버의 정상 TURN 구성과 실제 중계 경로가 필요하며, 직접 경로로 성공했다고 중계 시험을 통과시키지 않는다.
- 단일 출력 후보는 양쪽에 `-SharedOutput`. 생략하면 기존 출력이다. 프레임/버퍼를 함께 변경하지 않는다.

### VPN을 유지하는 통화별 LAN 지정

`-MediaAddress`는 **그 PC에 실제 할당된 로컬 유니캐스트 IP**다. 상대 IP를 넣지 않는다. 예를 들어 PC와 노트북이 각각 다음과 같다면 서로 다른 값을 사용한다.

```powershell
# PC
./native/test-pair.ps1 -Server https://qlrpal.ddns.net/ -RunId $run -Role a -Route direct -MediaAddress 192.168.0.10 -NoBuild
# 노트북
./native/test-pair.ps1 -Server https://qlrpal.ddns.net/ -RunId $run -Role b -Route direct -MediaAddress 192.168.0.11 -NoBuild
```

위 주소는 예시이며 실행 PC의 실제 LAN 주소로 바꾼다. Node 직접 실행은 `--media-address LOCAL_IP`다. 생략하면 기존 OS 자동 경로를 유지한다. 사용할 수 없는 주소는 실패로 보고하며 자동 경로로 몰래 대체하지 않는다.

선택은 시험 엔진의 UDP 소켓 바인딩/UNICAST_IF에만 적용한다. Tailscale 종료, 서브넷 경로 변경, VPN 설정, 방화벽 변경은 하지 않는다. 보고서에 요청 IP·선택된 인터페이스와 실제 소켓 옵션 readback, 실패 단계·정리 전 진단을 남긴다. 지정 모드에서는 연결 후 소켓의 인터페이스 인덱스가 일치하는지도 확인한다. 이 소프트웨어 소켓 경계를 물리 무선 송수신 시각으로 해석하지 않는다.

두 PC는 서로 다른 주소를 지정할 수 있고, 한쪽만 지정하는 대조도 가능하다. 병합 결과는 양쪽 지정값을 따로 보존한다. `자동 → 지정 → 자동` 대조는 각 단계마다 새 실행 ID를 사용하고 빌드·프레임·출력·경로는 유지한다. 양쪽 지정값을 동시에 바꾼 시험은 어느 한쪽의 단독 영향까지 분리한 실험이 아니다. 설정상의 주소만 보고 실제 경로 성공을 주장하지 않고 상대가 관측한 endpoint와 소켓 옵션을 함께 확인한다.

방화벽·라우팅·공유기·장치 기본값은 변경하지 않는다. 새로운 실행 파일 경로에 Windows 앱 방화벽 허용이 필요한 환경은 먼저 준비해야 한다. 이 도구는 SSH나 관리자 권한을 필수로 사용하지 않는다.

### 신호 서버에 도달하지 못하는 개발 환경

HTTP 참가 전 실패는 음성 지연 후보의 실패와 구분한다. 운영 서버를 고치거나 시험 조건을 섞는 대신 세 조건 모두에 같은 임시 localhost 신호 서버를 쓸 수 있다. 개발 PC에서 아래 fixture를 실행하면127.0.0.1의 임시 포트를 표시한다. 공개 인터페이스에 노출하지 않는다.

```powershell
node ./native/TransportProbe/Fixtures/lifecycle-server.mjs
# 별도 터미널: PC_PORT는 표시된 포트, USER/LAPTOP은 소유한 노트북의 SSH 계정/주소
ssh -N -o ExitOnForwardFailure=yes -R 127.0.0.1:39000:127.0.0.1:PC_PORT USER@LAPTOP
```

PC 역할a의 Server는 `http://127.0.0.1:PC_PORT/`, 노트북 역할b는 `http://127.0.0.1:39000/`이다. 두 URL은 SSH로 같은 서버에 연결된다. 음성은 이 터널을 쓰지 않으며 `-Route direct -MediaAddress LOCAL_IP`로 별도 지정한다. fixture ICE 목록은 비어 있으므로 강제 TURN 검증에는 쓸 수 없다. SSH 포워딩이 거절되면 임의로 SSH 서버 설정을 바꾸지 않는다. 끝나면 SSH와 임시 서버를 종료한다. 공개 신호 서버로 수집한 다른 조건의 표본과 혼합해 A/B/A를 만들지 않는다.

## 지연·연속성 기준선 관측

최신 시험 보고서는 `baselineIncident`에 기준선 종료 시점의 자동 사건 기록을 보존하고 `finalIncident`에 마지막 통화 기록을 포함한다. 재참가에서 초기화되는 기록과 구분한다. 수동 상세 기록을 켜지 않아도 수집되며, `incidentIncludesStartup=true`이므로 기준선 시작 이전의 첫 사건일 수 있다. [기록 범위/상한/경계의 의미](INCIDENT-RECORDING.md)를 확인한다. 사건이 없으면 Rolling 꼬리만 남으며 전체 기준선 패킷 기록으로 해석하지 않는다.

양쪽에 같은 `-ObserveSeconds 60`을 지정하면 baseline 구간을60초로 확장한다(허용5–300초, 기본5초). 초기 연결 뒤2초의 고정 준비 시간을 제외하고 관측한다. 마이크 음소거/해제 구간은 각각5초이며 입퇴장 검사는 유지한다. 양쪽 관측 시간이 다르면 중단한다. baseline에는250ms 진단 스냅샷과 C# 프로세스 CPU·할당·GC 정지 합계를 기록한다. 시스템 전체·audiodg·드라이버 CPU를 측정한 값은 아니다.

같은 빌드·파형·경로·주소·프레임·버퍼·장치로 기존 출력→단일 출력→기존 출력을 각각 새 실행 ID로 수행한다. 후보만 양쪽 `-SharedOutput`을 사용한다. 1:1은 양쪽 구조 모두 출력 스트림 하나이므로 다인의 출력 수 절감 효과를 평가할 수 없다.

분석 입력 manifest는 실행 순서대로 세 항목의 JSON 배열이며 각 항목에 `pcReport`, `laptopReport` 원본의 로컬 절대 경로를 넣는다.

```powershell
node ./native/TransportProbe/Runner/analyze-pair-observation.mjs MANIFEST.json OUTPUT_DIRECTORY
```

분석기는 양쪽 기능 검사·서로 다른 호스트 식별자·대조 순서·빌드/설정 일치와 카운터 역행·미완료 구간을 검사한다. 큐와 송신 대기는 진단 스냅샷 분포이며 패킷 전체 분포가 아니다. RTT는 유효한 서로 다른 로컬 RTCP 보고만 센다. PLC는 관측 시간 대비 마스킹 오디오 시간으로 표시하며 패킷 손실률이 아니다. 누락값은0으로 바꾸지 않고 미측정으로 남긴다. 출력 목표 패딩은 실제 하드웨어 대기 시간과 구분해 JSON에 보존한다.

`comparison.md/json`의 관측 완료는 후보 채택·음질 합격·물리 지연 감소를 뜻하지 않는다. 진단 수집 비용도 자원 계측에 포함된다. 실제 입→귀 지연, 게임 부하, 장시간 유지와 독립 다인 환경은 따로 확인해야 한다.

수신 여유 하나만 비교할 때는 양쪽 `-ReserveMs 15`(허용10–40ms, 기본20ms)를 지정할 수 있다. Node 진입점은 `--reserve-ms`이며 실제 RTP 버퍼에 요청값이 적용됐는지도 분석한다. 일반 통화 앱 기본값을 변경하는 옵션이 아니라 시험 도구의 기존 진단 매개변수다. 양쪽 값이 다르면 설정 불일치로 중단한다.

기존 출력·10ms 등 나머지 설정을 유지한20→15→20ms 대조는 분석 시 `--factor reserve`를 추가한다. 기본 분석 인자는 출력 구조 대조인 `--factor shared`다. 분석기의 `followUpEligible`은 양쪽 큐 중앙값/P95가 두 기준선보다 낮고 PLC·부족·재버퍼·늦은 폐기·송신 폐기·잘못된 패킷이 기준선 범위보다 나쁘지 않을 때의 후속 시험 대상 표시다. 누락 지표는 이 판정을 통과시키지 않는다. 한 번의 대조가 품질 합격이나 기본값 승격을 자동 결정하지 않는다.

```powershell
node ./native/TransportProbe/Runner/analyze-pair-observation.mjs MANIFEST.json OUTPUT_DIRECTORY --factor reserve
```

## 결과 병합

각 PC의 `native/.local/pair-checks/<UTC시각>-<고유값>/participant.json`을 한 PC로 모은다. 파일 이름이 같으므로 복사할 때 구분한다. a/b 순서를 지켜 병합한다.

```powershell
./native/test-pair.ps1 -ReportA C:\test\pc-participant.json -ReportB C:\test\laptop-participant.json
```

참가자6검사: baseline, muted, active, a-rejoin, b-rejoin, cleanup. 통화 연결만 확인하지 않고5초 구간별 송신·수신·디코딩 증가와 캡처 최신성, 로컬 음소거 RMS, 선택 경로·출력 오류를 검사한다. 상대 재참가는 실제 퇴장 및 새 세션 ID를 관측한다. 명시적 Leave/Join이며 물리 네트워크 장애의 자동 복구 시험과는 다르다.

병합 시 양쪽 완료·오류 없음·6검사 성공·실행/설정/빌드/파형/SDK 일치를 요구한다. 같은 컴퓨터 이름 해시이면 로컬 기능 성공이어도 **incomplete**로 표시한다. 다른 해시도 독립 하드웨어를 증명하는 인증 수단은 아니다. 사용자가 실제 시험 배치를 확인해야 한다. 한쪽의 성공 보고서만으로 두 PC 합격을 선언하지 않는다.

`summary.md/json`은 로컬 실행 결과, `pair-summary.md/json`은 양쪽 병합 결과다. `pass`만 종료 코드0이며 `fail`·`incomplete`는1이다. 실제 raw 진단·로그에는 IP/장치 식별자가 있을 수 있으며 결과 폴더는 Git 제외다. 지터/RTT/큐 길이를 물리 입→귀 지연으로 표현하지 않는다. PLC 시간·늦은 폐기·출력 부족 등은 원본 구간 진단에 남지만 이번 기능 성공은 그 품질 합격을 의미하지 않는다.

## 선택적 상세 기준선 기록

양쪽에 `-ObserveSeconds 45 -TraceBaseline`을 지정하면 baseline에서만 기존 제한형 PipelineTrace를 기록하고, 음소거·재참가 전에 중지한다. Node 진입점은 `--trace-baseline`이다. 생략하면 기록은 꺼져 있고 기존5–300초 관측 범위를 유지한다. 상세 기록은 양쪽 같은 설정이어야 하며 최대45초다. 최소 장치 주기의 읽기 기록이 기존24000개 상한을 초과하지 않도록 제한한다.

`participant.json`의 `traceBaseline`, `baselineTrace`에 송신·복호화 이후 수신·버퍼 입장·렌더 요청·재생 메타데이터가 있다. 음성·페이로드·토큰은 기록하지 않는다. 양쪽 병합은 상세 기록의 누락·빈 필수 배열·축출을 실패로 판정한다. 첫 참가/장치 준비부터의 기록이 아니므로 `traceIncludesStartup:false`이며 초기 큐/디코더 이력을 모른다. 최초 기록 이전에 입장한 패킷의 재생은 입장 시각과 대응하지 못할 수 있다.

기록 켬/끔의 처리 비용은 실제 환경에서 대조해야 한다. 이벤트 정체성·로컬 입장→렌더 시간을 확인할 수 있지만 NIC 물리 송수신 시각이나 입→귀 측정이 아니다. 실시간 SDK 경계의 의미는 기존 PipelineTrace 정의를 따른다. 생략한 경계를 네트워크 손실로 단정하지 않는다. [118 보고서](reports/EXTREME-118.md).
