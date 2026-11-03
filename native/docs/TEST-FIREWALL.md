# 반복 시험의 Windows 방화벽 준비

Windows의 앱 인바운드 허용 창은 UAC와 별개다. UAC 알림을 끈 상태에서도 방화벽 창은 뜰 수 있다. 앱 규칙은 실행 파일의 전체 경로를 기준으로 하므로 새 실험 폴더마다 실행하면 허용을 반복해야 할 수 있다.

## 기본 검사 경로를 고정한다

반복 시험은 다음 실행 경로를 사용한다.

```text
native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe
```

같은 경로에서 다시 빌드하며, A/B 원본은 Git 제외 캐시에 보관한다. 서로 다른 바이너리의 대조가 필요하면 검사 프로세스가 완전히 종료된 상태에서 해당 실행 폴더 전체를 교체하고 실제 로딩 해시를 기록한다. 교체 전 원본 백업·종료 후 해시 복구가 필요하다. 별도 경로가 꼭 필요하면 그 **정확한 실행 파일 경로**에만 규칙을 준비하고 확인한 뒤 시작한다. 운영 앱이나 실행 중인 프로세스의 DLL을 바꾸지 않는다.

## 앱 한정 허용

관리자 PowerShell에서 해당 검사 경로에 공용/프라이빗 인바운드 허용 규칙을 한 번 등록한다. 공용/프라이빗은 Windows가 판단한 현재 네트워크 프로필이며, 집의 LAN도 공용으로 설정돼 있을 수 있다. 네트워크 분류 자체를 바꿀 필요는 없다.

```powershell
$probe = (Resolve-Path 'native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe').Path
New-NetFirewallRule -Name 'MyeongdongCalling-TransportProbe-Release' `
  -DisplayName 'Myeongdong Calling fixed Release test tool' `
  -Program $probe -Direction Inbound -Action Allow `
  -Protocol Any -Profile Private,Public
```

이미 같은 이름의 규칙이 있으면 중복 생성하지 말고 `Get-NetFirewallRule -Name 'MyeongdongCalling-TransportProbe-Release'`와 `Get-NetFirewallApplicationFilter`로 경로/허용/활성/프로필을 확인한다. 프로토콜 Any는 이 실행 파일에만 적용되며 시스템 전체 포트를 여는 규칙이 아니다. 명시적 차단 규칙이나 조직 정책이 있으면 허용 규칙보다 우선할 수 있으므로 단순히 새 허용을 추가하고 성공으로 판단하지 않는다. 기존 사용자 정의 차단이나 전체 방화벽 정책을 임의로 제거하지 않는다.

시험 전에는 다음을 문자열로 확인한다. NetSecurity enum의 숫자만 보고 허용/차단을 판단하지 않는다.

- `Action.ToString()`이 `Allow`, `Enabled.ToString()`이 `True`인지
- 실제 실행 파일의 전체 경로가 규칙과 일치하는지
- 현재 네트워크 프로필이 규칙 범위에 포함되는지
- 같은 경로의 명시적 차단이나 상위 정책이 있는지

최종 통신 가능 여부는 실제 송수신 검사로 별도 확인한다. 규칙 존재만으로 WebRTC/직접 연결이나 무손실을 보장하지 않는다. localhost 시험 성공도 다른 PC의 인바운드 허용을 검증한 결과로 사용하지 않는다.

## 알림만 끄는 설정과 구분

`Set-NetFirewallProfile -NotifyOnListen False`는 알림을 숨길 뿐 앱의 수신을 허용하지 않는다. 이를 시험 실패 해결책으로 사용하지 않는다. 기본 반복 시험은 전체 방화벽/UAC/전역 알림 설정을 유지하고 고정 검사 경로만 허용한다.

## 2026-11-03 로컬 준비 결과

사용자의 반복 허용 창 제거 요청에 따라 위 고정 Release 경로의 관리용 허용 규칙을 등록하고 경로·활성·공용/프라이빗 범위를 readback했다. 등록 후 확인 시점의 프로젝트 경로 PersistentStore 규칙115개는 모두 문자열 `Allow`였으며, 차단으로 해석했던 숫자값 판단은 정정했다. 기존 규칙을 비활성화하거나 삭제하지 않았다. 이 현재 상태만으로 과거 각 시험 시점의 규칙 상태를 소급 확정하지 않는다.

Domain/Private/Public 방화벽 활성과 `NotifyOnListen=True`는 유지했다. 현재 실행 토큰은 관리자 토큰이 아니었으므로 준비한 앱 한정 설정 스크립트만 Windows의 관리자 실행으로 수행했다. UAC 알림 설정과 현재 프로세스의 관리자 권한은 같은 개념이 아니다.

설정 전 자료와 해당 로컬 PC의 복구 스크립트는 Git 제외 `native/.local/test-firewall/before.json`, `restore.ps1`에 보존했다. 관리자 실행으로 복구하면 이번에 추가한 관리용 규칙만 제거하며 기존 허용 규칙은 유지한다. 노트북·운영 서버·배포 앱의 방화벽은 이번 준비에서 바꾸지 않았다.

근거: [Microsoft 방화벽 규칙](https://learn.microsoft.com/windows/security/operating-system-security/network-security/windows-firewall/rules), [프로필 알림 설정](https://learn.microsoft.com/powershell/module/netsecurity/set-netfirewallprofile).
