using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class PipelineProbe
{
    public static async Task<int> RunAsync(string server, string room, string path, string role, bool measure = true, bool fixedReserveClockTarget = false, bool transientEmptyQueueHold = false, bool freshRestart = false, bool intermediateCapture = true, bool boundedRender = false)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc or laptop.");
        const int seconds = 120;
        var samples = new List<CallDiagnostics>(); var measurements = new List<AcousticLatencyResult>();
        var events = new List<string>(); var sync = new object(); var attempts = 0;
        string? error = null;
        await using var engine = new WebRtcCallEngine { FixedReserveClockTarget = fixedReserveClockTarget, TransientEmptyQueueHold = transientEmptyQueueHold, FreshRestart = freshRestart, IntermediateCapture = intermediateCapture, BoundedRender = boundedRender };
        engine.SetAdaptivePlayoutPreference(false);
        engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
        PipelineTraceSnapshot trace;
        var traceBeganAt = ProbeClock.Now;
        engine.StartPipelineTrace(180);
        try
        {
            await engine.JoinAsync(new Uri(server), room, $"pipeline-{role}");
            var ready = Stopwatch.StartNew();
            while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            {
                if (ready.Elapsed.TotalSeconds > 35) throw new TimeoutException("Peer did not connect in 35s.");
                await Task.Delay(200);
            }
            // Trace from before joining, including the first device pull and RTP packet.
            // Fail rather than silently truncating a slow join at the recorder's 180s cap.
            if (ProbeClock.Now - traceBeganAt > 55) throw new TimeoutException("Join exceeded startup trace budget.");
            Console.WriteLine($"{DateTimeOffset.Now:O} pipeline started role={role}");
            var clock = Stopwatch.StartNew(); var nextLog = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (measure && attempts < 4 && clock.Elapsed.TotalSeconds >= (role == "pc" ? 12 : 24) + attempts * 30)
                {
                    attempts++;
                    try
                    {
                        await engine.MeasureLoopbackLatencyAsync();
                        if (engine.GetDiagnostics().Acoustic is { } result) measurements.Add(result);
                    }
                    catch (Exception ex) { lock (sync) events.Add("측정 제외: " + ex.Message); }
                }
                var snapshot = engine.GetDiagnostics(); samples.Add(snapshot);
                if (snapshot.Peers.Length != 1 || snapshot.Peers[0].State != "connected") throw new InvalidOperationException("Peer disconnected during pipeline trace.");
                if (clock.Elapsed.TotalSeconds >= nextLog)
                {
                    var p = snapshot.Peers[0];
                    Console.WriteLine($"{DateTimeOffset.Now:O} elapsed={clock.Elapsed.TotalSeconds:F0}s rx={p.RxPackets} queue={p.Audio.QueueMs:F1}ms PLC={p.Audio.RtpPlayout?.ConcealedMs:F0}ms");
                    nextLog += 30;
                }
                await Task.Delay(1000);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            trace = engine.StopPipelineTrace();
            // Allow the peer's last 1s diagnostic tick to finish before closing the call.
            if (error is null) await Task.Delay(2000);
            await engine.LeaveAsync();
        }
        if (error is null && (trace.Sends.Length < 6000 || trace.Receives.Length < 6000 || trace.EvictedRecords > 0))
            error = "Incomplete or truncated trace; do not infer network loss from unmatched records.";
        string[] captured; lock (sync) captured = events.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
            generatedAt = DateTimeOffset.Now,
            role,
            loopbackProbes = measure,
            fixedReserveClockTarget,
            transientEmptyQueueHold,
            freshRestart,
            intermediateCapture,
            boundedRender,
            traceIncludesStartup = true,
            traceBeganAt,
            seconds,
            error,
            attempts,
            measurements,
            samples,
            trace,
            events = captured,
            notes = "고정20ms·소비율 보정 유지. 캡처/송신/수신/재생의 제한된 시각 메타데이터만 저장. 음성·페이로드·토큰 없음. 수신 콜백은 복호화 이후이며 물리 입→귀 실측 아님."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"{DateTimeOffset.Now:O} complete sends={trace.Sends.Length} receives={trace.Receives.Length} reads={trace.Reads.Length} recorderMax={trace.MaxRecorderMs:F3}ms error={error is not null}");
        return error is null ? 0 : 1;
    }
}
