using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class RtpSoakProbe
{
    public static async Task<int> RunAsync(string server, string room, string reportPath, string role, int seconds, PlayoutPolicy policy = PlayoutPolicy.Compatible, bool measure = true)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc or laptop.");
        if (seconds is < 60 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        var events = new List<string>(); var samples = new List<CallDiagnostics>();
        var measurements = new List<AcousticLatencyResult>(); var sync = new object();
        string? error = null; var attempts = 0;
        await using var engine = new WebRtcCallEngine();
        engine.SetPlayoutPolicy(policy);
        engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
        try
        {
            await engine.JoinAsync(new Uri(server), room, $"soak-{role}");
            var ready = Stopwatch.StartNew();
            while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            {
                if (ready.Elapsed.TotalSeconds > 35) throw new TimeoutException("Peer did not connect in 35s.");
                await Task.Delay(200);
            }
            Console.WriteLine($"{DateTimeOffset.Now:O} connected role={role} seconds={seconds}");
            var clock = Stopwatch.StartNew(); var nextLog = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (measure && attempts < seconds / 60 && clock.Elapsed.TotalSeconds >= (role == "pc" ? 15 : 40) + attempts * 60)
                {
                    attempts++;
                    try
                    {
                        await engine.MeasureLoopbackLatencyAsync();
                        if (engine.GetDiagnostics().Acoustic is { } result) measurements.Add(result);
                    }
                    catch (Exception ex) { lock (sync) events.Add("측정 실패: " + ex.Message); }
                }
                var snapshot = engine.GetDiagnostics(); samples.Add(snapshot);
                if (snapshot.Peers.Length != 1 || snapshot.Peers[0].State != "connected") throw new InvalidOperationException("Peer disconnected during soak.");
                if (clock.Elapsed.TotalSeconds > 5 && (snapshot.Peers[0].RxPackets == 0 || snapshot.Peers[0].Precision?.LastPacketAgeSeconds is > 5))
                    throw new InvalidOperationException("Received media stalled for more than five seconds.");
                if (clock.Elapsed.TotalSeconds >= nextLog)
                {
                    var audio = snapshot.Peers[0].Audio;
                    Console.WriteLine($"{DateTimeOffset.Now:O} elapsed={clock.Elapsed.TotalSeconds:F0}s queue={audio.QueueMs:F1}ms PLC={audio.RtpPlayout?.ConcealedMs:F0}ms underfill={audio.UnderfillMs:F1}ms rebuffer={audio.RtpPlayout?.Rebuffers} correction={audio.RtpPlayout?.RateCorrectionPpm:F0}ppm readMax={audio.RtpPlayout?.ReadRequestMaxMs:F2}ms");
                    nextLog += 30;
                }
                await Task.Delay(1000);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { if (error is null) await Task.Delay(2000); await engine.LeaveAsync(); }
        string[] captured; lock (sync) captured = events.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
            generatedAt = DateTimeOffset.Now,
            role,
            seconds,
            policy = policy.ToString(),
            loopbackProbes = measure,
            error,
            attempts,
            measurements,
            samples,
            events = captured,
            notes = "RTP 연속 통화. 기본 코덱·WASAPI 저지연 요청·복귀40ms. 루프백 신호 사용 여부는 loopbackProbes 참조. 물리 입→귀 실측 아님. 원음 저장 없음."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"{DateTimeOffset.Now:O} complete measurements={measurements.Count}/{attempts} error={error is not null}");
        return error is null ? 0 : 1;
    }
}
