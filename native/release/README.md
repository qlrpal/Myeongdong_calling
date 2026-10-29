# 명동 콜링 0.6.1 베타 1 · Windows x64

ZIP 전체를 새 폴더에 풀고 **VoiceNative.exe**를 실행하세요. 파일을 낱개로 옮기지 마세요.

기본 베타 패키지는 .NET 실행 환경을 포함합니다. `release-manifest.json`의 `selfContained`가 false인 별도 패키지만 .NET 10 Desktop Runtime x64가 필요합니다.

사용법은 [시작하기](시작하기.md), 알려진 제한과 문제 제보 방법은 [베타 안내](베타안내.md)를 참고하세요.

직접 연결을 우선하고 필요하면 TURN으로 중계합니다. 게임과 함께 사용하는 WASAPI 공유 모드를 사용합니다. 사용자 공유기의 포트포워딩은 필요하지 않습니다.

이 빌드는 베타입니다. 모든 환경의 저지연·무중단 통화를 보장하는 안정판으로 확정하지 않았습니다. 오픈소스 고지는 함께 들어 있는 `THIRD-PARTY-NOTICES.md`와 licenses 폴더에 있습니다.
