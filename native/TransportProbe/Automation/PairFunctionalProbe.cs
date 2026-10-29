using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceNative;

internal static class PairFunctionalProbe
{
    internal static async Task RunAsync(string server, string run, string role, string route, string report, string wave, bool shared, string? mediaAddress = null, int observeSeconds = 5, int reserveMs = 20, bool traceBaseline = false)
    {
        if (traceBaseline && observeSeconds > 45) throw new ArgumentOutOfRangeException(nameof(observeSeconds), "상세 기준선 기록은 최대45초입니다.");
        if (reserveMs is < 10 or > 40) throw new ArgumentOutOfRangeException(nameof(reserveMs));
        if (observeSeconds is < 5 or > 300) throw new ArgumentOutOfRangeException(nameof(observeSeconds));
        if (!Guid.TryParseExact(run, "N", out _) || role is not ("a" or "b") || route is not ("direct" or "relay"))
            throw new ArgumentException("RunId는 GUID N 형식, 역할은 a/b, 경로는 direct/relay여야 합니다.");
        var request = CallRequest.Create(server, "pair-" + run, "pair-" + role, null, null);
        var buildHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(WebRtcCallEngine).Assembly.Location)));
        var probeHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(PairFunctionalProbe).Assembly.Location)));
        var fixtureHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(wave)));
        var protocol = new PairBarrier(run, role, route + ":" + shared + ":" + observeSeconds + ":" + reserveMs + ":" + traceBaseline + ":" + buildHash + ":" + probeHash + ":" + fixtureHash);
        var fixture = SpeechFixture.Load(wave);
        var engine = new WebRtcCallEngine { DirectOnly = route == "direct", ForceRelay = route == "relay",
            UseSharedOutput = shared, DiagnosticReserveMs = reserveMs, DiagnosticCaptureTransform = fixture.Fill, DiagnosticControlReceived = protocol.Receive };
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency); // Same policy selected by the normal UI.
        var session = new CallSession(engine);
        var clock = Stopwatch.StartNew(); var events = new List<string>(); var sync = new object();
        var checks = new List<object>(); var windows = new List<object>();
        var bindings = new List<object>();
        PipelineTraceSnapshot? baselineTrace = null;
        string? error = null;
        var stage = "prepare";
        MediaInterfaceChoice? selectedInterface = null;
        CallDiagnostics? failureDiagnostics = null;
        engine.Status += message => { lock (sync) events.Add($"{clock.Elapsed.TotalSeconds:F3}s {message}"); };
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Wait(Func<bool> condition, string stage, int seconds = 45)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (protocol.Error is { } mismatch) throw new InvalidOperationException(mismatch);
                if (deadline.Elapsed.TotalSeconds > seconds) throw new TimeoutException("단계 시간 초과: " + stage);
                await Task.Delay(100);
            }
        }
        PeerDiagnostics Peer()
        {
            var d = engine.GetDiagnostics();
            Require(d.InCall && d.CleanupError is null && d.Peers.Length == 1, "통화 멤버/정리 상태 오류");
            var p = d.Peers[0]; engine.SetPeerVolume(p.Id, .01f);
            Require(p.Name == (role == "a" ? "pair-b" : "pair-a") && p.State == "connected" && p.Audio.DeviceError is null,
                "상대 역할/연결/출력 오류");
            Require(route == "direct" ? p.Route == "직접 연결" : p.Route.Contains("TURN", StringComparison.OrdinalIgnoreCase), "선택 경로 불일치");
            return p;
        }
        async Task Connected()
        {
            stage = "connect/decode";
            await Wait(() => {
                var d = engine.GetDiagnostics();
                foreach (var p in d.Peers) engine.SetPeerVolume(p.Id, .01f);
                return d.InCall && d.Peers.Length == 1 && d.Peers[0].State == "connected" && d.Peers[0].Audio.DecodedFrames > 20;
            }, "연결/디코딩");
            _ = Peer();
            var observed = engine.DiagnosticMediaBindings();
            bindings.Add(new { stage, observed });
            if (selectedInterface is not null)
                Require(observed.Length == 1 && observed.All(s => s.Error is null && s.InterfaceIndex == selectedInterface.Index), "통화 소켓 인터페이스 적용 확인 실패");
        }
        async Task Barrier(string phase)
        {
            stage = "barrier/" + phase;
            var watch = Stopwatch.StartNew(); double? confirmed = null;
            while (true)
            {
                if (!protocol.Ready(phase)) _ = Peer();
                if (protocol.Error is { } mismatch) throw new InvalidOperationException(mismatch);
                engine.SendDiagnosticControl(protocol.Message(phase));
                if (protocol.Ready(phase)) confirmed ??= watch.Elapsed.TotalSeconds;
                if (confirmed is { } at && watch.Elapsed.TotalSeconds - at >= .7) break;
                if (watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("상대 단계 확인 시간 초과: " + phase);
                await Task.Delay(100);
            }
            Console.WriteLine("PASS barrier " + phase);
        }
        async Task Window(string phase, bool muted)
        {
            stage = "media/" + phase;
            var seconds = phase == "baseline" ? observeSeconds : 5;
            var first = Peer(); var samples = new List<CallDiagnostics>();
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes(); var pause = GC.GetTotalPauseDuration();
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                _ = Peer(); var d = engine.GetDiagnostics();
                Require(d.Microphone?.LastFrameAgeMs is < 500, "캡처 전달 정지");
                if (watch.Elapsed.TotalSeconds > 1)
                    Require(muted ? d.Microphone?.LevelDbFs is <= -119 : d.Microphone?.LevelDbFs is > -50, "마이크 음소거 상태 불일치");
                if (watch.Elapsed.TotalSeconds > 2)
                    Require(d.Peers.All(p => ParticipantAudioStatus.RemoteMuted(p.State, p.Latency) == muted), "상대 마이크 상태 공유 불일치");
                samples.Add(d); await Task.Delay(250);
            }
            var last = Peer();
            Require(first.Id == last.Id && last.TxFrames - first.TxFrames > 50 && last.RxPackets - first.RxPackets > 50
                && last.Audio.DecodedFrames - first.Audio.DecodedFrames > 50, "음성 송수신/디코딩 진행 없음");
            var elapsed = watch.Elapsed.TotalSeconds;
            process.Refresh();
            windows.Add(new { phase, requestedSeconds = seconds, elapsedSeconds = elapsed,
                cpuCores = (process.TotalProcessorTime - cpu).TotalSeconds / elapsed,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
                gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, samples });
            checks.Add(new { name = phase, ok = true });
        }
        async Task Rejoin(string leavingRole)
        {
            var previous = Peer().Id;
            await Barrier(leavingRole + "-leave");
            if (role == leavingRole)
            {
                await session.DisconnectAsync();
                Require(!engine.GetDiagnostics().InCall && engine.GetDiagnostics().Microphone is null, "퇴장 뒤 마이크 잔류");
                await Task.Delay(1500); await session.ConnectAsync(request); await Connected();
            }
            else
            {
                await Wait(() => engine.GetDiagnostics().Peers.Length == 0, "상대 퇴장 관측");
                await Connected(); Require(Peer().Id != previous, "상대 새 세션 미관측");
            }
            await Barrier(leavingRole + "-rejoined");
            checks.Add(new { name = leavingRole + "-rejoin", ok = true });
        }
        try
        {
            selectedInterface = MediaInterfaceSelection.Resolve(mediaAddress);
            engine.SetMediaInterface(mediaAddress);
            stage = "join";
            await session.ConnectAsync(request); await Connected(); await Barrier("ready");
            await Task.Delay(2000); // Fixed device/media warmup, excluded from observation resources.
            if (traceBaseline) engine.StartPipelineTrace(observeSeconds + 3);
            await Window("baseline", false);
            if (traceBaseline)
            {
                baselineTrace = engine.StopPipelineTrace();
                Require(baselineTrace.EvictedRecords == 0 && baselineTrace.Reads.Length > 0 && baselineTrace.Admissions.Length > 0 && baselineTrace.Receives.Length > 0, "상세 기준선 기록 누락/축출");
            }
            await Barrier("baseline");
            engine.SetMicrophoneMuted(true); await Window("muted", true); await Barrier("muted");
            engine.SetMicrophoneMuted(false); await Window("active", false); await Barrier("active");
            await Rejoin("a"); await Rejoin("b");
            await Barrier("finish"); stage = "cleanup"; await session.DisconnectAsync();
            var idle = engine.GetDiagnostics();
            Require(!idle.InCall && idle.Microphone is null && idle.Sender is null && idle.Peers.Length == 0 && idle.SharedOutput is null or { OutputStreams: 0, Sources: 0 }, "최종 정리 미완료");
            checks.Add(new { name = "cleanup", ok = true });
            stage = "complete";
        }
        catch (Exception failure) { error = failure.ToString(); failureDiagnostics = engine.GetDiagnostics(); bindings.Add(new { stage, observed = engine.DiagnosticMediaBindings() }); }
        finally
        {
            if (traceBaseline && baselineTrace is null) baselineTrace = engine.StopPipelineTrace();
            var cleanup = new CallCleanup();
            await cleanup.RunAsync("session", () => session.DisposeAsync().AsTask());
            await cleanup.RunAsync("engine", () => engine.DisposeAsync().AsTask());
            try { cleanup.ThrowIfFailed(); } catch (Exception failure) { error = (error ?? "") + "\n" + failure; }
            string[] recorded; lock (sync) recorded = events.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { schemaVersion = 1, run, role, route, shared,
                completed = true, error, stage, observeSeconds, reserveMs, traceBaseline, baselineTrace, traceIncludesStartup = false, mediaAddress, selectedInterface, bindings, failureDiagnostics, buildHash, probeHash, fixtureHash, sdk = SdkBuildIdentity.Read(),
                hostFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName))),
                checks, windows, events = recorded,
                notes = "Actual shared capture/render with synthetic PCM replacement. Phases use authenticated data-channel acknowledgements and local monotonic deadlines, not cross-host clock subtraction. Functional test, not physical mouth-to-ear latency, game load or PLC quality qualification." }, DiagnosticJson.Readable));
        }
        if (error is not null) throw new InvalidOperationException("양방향 기능 검사 실패. 로컬 보고서를 확인하세요.");
    }
}
