# SDK UDP 수신·TURN 종료 대조 빌드

현재 일반 실행은 [자동 빌드되는 수정 SDK](../docs/SDK-BUILD.md)를 사용한다. 이 도구는 원본/수정 A/B 대조와 추가 계측 전용이며 정상 실행에 필요하지 않다. 아래094/095의 승격 판단은 당시 기록이다.

SIPSorcery10.0.17의 정확한 커밋 `08c2ed0ad72c423ca06950dee2e842488a219884`를 별도 `.local` 폴더에서 빌드한다. 대조 도구는 일반 빌드 참조·잠금 파일·`dist`·시스템 네트워크 설정을 바꾸지 않는다. 공식 패키지 수정판이나 안정판 배포가 아니다.

[094 시험 결과](../docs/reports/EXTREME-094.md): 실제6인 A/B/A에서 중첩·중복/미관측 개선을 확인했으나 원본과 수정 모두 제품 재연결 실패가 남았다. 일반 배포용 기본값으로 승격하지 않았다.

[095 후속 결과](../docs/reports/EXTREME-095.md): TURN437과 OS의 닫힌 UDP 포트 재사용을 확인했다. 일반 앱은 최근 포트를 회피하도록 수정했다. 새 준비 도구는 정상 UDP 종료에 인증된 Refresh(0)를 보내는 별도 패치도 포함한다. 결합 후보는 두 PC의 제품 흐름3회 연속과6인90초 검사를 통과했다. 외부 환경의 장기 합격이나 안정판 승격을 의미하지 않는다.

```powershell
./native/sdk-experiment/prepare.ps1
# 준비된 시험 폴더를 지정해 수정된 별도 앱을 실행한다.
# ./native/sdk-experiment/run.ps1 -TrialDirectory ./native/.local/sdk-receive-TIMESTAMP-ID
# 출력된 TEST BIN의 TransportProbe.exe를 사용한다.
$env:VOICE_SDK_RECEIVE_DIAGNOSTICS='1'
$env:VOICE_SDK_RECEIVE_GATE='original'   # A: 기존 시작 로직 + 계측
# $env:VOICE_SDK_RECEIVE_GATE='serialized' # B: 같은 계측 + 수신 상태 확인/설정 잠금
$env:VOICE_SDK_TURN_RELEASE='1' # UDP 정상 종료 시 할당 삭제 요청, 대조는 '0'
```

도구는 Python·.NET10 SDK·인터넷과 정상 솔루션의 선행 restore가 필요하다. 상류 소스 압축본·원본 해시가 다르면 중단한다. 내려받은 상류 라이선스는 시험 폴더에 보존한다. 기본 제품 바이너리에 DLL을 덮어쓰지 않는다. 환경 변수는 이 시험 DLL만 읽는다. 수동 프로브 시험 종료 후 현재 셸의 세 환경 변수를 제거하고 정상 실행 경로를 사용하면 된다.

`--sdk-receive-race REPORT LOCAL_ADDRESS 50000` 및 [두 호스트 그룹 시험](../docs/TWO-HOST-GROUP-TEST.md)의 `--two-host-group-boundary-pinned`으로 비교한다. 두 호스트에서 같은 DLL 해시·같은 게이트를 사용하고 원본→직렬화→원본 순서로 별도 프로세스를 실행한다. 계측 없는 공식 패키지와의 차이도 고려해야 한다.

계측은 비동기 수신 시작 ID·버퍼 객체 식별자·미완료 작업 수 및 처리 소유권을 기록한다. IO 완료 후 버퍼 복사·콜백·동기 drain까지 끝나야 소유권이 해제된다. 처음64개 중첩 시작과 최대128개 관련 작업 수명만 보존하며 음성/UDP 내용은 저장하지 않는다. 동기 drain은 부모 비동기 작업에 포함된다. `sdkReceiveDiagnostics`는 정상 패키지에서는 null이다. 계측 잠금·시각 읽기·작업 객체 할당은 타이밍에 영향을 줄 수 있으므로 양쪽 조건에 똑같이 둔다.

잠금은 비동기 완료까지 유지하는 방식이 아니다. 기존 실행 상태 확인·설정만 직렬화하고 비동기 요청 등록 및 패킷 핸들러는 잠금 밖에서 실행한다. APM 콜백이 동기 실행될 가능성 때문에 등록 호출 전체에 잠금을 유지하지 않는다. 효과 여부는 실제 중첩 수·정체성 중복/미관측·PLC/부족·연결/종료·TURN 회귀로 판단한다. Task API 전환, 수신 버퍼 크기 및 코덱·재생 여유 변경은 이 대조에 섞지 않는다.

시험 폴더의 `VoiceNative.exe`도 같은 SDK를 사용하는 별도 앱 빌드다. 직접 실행할 때는 현재 셸의 게이트 값을 명시하고 사용한다. 현재 기본 수정 SDK로 돌아가려면 정상 `native/run.ps1`을 사용한다. 공식 원본 비교는 이 도구의 original 게이트로 수행한다.

`run.ps1`은 시험 manifest의 SDK 해시를 확인하고 기본적으로 수정 게이트를 선택한다. 추가 수명 계측은 `-TraceReceive`로 켠다. 실행 후 호출한 셸의 환경 변수는 복구한다. GUI WinExe를 실행하므로 앱 종료 대기나 실사용 품질 합격을 이 스크립트가 보장하지는 않는다.

새 manifest의 TURN 종료 패치가 있으면 GUI 실행기도 삭제 요청을 켠다. 삭제는 UDP에 한정된 best effort이며 응답 확인이나 즉시 삭제를 보장하지 않는다. 정상 앱 재연결에 대기2초를 넣지 않는다. 종료 송신은 해당 소켓에서100ms로 제한하며 TCP/TLS는 변경하지 않는다. `--turn-allocation-lifecycle SERVER REPORT LOCAL_ADDRESS`는 실제 TURN만 사용한 원본/삭제/원본 통제 시험이다. 같은 포트 재사용은 실패를 의도하므로 일반 음성 통화가 아닌 별도 시험 폴더에서 사용한다.
