# SRTP 초기 수신 상태 보호

수정 SDK `10.0.17+myeongdong.2 / udp1-turn1-rtp0`은 RTP 인증 전에 초기 수신 순번을 상태에 반영하던 동작을 수정한다. 첫 인덱스는 지역 변수로 계산하고, 인증에 성공한 뒤 기존 재전송 창 갱신을 사용한다. 정상 초기 순번0 거부와 위조된 첫 패킷의 초기 상태 오염을 회귀 검사한다.

```powershell
./native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe --srtp-protection-checks ./srtp-protection.json
```

AES128/256-GCM RTP를 .NET AES-GCM 결과와 비교하며 순번 래핑, 위조 태그, 재전송, 순서 변경, 키 재유도, 동시 SSRC 및 SRTCP를 검사한다. 공개된 고정 시험 키만 사용한다. 제품 진단의 `SrtpCipher`는 협상된 프로필 이름이며 키·salt·세션 컨텍스트는 내보내지 않는다.

단일 GCM 키 스케줄 재사용 후보는 작은 합성 시험에서 할당을 줄였지만 실제 2호스트 6엔진 A/B/A에서 개선되지 않아 제외했다. 암호화 협상 우선순위는 변경하지 않았다. 이 수정은 지연 감소량이나 지속적인 PLC 해결을 보장하지 않는다.
