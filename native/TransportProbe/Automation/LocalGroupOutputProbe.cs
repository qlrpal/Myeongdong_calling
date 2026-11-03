using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

// All engines run on this PC. This checks real shared-device and WebRTC
// lifecycles; it cannot represent six independent PCs or physical audio latency.
internal static class LocalGroupOutputProbe
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string server, string report, string wave, bool shared, string mode, bool synthetic = false, int? soakSeconds = null)
    {
        if (soakSeconds is not null && soakSeconds is < 30 or > 300) throw new ArgumentOutOfRangeException(nameof(soakSeconds));
        var engines = new List<WebRtcCallEngine>(); var joined = new HashSet<int>();
        var room = "local-output-" + Guid.NewGuid().ToString("N")[..24];
        var events = new List<string>(); var sync = new object();
        var samples = new List<object>(); var resources = new List<object>();
        var watch = Stopwatch.StartNew(); string? error = null;
        using var process = Process.GetCurrentProcess();
        using var feedCancellation = new CancellationTokenSource();
        Task? feeding = null;
        async Task Join(int i)
        { await engines[i].JoinAsync(new Uri(server), room, "local-" + i); joined.Add(i); }
        async Task Leave(int i)
        { await engines[i].LeaveAsync(); joined.Remove(i); }
        var mutedSlots = new HashSet<int>();
        async Task VerifyRemoteMute()
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                var correct = true;
                foreach (var i in joined)
                {
                    var snapshot = engines[i].GetDiagnostics();
                    correct &= snapshot.Peers.Length == joined.Count - 1;
                    foreach (var peer in snapshot.Peers)
                    {
                        var slot = joined.SingleOrDefault(other => other != i && peer.Name == "local-" + other, -1);
                        correct &= slot >= 0 && ParticipantAudioStatus.RemoteMuted(peer.State, peer.Latency) == mutedSlots.Contains(slot);
                    }
                }
                if (correct) { Console.WriteLine("PASS local remote microphone states members=" + joined.Count); return; }
                if (deadline.Elapsed.TotalSeconds > 8) throw new TimeoutException("Remote microphone state propagation failed");
                await Task.Delay(200);
            }
        }
        async Task Ready()
        {
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var ready = true;
                foreach (var i in joined)
                {
                    var d = engines[i].GetDiagnostics();
                    foreach (var p in d.Peers) engines[i].SetPeerVolume(p.Id, .01f);
                    ready &= d.InCall && d.Peers.Length == joined.Count - 1
                        && d.Peers.All(p => p.State == "connected" && p.Audio.PlaybackState == "재생");
                    if (shared) ready &= d.SharedOutput is { } output && output.OutputStreams == (joined.Count > 1 ? 1 : 0)
                        && output.Sources == joined.Count - 1 && output.ActiveSources == joined.Count - 1;
                }
                if (ready) return;
                if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("Local output group failed to converge");
                await Task.Delay(200);
            }
        }
        async Task Window(string phase, int seconds)
        {
            Console.WriteLine($"START local {phase} count={joined.Count} shared={shared}");
            var timer = Stopwatch.StartNew(); process.Refresh(); var cpu = process.TotalProcessorTime;
            var allocation = GC.GetTotalAllocatedBytes(); var pause = GC.GetTotalPauseDuration();
            var first = joined.ToDictionary(i => i, i => engines[i].GetDiagnostics());
            var last = first;
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                last = joined.ToDictionary(i => i, i => engines[i].GetDiagnostics());
                foreach (var (i, d) in last)
                {
                    Require(d.InCall && d.Peers.Length == joined.Count - 1 && d.Peers.All(p => p.State == "connected" && p.Route == "직접 연결"), "Local media membership/path failed");
                    Require(d.Peers.All(p => p.Audio.DeviceError is null && p.Audio.RtpPlayout?.InvalidPackets == 0), "Local output/decoder failed");
                    if (shared) Require(d.SharedOutput is { ClippedSamples: 0 } output && output.OutputStreams == (joined.Count > 1 ? 1 : 0)
                        && output.Sources == joined.Count - 1 && output.ActiveSources == joined.Count - 1, "Shared stream/source count or headroom failed");
                    samples.Add(new { phase, slot = i, at = watch.Elapsed.TotalSeconds, sample = d });
                }
                await Task.Delay(500);
            }
            process.Refresh();
            foreach (var (i, d) in last)
            {
                Require(d.Microphone?.LastFrameAgeMs is < 500, "Local capture stalled");
                foreach (var p in d.Peers)
                {
                    var before = first[i].Peers.Single(old => old.Id == p.Id);
                    Require(p.Audio.DecodedFrames - before.Audio.DecodedFrames > 100, "Peer did not decode during window");
                }
            }
            var totals = last.SelectMany(kv => kv.Value.Peers.Select(peer =>
            {
                var before = first[kv.Key].Peers.Single(p => p.Id == peer.Id).Audio;
                return new { plcMs = peer.Audio.RtpPlayout!.ConcealedMs - before.RtpPlayout!.ConcealedMs,
                    underfillMs = peer.Audio.UnderfillMs - before.UnderfillMs,
                    requestedMs = peer.Audio.RequestedMs - before.RequestedMs,
                    rebuffers = peer.Audio.RtpPlayout.Rebuffers - before.RtpPlayout.Rebuffers,
                    latePackets = peer.Audio.RtpPlayout.LatePackets - before.RtpPlayout.LatePackets,
                    duplicatePackets = peer.Audio.RtpPlayout.DuplicatePackets - before.RtpPlayout.DuplicatePackets,
                    invalidPackets = peer.Audio.RtpPlayout.InvalidPackets - before.RtpPlayout.InvalidPackets };
            })).ToArray();
            // End-of-window aggregation only; no additional per-packet recording.
            var renderCoverage = LocalGroupQuality.RenderCounters(first, last, shared);
            var renderMeasured = renderCoverage.Streams == renderCoverage.MeasuredStreams;
            var quality = new
            {
                plcMs = totals.Sum(t => t.plcMs), underfillMs = totals.Sum(t => t.underfillMs), rebuffers = totals.Sum(t => t.rebuffers),
                latePackets = totals.Sum(t => t.latePackets), duplicatePackets = totals.Sum(t => t.duplicatePackets), invalidPackets = totals.Sum(t => t.invalidPackets),
                renderEmptyEvents = renderCoverage.EmptyEvents,
                renderSafetyFallbacks = renderCoverage.SafetyFallbacks,
                sendDrops = last.Sum(kv => kv.Value.Sender!.DroppedFrames - first[kv.Key].Sender!.DroppedFrames),
                sendExpired = last.Sum(kv => kv.Value.Sender!.ExpiredFrames - first[kv.Key].Sender!.ExpiredFrames),
                captureDiscontinuities = last.Sum(kv => kv.Value.Microphone!.CaptureDiscontinuities - first[kv.Key].Microphone!.CaptureDiscontinuities),
                captureTimestampErrors = last.Sum(kv => kv.Value.Microphone!.CaptureTimestampErrors - first[kv.Key].Microphone!.CaptureTimestampErrors),
                appCaptureDiscardMs = last.Sum(kv => kv.Value.Microphone!.CaptureContinuity!.AppDiscardedMs - first[kv.Key].Microphone!.CaptureContinuity!.AppDiscardedMs),
                clippedSamples = shared ? (long?)last.Sum(kv => kv.Value.SharedOutput!.ClippedSamples - first[kv.Key].SharedOutput!.ClippedSamples) : null
            };
            var observedClear = quality.plcMs == 0 && quality.underfillMs == 0 && quality.rebuffers == 0
                && quality.latePackets == 0 && quality.duplicatePackets == 0 && quality.invalidPackets == 0
                && quality.renderEmptyEvents is null or 0 && quality.renderSafetyFallbacks is null or 0
                && renderCoverage.ObservedEmptyEvents == 0 && renderCoverage.ObservedSafetyFallbacks == 0
                && quality.sendDrops == 0 && quality.sendExpired == 0 && quality.captureDiscontinuities == 0 && quality.captureTimestampErrors == 0
                && quality.appCaptureDiscardMs == 0 && quality.clippedSamples is null or 0;
            bool? qualityCountersClear = !observedClear ? false : renderMeasured ? true : null;
            resources.Add(new { phase, members = joined.Count, seconds = timer.Elapsed.TotalSeconds,
                cpuCores = (process.TotalProcessorTime - cpu).TotalSeconds / timer.Elapsed.TotalSeconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - allocation,
                gcPauseMs = (GC.GetTotalPauseDuration() - pause).TotalMilliseconds,
                handles = process.HandleCount, threads = process.Threads.Count,
                plcMs = totals.Sum(t => t.plcMs), underfillMs = totals.Sum(t => t.underfillMs),
                requestedMs = totals.Sum(t => t.requestedMs), rebuffers = totals.Sum(t => t.rebuffers),
                outputStreams = shared ? last.Values.Sum(d => d.SharedOutput!.OutputStreams) : last.Values.Sum(d => d.Peers.Length),
                qualityCounters = quality, qualityCountersClear, renderCoverage,
                qualityScope = "First/last saved sample deltas; receive-stream/capture-session sums, render counters once per owned output. Null means unmeasured. Clear counters do not prove audible quality or mouth-to-ear latency." });
            Console.WriteLine($"PASS local {phase} functional shared={shared}");
            Console.WriteLine($"QUALITY local {phase} counters={(qualityCountersClear is true ? "clear" : qualityCountersClear is false ? "events" : "unmeasured")} plcMs={quality.plcMs} renderEmpty={quality.renderEmptyEvents} sendDrops={quality.sendDrops}");
        }
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var fixture = SpeechFixture.Load(wave);
                var engine = new WebRtcCallEngine { DirectOnly = true, UseSharedOutput = shared, DiagnosticCaptureTransform = fixture.Fill, DiagnosticSyntheticCapture = synthetic };
                engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency); engine.SetLatencyExperiment(LatencyExperiment.FromName(mode));
                var slot = i; engine.Status += line => { lock (sync) events.Add($"{watch.Elapsed.TotalSeconds:F3}s {slot}: {line}"); };
                engines.Add(engine);
            }
            if (synthetic) feeding = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew(); var blocks = 0L;
                var silence = new byte[480]; // 5ms PCM; the fixture replaces samples before encoding.
                try
                {
                    while (!feedCancellation.IsCancellationRequested)
                    {
                        var target = (long)(clock.Elapsed.TotalSeconds * 200);
                        while (blocks < target)
                        {
                            foreach (var engine in engines) engine.SupplySyntheticCapture(silence);
                            blocks++;
                        }
                        await Task.Delay(5, feedCancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (feedCancellation.IsCancellationRequested) { }
            });
            if (soakSeconds is { } duration)
            {
                for (var i = 0; i < 6; i++) await Join(i);
                await Ready(); await Task.Delay(10000); await Window("six-steady", duration);
            }
            else
            {
            await Join(0); await Join(1); await Ready(); await VerifyRemoteMute(); await Window("pair", 5);
            await Join(2); await Ready(); await Window("three", 8);
            await Join(3); await Join(4); await Join(5); await Ready(); await Window("six", 15);
            engines[0].SetMicrophoneMuted(true); mutedSlots.Add(0); await VerifyRemoteMute();
            engines[2].SetMicrophoneMuted(true); mutedSlots.Add(2); await VerifyRemoteMute();
            engines[0].SetMicrophoneMuted(false); mutedSlots.Remove(0); await VerifyRemoteMute();
            engines[2].SetMicrophoneMuted(false); mutedSlots.Remove(2); await VerifyRemoteMute();
            var oldRestarts = engines[0].GetDiagnostics().Peers.ToDictionary(p => p.Id, p => p.Audio.DeviceRestarts);
            await engines[0].RestartOutputAsync(); await Ready(); await Window("output-restart", 5);
            Require(engines[0].GetDiagnostics().Peers.All(p => p.Audio.DeviceRestarts == oldRestarts[p.Id] + 1), "Output restart count mismatch");
            var muted = engines[0].GetDiagnostics().Peers[0]; engines[0].SetPeerVolume(muted.Id, 0);
            await Window("peer-muted", 3); await VerifyRemoteMute(); engines[0].SetPeerVolume(muted.Id, .01f);
            engines[5].SetMicrophoneMuted(true); mutedSlots.Add(5); await VerifyRemoteMute();
            await Leave(5); await Ready(); await Window("departure", 3);
            await Join(5); await Ready(); await VerifyRemoteMute(); await Window("rejoin", 5);
            engines[5].SetMicrophoneMuted(false); mutedSlots.Remove(5); await VerifyRemoteMute();
            for (var i = 5; i >= 1; i--) await Leave(i);
            await Ready(); await Window("alone", 3);
            await Task.WhenAll(engines.Select(e => e.LeaveAsync())); joined.Clear();
            Require(engines.All(e => !e.GetDiagnostics().InCall && e.GetDiagnostics().Peers.Length == 0
                && (!shared || e.GetDiagnostics().SharedOutput is { OutputStreams: 0, Sources: 0 })), "Group leave leaked sources/outputs");
            await Join(0); await Join(1); await Ready(); await Window("fresh-call", 5);
            }
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            feedCancellation.Cancel();
            if (feeding is not null)
            {
                try { await feeding; }
                catch (Exception failure) { error = (error ?? "") + "\nSynthetic input: " + failure; }
            }
            foreach (var engine in engines)
            {
                try { await engine.DisposeAsync(); }
                catch (Exception failure) { error = (error ?? "") + "\nCleanup: " + failure; }
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        string[] captured; lock (sync) captured = events.ToArray();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { shared, mode, synthetic, soakSeconds, error, resources, samples,
            sdkBuild = SdkBuildIdentity.Read(), opusBackend = OpusBackend.Snapshot(), events = captured,
            notes = "All 2/3/6 engines on ONE PC. Real shared WASAPI OUTPUT and local WebRTC; synthetic speech, peer gain 0.01. When synthetic=true, timer-fed PCM replaces physical capture entirely, including its clock/scheduling cost; it is not a microphone test. Inspect output endpoint: Remote Audio is not a physical-device benchmark. Departure/rejoin is explicit Leave/Join, not an induced network outage. Output restart reopens the same endpoint, not a physical unplug test. No physical mouth-to-ear or independent-PC/game/Internet quality claim. Stream counts are owned application players, not an OS-wide session audit." }, DiagnosticJson.Readable));
        if (error is not null) throw new InvalidOperationException(error);
        Console.WriteLine(soakSeconds is not null ? "PASS: local six-engine functional output and cleanup; inspect per-phase QUALITY counters (not independent PCs)."
            : "PASS: local group functional lifecycle and cleanup; inspect per-phase QUALITY counters.");
    }
}
