using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VoiceNative;

internal static class OpusAbProbe
{
    // Six 75s rounds: change one sender only, and reverse direction after A/B/A.
    public static async Task<int> RunAsync(string server, string roomPrefix, string reportPath, string role)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc or laptop.");
        var rounds = new List<object>(); var failures = 0;
        for (var round = 0; round < 6; round++)
        {
            var sender = round < 3 ? "pc" : "laptop";
            var condition = round % 3 == 1 ? "B" : round % 3 == 0 ? "A1" : "A2";
            var profile = role == sender && condition == "B" ? OpusProfile.LowDelay64 : OpusProfile.Voip64;
            var events = new List<string>(); var samples = new List<CallDiagnostics>();
            var measurements = new List<AcousticLatencyResult>(); var sync = new object(); var attempts = 0;
            string? error = null;
            await using var engine = new WebRtcCallEngine();
            engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
            engine.SetLowLatencyPreference(false); engine.SetOpusProfile(profile);
            try
            {
                await engine.JoinAsync(new Uri(server), $"{roomPrefix}-{round}", $"ab-{role}-{condition}");
                var ready = Stopwatch.StartNew();
                while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
                {
                    if (ready.Elapsed.TotalSeconds > 35) throw new TimeoutException("Peer did not connect in 35s.");
                    await Task.Delay(200);
                }
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < 75)
                {
                    if (role == sender && attempts < 5 && clock.Elapsed.TotalSeconds >= 15 + attempts * 10)
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
            string[] capturedEvents; lock (sync) capturedEvents = events.ToArray();
            rounds.Add(new { round, sender, condition, role, profile = profile.ToString(), attempts, measurements, error, samples, events = capturedEvents });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
                generatedAt = DateTimeOffset.Now,
                role,
                completedRounds = rounds.Count,
                failures,
                notes = "송신자만 VOIP64→LowDelay64→VOIP64 변경. WASAPI 표준·출력40ms 고정. 조건별5회 디지털 출력 루프백. 음성 저장 없음. 실제 입→귀 지연 측정 아님.",
                rounds
            }, VoiceNative.DiagnosticJson.Readable));
            await Task.Delay(3000);
        }
        return failures == 0 ? 0 : 1;
    }
}
