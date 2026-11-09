# 명동콜링 2.0

Windows 극저지연 음성 통화. 최우선 목표는 실제 입→귀 지연 최소화이며, 손실·끊김 개선은 후속 단계에서 한다.

`v2` 브랜치는 기존 C# 버전(`native`, `codex/extreme-latency`)을 대체하는 Rust 전면 재작성이다.

| 문서 | 내용 |
|---|---|
| [AGENTS.md](AGENTS.md) | Codex·Claude Code 공통 작업 지침 (`CLAUDE.md`가 이것을 불러옴) |
| [docs/DESIGN.md](docs/DESIGN.md) | 합의된 설계와 결정 상태 |
| [docs/ROADMAP.md](docs/ROADMAP.md) | 단계·관문·실험 설계 |
| [docs/RIG.md](docs/RIG.md) | P0 측정 리그 |
| [docs/DISCUSSION-LOG.md](docs/DISCUSSION-LOG.md) | 설계 토론 경과, 철회·기각 사항 |

## 원칙 (사용자 지정)

- **R1.** 기존 명동콜링 측정값은 가설 생성에만 쓰고, 새 구현의 성능 판정에는 쓰지 않는다.
- **R2.** 논리 쟁점은 반례·불변식으로 닫고, 측정 쟁점은 실험 방법·판정 기준 합의로 닫는다.

## 크레이트 (의존성은 아래 방향만 허용)

| 크레이트 | 책임 | 단계 |
|---|---|---|
| `timeline` | 샘플 시간축, StreamKey·Epoch·ClockDomainId, MediaFrame, 캡처 불연속 판정, 수신 분류 | 구현됨 |
| `audio` | WASAPI 캡처·렌더, 장치 시계, P1 프로브 기록 타입 | 타입 정의됨 |
| `codec` | PCM·표준 Opus·Opus Custom | P2 |
| `jitter` / `drift` | 재생 마감·폐기 / 상대 시계 추정·보정 | P2·P3 |
| `engine` | 스레드 소유와 조립 | P2 |
| `rig` | P0 측정 신호·분석 | P0 |
| `wire-mc` / `wire-rtp` | 패킷 형식 변환 | P3 |
| `transport` / `signal` | 연결·암호화 / 시그널링 메시지 | P3·P5 |
| `server` / `app` | 서버 / UI | P5·P7 |

```
timeline ← codec, wire-*, jitter, drift, audio
transport → timeline, signal        (wire-*는 P3에서 어댑터로 연결)
engine → audio, codec, jitter, drift, transport, wire-mc, timeline
app → engine        rig → audio, timeline        server → signal
```

## 시간축 의미

- `ClockDomainId`: 물리 발진기. 같은 ID면 epoch를 넘어서도 속도(ppm) 추정이 유효하다.
- `SampleIndex`: 한 `StreamKey`(peer, stream, epoch) 안의 송신 캡처 샘플 카운터. 캡처 유실은 정확한 길이를 알 때만 건너뛴다. epoch가 다르면 오프셋 관계가 없다.
- 길이를 모르는 불연속, 장치 위치 역행, 상한 초과 → 새 epoch (클록 도메인 유지).
- 장치 변경 → 새 epoch + 새 클록 도메인.

## 개발

```
cargo test --workspace
```

Windows 전용 구현(WASAPI, libopus)은 P1·P2에서 `cfg(windows)`로 추가한다.
