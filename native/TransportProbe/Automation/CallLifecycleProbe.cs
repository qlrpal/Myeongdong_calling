using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace VoiceNative;

internal static class CallLifecycleProbe
{
    private static readonly int[] Followers = [1, 2];
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string server, string report, string wave, bool shared)
    {
        using var control = new HttpClient { BaseAddress = new Uri(server), Timeout = TimeSpan.FromSeconds(5) };
        var engines = new List<WebRtcCallEngine>(); var sessions = new List<CallSession>();
        var events = new List<string>(); var checks = new List<object>(); var sync = new object();
        var attempts = new Dictionary<string, int>(); var failures = new HashSet<string>(); var armed = false;
        string? error = null; var clock = Stopwatch.StartNew();
        var room = "life-" + Guid.NewGuid().ToString("N")[..24];
        CallRequest Request(int i) => CallRequest.Create(server, room, "lifecycle-" + i, null, null);
        async Task Flags(bool configFail = false, bool hangLeave = false)
        { using var response = await control.PostAsJsonAsync("__test/flags", new { configFail, hangLeave }); response.EnsureSuccessStatusCode(); }
        async Task Ready(int[] members, bool recovering = false)
        {
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var ready = true;
                foreach (var i in members)
                {
                    var d = engines[i].GetDiagnostics();
                    foreach (var p in d.Peers) engines[i].SetPeerVolume(p.Id, .01f);
                    if (recovering) sessions[i].Observe(d);
                    ready &= d.InCall && d.Peers.Length == members.Length - 1
                        && d.Peers.All(p => p.State == "connected" && p.Audio.DeviceError is null && p.Audio.DecodedFrames > 20);
                    if (recovering) ready &= sessions[i].State.Contains("재연결 완료", StringComparison.Ordinal);
                }
                if (ready) return;
                if (timeout.Elapsed.TotalSeconds > 40) throw new TimeoutException("Lifecycle members did not converge");
                await Task.Delay(100);
            }
        }
        static bool Idle(WebRtcCallEngine engine)
        {
            var d = engine.GetDiagnostics();
            return !d.InCall && d.Microphone is null && d.Sender is null && d.Peers.Length == 0
                && d.SharedOutput is null or { OutputStreams: 0, Sources: 0 };
        }
        try
        {
            await Flags();
            for (var i = 0; i < 3; i++)
            {
                var fixture = SpeechFixture.Load(wave); var slot = i;
                var engine = new WebRtcCallEngine { DirectOnly = true, UseSharedOutput = shared, DiagnosticCaptureTransform = fixture.Fill,
                    DiagnosticAfterCleanup = i == 0 ? stage =>
                    {
                        if (!armed) return;
                        attempts[stage] = attempts.GetValueOrDefault(stage) + 1;
                        if (failures.Remove(stage)) throw new IOException("Injected after actual cleanup: " + stage);
                    } : null };
                engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
                engine.Status += _ => throw new InvalidOperationException("Injected status observer");
                engine.Status += line => { lock (sync) events.Add($"{clock.Elapsed.TotalSeconds:F3}s {slot}: {line}"); };
                engines.Add(engine); sessions.Add(new(engine, (_, token) => Task.Delay(100, token)));
                await sessions[i].ConnectAsync(Request(i));
            }
            await Ready([0, 1, 2]);
            failures.UnionWith(["마이크", "상대 연결"]); if (shared) failures.Add("공통 출력");
            armed = true;
            CallCleanupException? captured = null;
            try { await engines[0].LeaveAsync(); }
            catch (CallCleanupException expected) { captured = expected; }
            armed = false;
            Require(captured is not null && captured.Stages.Length == (shared ? 3 : 2) && failures.Count == 0
                && attempts.GetValueOrDefault("상대 연결") == 2 && Idle(engines[0]), "Injected cleanup stopped later resources");
            checks.Add(new { name = "after-release-failure", stages = captured!.Stages, peerAttempts = attempts["상대 연결"], idle = true });
            sessions[0].Observe(engines[0].GetDiagnostics());
            Require(engines[0].GetDiagnostics().CleanupError is not null && !sessions[0].WantsCall && !sessions[0].IsBusy,
                "Cleanup warning triggered automatic microphone reopen");
            checks.Add(new { name = "cleanup-warning-blocks-auto-recovery", state = sessions[0].State });
            await Ready([1, 2]);
            var progress = Followers.ToDictionary(i => i, i => engines[i].GetDiagnostics().Peers.Single().Audio.DecodedFrames);
            await Task.Delay(500);
            Require(Followers.All(i => engines[i].GetDiagnostics().Peers.Single().Audio.DecodedFrames - progress[i] > 10), "Other peers stalled after target cleanup failure");
            checks.Add(new { name = "other-peers-continue", ok = true });
            await sessions[0].DisconnectAsync();
            await sessions[0].ConnectAsync(Request(0)); await Ready([0, 1, 2]);
            Require(engines[0].GetDiagnostics().CleanupError is null, "Deliberate new call retained old cleanup warning");
            checks.Add(new { name = "rejoin-after-failed-cleanup", ok = true });
            var oldCleanup = engines[0].DiagnosticCaptureStreamCleanup();
            await sessions[0].DisconnectAsync(); await Ready([1, 2]);
            await sessions[0].ConnectAsync(Request(0)); await Ready([0, 1, 2]);
            await oldCleanup();
            Require(engines[0].GetDiagnostics().InCall && engines[0].GetDiagnostics().Peers.Length == 2,
                "Delayed cleanup for an old stream closed the new call");
            checks.Add(new { name = "old-stream-cleanup-skips-new-call", ok = true });

            // Invalid selection affects this engine only, never the Windows default.
            await using (var invalid = new WebRtcCallEngine { DirectOnly = true })
            await using (var invalidSession = new CallSession(invalid))
            {
                Exception? failure = null;
                try { await invalidSession.ConnectAsync(Request(9) with { CaptureId = "nonexistent-lifecycle-capture" }); }
                catch (CallOperationException expected) { failure = expected; }
                Require(failure is CallOperationException { Kind: CallFailureKind.InputDevice } && Idle(invalid), "Input failure misclassified or leaked");
                checks.Add(new { name = "invalid-input", state = invalidSession.State });
            }
            await using (var invalid = new WebRtcCallEngine { DirectOnly = true })
            await using (var invalidSession = new CallSession(invalid))
            {
                Exception? failure = null;
                try { await invalidSession.ConnectAsync(Request(8) with { RenderId = "nonexistent-lifecycle-render" }); }
                catch (CallOperationException expected) { failure = expected; }
                Require(failure is CallOperationException { Kind: CallFailureKind.OutputDevice } && Idle(invalid), "Output failure misclassified or leaked");
                checks.Add(new { name = "invalid-output", state = invalidSession.State });
            }
            await Ready([0, 1, 2]);
            await Flags(configFail: true);
            await using (var unavailable = new WebRtcCallEngine { DirectOnly = true })
            await using (var unavailableSession = new CallSession(unavailable))
            {
                try { await unavailableSession.ConnectAsync(Request(7)); throw new InvalidOperationException("503 accepted"); }
                catch (HttpRequestException) { }
                Require(Idle(unavailable) && unavailableSession.State.Contains("서버", StringComparison.Ordinal), "Server failure misclassified or leaked");
                checks.Add(new { name = "server-503", state = unavailableSession.State });
            }
            await Flags();
            using (var drop = await control.PostAsJsonAsync("__test/drop", new { })) drop.EnsureSuccessStatusCode();
            await Ready([0, 1, 2], recovering: true);
            checks.Add(new { name = "actual-sse-disconnect-recovery", ok = true, states = sessions.Select(s => s.State).ToArray() });

            await Flags(hangLeave: true);
            var elapsed = Stopwatch.StartNew(); var leaving = sessions[0].DisconnectAsync();
            while (!Idle(engines[0]) && elapsed.Elapsed.TotalSeconds < 2) await Task.Delay(20);
            Require(Idle(engines[0]) && !leaving.IsCompleted, "Local cleanup waited for hung leave notification");
            await leaving.WaitAsync(TimeSpan.FromSeconds(8));
            Require(elapsed.Elapsed.TotalSeconds < 8 && !sessions[0].IsBusy && !sessions[0].WantsCall, "Hung server blocked leave");
            checks.Add(new { name = "hung-leave-notification", seconds = elapsed.Elapsed.TotalSeconds, idle = true });
            await Flags(); await Ready([1, 2]);
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            try { await Flags(); } catch (Exception) { }
            var cleanup = new CallCleanup();
            foreach (var session in sessions) await cleanup.RunAsync("session", () => session.DisposeAsync().AsTask());
            foreach (var engine in engines) await cleanup.RunAsync("engine", () => engine.DisposeAsync().AsTask());
            try { cleanup.ThrowIfFailed(); } catch (Exception failure) { error = (error ?? "") + "\nCleanup: " + failure; }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        string[] recorded; lock (sync) recorded = events.ToArray();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { shared, error, checks, events = recorded,
            notes = "Three real engines on ONE PC, default shared input/render with synthetic speech. Explicit invalid endpoint IDs do not modify OS settings. Diagnostic errors are injected AFTER actual cleanup: proves subsequent stages execute, not that native release can never fail before completion. Localhost fixture drops real SSE and stalls leave response. Not physical unplug, independent PC, Internet/TURN or audio latency testing." }, DiagnosticJson.Readable));
        if (error is not null) throw new InvalidOperationException(error);
        Console.WriteLine("PASS: real local cleanup fault continuation, device/server classification, SSE recovery and bounded leave notification; shared=" + shared);
    }
}
