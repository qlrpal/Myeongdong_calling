using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class RtpAbProbe
{
    // Same codec and devices in all three rounds; change only the receiver path.
    public static async Task<int> RunAsync(string server, string prefix, string reportPath, string role, bool compareAdaptive = false)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc or laptop.");
        var rounds = new List<object>(); var failures = 0;
        for (var round = 0; round < 3; round++)
        {
            var condition = compareAdaptive ? round == 1 ? "ADAPTIVE" : "FIXED" : round == 1 ? "RTP" : "PCM";
            var events = new List<string>(); var samples = new List<CallDiagnostics>();
            var measurements = new List<AcousticLatencyResult>(); var sync = new object(); var attempts = 0;
            string? error = null;
            await using var engine = new WebRtcCallEngine();
            engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
            engine.SetRtpPlayoutPreference(compareAdaptive || round == 1);
            if (compareAdaptive) engine.SetAdaptivePlayoutPreference(round == 1);
            try
            {
                Console.WriteLine($"{DateTimeOffset.Now:O} round={round} mode={condition} joining");
                await engine.JoinAsync(new Uri(server), $"{prefix}-{round}", $"rtp-ab-{role}-{condition}");
                var ready = Stopwatch.StartNew();
                while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
                {
                    if (ready.Elapsed.TotalSeconds > 35) throw new TimeoutException("Peer did not connect in 35s.");
                    await Task.Delay(200);
                }
                Console.WriteLine($"{DateTimeOffset.Now:O} round={round} connected");
                var clock = Stopwatch.StartNew();
                var firstTrial = role == "pc" ? 12 : 44;
                while (clock.Elapsed.TotalSeconds < 75)
                {
                    if (attempts < 3 && clock.Elapsed.TotalSeconds >= firstTrial + attempts * 9)
                    {
                        attempts++;
                        try
                        {
                            await engine.MeasureLoopbackLatencyAsync();
                            if (engine.GetDiagnostics().Acoustic is { } result && measurements.All(m => m.Timestamp != result.Timestamp)) measurements.Add(result);
                        }
                        catch (Exception ex) { lock (sync) events.Add("측정 실패: " + ex.Message); }
                    }
                    samples.Add(engine.GetDiagnostics()); await Task.Delay(1000);
                }
                var last = samples.Last();
                if (last.Peers.Length != 1 || last.Peers[0].RxPackets < 500 || last.Peers[0].TxFrames < 500)
                    throw new InvalidOperationException("Insufficient bidirectional media.");
            }
            catch (Exception ex) { error = ex.ToString(); failures++; }
            finally { await engine.LeaveAsync(); }
            string[] captured; lock (sync) captured = events.ToArray();
            rounds.Add(new { round, condition, role, attempts, measurements, error, samples, events = captured });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
                generatedAt = DateTimeOffset.Now,
                role,
                completedRounds = rounds.Count,
                failures,
                notes = (compareAdaptive ? "고정→적응→고정, RTP/소비율 보정은 유지. " : "PCM→RTP→PCM. ") + "각75초. 기본 코덱·WASAPI 저지연 요청·복귀40ms 고정. 방향별3회 디지털 루프백. 물리 입→귀 실측 아님. 원음 저장 없음.",
                rounds
            }, DiagnosticJson.Readable));
            Console.WriteLine($"{DateTimeOffset.Now:O} round={round} complete measurements={measurements.Count}/3 error={error is not null}");
            await Task.Delay(3000);
        }
        return failures == 0 ? 0 : 1;
    }
}
