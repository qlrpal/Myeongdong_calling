using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

// Synthetic markers enter after capture framing. This isolates the downstream path;
// it must not be reported as a physical microphone or Stereo Mix measurement.
internal static class StageOptimizationProbe
{
    public static async Task<int> RunAsync(string server, string room, string output, string role, int reserve)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Invalid role.");
        if (reserve is not (10 or 15 or 20)) throw new ArgumentException("Reserve must be 10, 15 or 20ms.");
        var samples = new List<CallDiagnostics>();
        var markers = new List<AcousticLatencyResult>();
        var events = new List<string>();
        var sync = new object();
        string? error = null;
        var attempts = 0;
        await using var engine = new WebRtcCallEngine
        {
            ForceRelay = true, IntermediateCapture = false, DiagnosticReserveMs = reserve
        };
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
        engine.SetOpusProfile(OpusProfile.LowDelay64);
        engine.Status += message => { lock (sync) events.Add(message); };
        engine.StartPipelineTrace(150);
        PipelineTraceSnapshot trace;
        try
        {
            await engine.JoinAsync(new Uri(server), room, "stage-" + role);
            var connection = Stopwatch.StartNew();
            while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            {
                if (connection.Elapsed.TotalSeconds > 40) throw new TimeoutException("Peer did not connect.");
                await Task.Delay(100);
            }
            Console.WriteLine($"connected {role} reserve={reserve}");
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 80)
            {
                if (attempts < 6 && clock.Elapsed.TotalSeconds >= (role == "pc" ? 8 : 13) + attempts * 10)
                {
                    attempts++;
                    try
                    {
                        await engine.MeasureLoopbackLatencyAsync();
                        if (engine.GetDiagnostics().Acoustic is { } marker)
                        {
                            markers.Add(marker);
                            Console.WriteLine($"marker {attempts}: {marker.MinimumMs:F1}..{marker.MaximumMs:F1}ms");
                        }
                    }
                    catch (Exception ex) { lock (sync) events.Add("Marker excluded: " + ex.Message); }
                }
                var snapshot = engine.GetDiagnostics();
                samples.Add(snapshot);
                if (snapshot.Peers.Length != 1 || snapshot.Peers[0].State != "connected"
                    || snapshot.Peers[0].Route != "TURN 중계")
                    throw new InvalidOperationException("Incomplete TURN media path.");
                await Task.Delay(500);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            trace = engine.StopPipelineTrace();
            if (error is null) await Task.Delay(2000);
            await engine.LeaveAsync();
        }
        if (error is null && (markers.Count < 4 || trace.EvictedRecords != 0
            || trace.Sends.Length < 6000 || trace.Receives.Length < 6000))
            error = "Insufficient markers or incomplete trace; do not infer latency.";
        string[] captured;
        lock (sync) captured = events.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            applicationVersion = typeof(WebRtcCallEngine).Assembly.GetName().Version?.ToString(),
            generatedAt = DateTimeOffset.Now, seconds = 80,
            role, reserveMs = reserve, error, attempts, measurements = markers, samples, trace, events = captured,
            notes = "Synthetic post-capture-frame injection, same-packet hash verified, forced TURN, standard 10ms shared input. Stage QPC and loopback detector are not physical mouth-to-ear. No raw audio saved. Product reserve unchanged."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"complete markers={markers.Count}/{attempts} error={error is not null}");
        return error is null ? 0 : 1;
    }
}
