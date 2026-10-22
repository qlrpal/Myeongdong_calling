using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class ExtremeSoak
{
    public static async Task RunAsync(string server, string room, string report, string role, string wave, string mode, int seconds, DateTimeOffset? scheduled = null, string route = "relay", string? mediaAddress = null)
    {
        var options = mode.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Deserialize<LatencyExperiment>(await File.ReadAllTextAsync(mode), DiagnosticJson.Readable) ?? throw new ArgumentException("Empty experiment config")
            : LatencyExperiment.FromName(mode);
        options?.Validate();
        if (seconds is < 10 or > 600) throw new ArgumentException("Duration must be 10–600s.");
        var traceSeconds = Math.Min(seconds, Math.Min(30, (int)(18000 * (options?.FrameMs ?? 10) / 1000)));
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc/laptop");
        if (route is not ("direct" or "relay")) throw new ArgumentException("Route must be direct/relay");
        var samples = new List<CallDiagnostics>(); var events = new List<string>(); var sync = new object();
        var fixture = SpeechFixture.Load(wave);
        await using var engine = new WebRtcCallEngine { ForceRelay = route == "relay", DirectOnly = route == "direct", IntermediateCapture = options?.MinimumCapture != true, DiagnosticCaptureTransform = fixture.Fill };
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency); engine.SetLatencyExperiment(options);
        engine.SetMediaInterface(mediaAddress);
        engine.Status += line => { lock (sync) events.Add(line); };
        PipelineTraceSnapshot? trace = null; string? error = null;
        double? processCoreUsage = null;
        try
        {
            await engine.JoinAsync(new Uri(server), room, "extreme-" + role);
            var connect = Stopwatch.StartNew();
            while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            { if (connect.Elapsed.TotalSeconds > 40) throw new TimeoutException("Extreme peer not connected"); await Task.Delay(200); }
            await Task.Delay(2000);
            if (scheduled is { } at)
            {
                var remaining = at - DateTimeOffset.UtcNow;
                if (remaining < TimeSpan.Zero || remaining > TimeSpan.FromSeconds(90)) throw new InvalidOperationException("Scheduled experiment missed or too distant");
                await Task.Delay(remaining);
            }
            using var process = Process.GetCurrentProcess();
            var cpuStart = process.TotalProcessorTime;
            Console.WriteLine($"{DateTimeOffset.Now:O} extreme {role} {mode} measure {seconds}s");
            var clock = Stopwatch.StartNew();
            var traceStarted = false; var nextProgress = 30;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (!traceStarted && clock.Elapsed.TotalSeconds >= seconds - traceSeconds)
                { engine.StartPipelineTrace(traceSeconds + 5); traceStarted = true; }
                var sample = engine.GetDiagnostics(); samples.Add(sample);
                if (sample.Peers.Length != 1 || sample.Peers[0].State != "connected") throw new InvalidOperationException("Experiment peer disconnected");
                if (sample.Peers[0].Audio.DeviceError is not null) throw new InvalidOperationException("Experiment output failed");
                if (sample.Microphone?.LastFrameAgeMs is null or > 500) throw new InvalidOperationException("Experiment capture stalled");
                if (clock.Elapsed.TotalSeconds >= nextProgress)
                {
                    Console.WriteLine($"PROGRESS {role} {route} {mode} {clock.Elapsed.TotalSeconds:F0}/{seconds}s queue={sample.Peers[0].Audio.QueueMs:F2}ms");
                    nextProgress += 30;
                }
                await Task.Delay(500);
            }
            trace = engine.StopPipelineTrace();
            processCoreUsage = (process.TotalProcessorTime - cpuStart).TotalSeconds / clock.Elapsed.TotalSeconds;
            var final = samples[^1].Peers[0];
            if (trace.EvictedRecords != 0 || trace.Sends.Length < 500 || trace.Receives.Length < 500
                || final.Audio.RtpPlayout is not { DecodedPackets: > 100, InvalidPackets: 0 }) throw new InvalidOperationException("Incomplete or invalid media experiment");
            // Avoid terminating the other host's slightly offset measurement window.
            await Task.Delay(2000);
        }
        catch (Exception ex) { error = ex.ToString(); trace ??= engine.StopPipelineTrace(); }
        finally { await engine.LeaveAsync(); }
        string[] captured; lock (sync) captured = events.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { role, mode, options, seconds, route, mediaAddress, processId = Environment.ProcessId, traceSeconds,
            engineBuild = typeof(WebRtcCallEngine).Assembly.ManifestModule.ModuleVersionId,
            runtime = new { version = Environment.Version.ToString(), tieredCompilationEnvironment = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") }, error, processCoreUsage, samples, trace, events = captured,
            notes = "Synthetic speech over real shared input/render and the explicitly selected direct/relay route. Frame size is not device period. Minimal output retains its target on device starvation; count EmptyEvents. PCM concealment is silence, not Opus PLC. No physical or subjective latency claim. Samples cover the full trial; bounded packet trace covers the final traceSeconds, excluding initial queue history. Baseline uses the product capture-period selection." }, DiagnosticJson.Readable));
        if (error is not null) throw new InvalidOperationException(error);
        var first = samples[0]; var last = samples[^1];
        var before = first.Peers[0].Audio; var audio = last.Peers[0].Audio;
        Console.WriteLine($"FUNCTIONAL PASS {role} {mode}: snapshotWindow={(last.Timestamp - first.Timestamp).TotalSeconds:F2}s queue={audio.QueueMs:F2}ms underfillDelta={audio.UnderfillMs - before.UnderfillMs:F2}ms concealDelta={audio.RtpPlayout!.ConcealedMs - before.RtpPlayout!.ConcealedMs:F2}ms sourceDropsDelta={last.Sender?.DroppedFrames - first.Sender?.DroppedFrames} CPUcores={processCoreUsage:F2}; not a continuity/latency quality verdict.");
    }
}
