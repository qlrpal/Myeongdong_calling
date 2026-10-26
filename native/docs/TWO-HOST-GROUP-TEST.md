# 두 Windows 호스트에서 3명·6명 시험

Windows PC와 노트북에서 각각 실제 통화 엔진3개를 실행한다. 첫3명은 PC1개+노트북2개,6명은 각3개다. 합성 음성을 인코딩 직전에 주입하고 실제 WASAPI 공유 입력/출력과 WebRTC를 사용한다. 각 피어는 별도 공유 출력 스트림으로 재생되고 Windows 공유 엔진에서 합산된다. 가상 오디오 장치나 앱의 별도 중앙 믹서를 가정하지 않는다.

## 범위와 한계

- 전체 메시의 참가자 수·피어별 송수신·디코딩·장치 스트림·입퇴장/엔진 재접속을 확인한다.
- 여러 클라이언트의 CPU·출력 부하가 두 호스트에 집중된다. 독립된6대PC, 실제6명 음성, 친구 WAN 또는 게임 부하 시험을 대신하지 않는다.
- 같은 호스트끼리의 연결과 호스트 사이의 연결을 별도로 집계한다. 같은 호스트 연결은 실제 집 네트워크를 거치지 않는다.
- 명시적 Leave/Join은 네트워크 단절 후 UI 자동 재시도 시험과 다르다. 자동 재시도·취소 정책은 ProductChecks에서 별도로 검사한다.
- 기본 기능 검증과 연속성/지연 판정을 구분한다. PLC·부족이 있어도 연결 기능만 완료할 수 있다.

## 준비

같은 소스/빌드의 TransportProbe를 두 호스트의 별도 시험 폴더에 준비한다. `speech.wav`는48kHz·16bit·모노 PCM,1~120초의 합성 음성이다. 원본 시험 WAV는 로컬 자료로 보존하며 Git에 포함하지 않는다. 다른 합성 파형으로 기능 시험은 가능하지만 CPU/비트레이트 숫자의 동일 재현을 보장하지 않는다.

기존 앱의 통화는 먼저 종료한다. 마이크·출력 장치는 실제로 열리며 시험 음성이 들릴 수 있다. 시험 피어의 앱 볼륨만 낮춘다. 시스템 음량·드라이버·전원·라우팅은 바꾸지 않는다. 실제 연결은 direct 강제이며 신호 서버 주소와 서로 겹치지 않는 방 이름을 사용한다.

새 폴더의 실행 파일은 기존 방화벽 허용을 상속하지 않을 수 있다. 양쪽의 **실제 실행 경로**를 먼저 기록하고 `./native/inspect-test-firewall.ps1 -Executable FULL_PATH_TO_TransportProbe.exe`로 해당 파일의 활성 UDP 인바운드 규칙을 읽는다. 차단 규칙의 프로필·주소·포트 조건을 확인하기 전에는 연결 실패를 SDK나 라우팅 결함으로 판정하지 않는다. 이 도구는 유효 정책 전체를 판정하지 않으며 규칙을 바꾸지 않는다. 허용 규칙이 없다는 사실만으로 차단을 확정하지도 않는다.

## 실행

```powershell
# 두 호스트 각각 실행하며 ROLE은 pc 또는 laptop이다.
TransportProbe.exe --two-host-group SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX
# CPU/장치 부하를 구간별로 비교하는 짧은3명/6명 대조
TransportProbe.exe --two-host-group-quick SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX
# 앱 자체의 송수신 인터페이스만 명시하는 대조(시스템 라우팅 변경 없음)
TransportProbe.exe --two-host-group-quick-pinned SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX LOCAL_MEDIA_ADDRESS
TransportProbe.exe --two-host-group-soak-pinned SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX LOCAL_MEDIA_ADDRESS
# 자동 경로 6인90초 경계 관측과600초 유지 시험
TransportProbe.exe --two-host-group-boundary SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX
TransportProbe.exe --two-host-group-soak SERVER ROOM REPORT.json ROLE speech.wav HOST_START_UNIX # 6인600초, 자동 경로
```

시작 시각은 각 호스트 UTC 기준120초 이내여야 한다. 두 시스템 시계에 차이가 있으면 원격 시각을 로컬 송수신 시각 사이에서 조회해 차이의 범위를 구한 뒤 각 HOST_START_UNIX를 보정한다. 시스템 시계를 임의 변경하거나 서로 다른 QPC 원점을 빼지 않는다. 단계 전환 전5초는 고정 멤버 측정에서 제외한다. 보정 불확실성이 이 완충보다 크면 시험하지 않는다.

전체 시험은3명60초 →6명120초 →1명퇴장30초 →재참가60초 →한 참가자10초퇴장 후 재접속60초 →전체퇴장20초 →6명600초 순서다. 짧은 시험은3명60초/6명90초다. 수렴에40초 이상 걸리면 실패한다. 단계의 준비 로그와 종료 JSON을 함께 보존한다. 스케줄 차이·준비 시간으로 실제 유효 측정 창은 지정 시간보다 짧다.

새 보고서는 실제 SDK·Opus 백엔드 식별자, 호스트 로컬 경과 시각이 붙은 상태 이벤트, 제한된 ICE 경로 로그, 구간 할당 바이트·GC 정지 시간을 포함한다. GC 정지는 프로세스 전체 합계이며 특정 음성 패킷 지연의 원인을 단독으로 증명하지 않는다.

## 분석

```powershell
python native/TransportProbe/Analysis/analyze_group_probe.py REPORT.json SUMMARY.json
python native/TransportProbe/Analysis/analyze_group_probe.py --self-test
python native/TransportProbe/Analysis/analyze_group_probe.py --pairs PC_REPORT.json LAPTOP_REPORT.json PAIRS.json
```

실패 보고서·미완료 단계·정리 실패·피어 수 불일치·미수신/미디코딩·피어 카운터 역행은 거부한다. 각 안정 구간의 첫10초를 제외하고 피어별 큐 중앙값/P95,PLC·부족·폐기와 송수신 프레임을 집계한다. CPU는 해당 시험 프로세스 안의 전체 엔진 합이며 단일 사용자 CPU나 시스템 전체 CPU가 아니다. 프로세스에 청구된 사용자/커널 시간을 포함하며 audiodg.exe·다른 프로세스·별도 시스템/드라이버 작업 전체의 부하는 포함하지 않는다. 프로세스 메모리에는 보존 중인 진단 자료가 포함되어 전후 값만으로 누수를 판정하지 않는다. 송신 최대 대기는 참가 이후 누적값이다. 단위는 앱 큐/네트워크 지표이며 물리 입→귀 지연을 표시하지 않는다.
