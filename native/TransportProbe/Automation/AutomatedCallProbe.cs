using System.Text.Json;
using System.Text.Encodings.Web;

namespace VoiceNative;

internal static class AutomatedCallProbe
{
    public static async Task<int> RunAsync(string server, string room, string name, string reportPath, bool receiver)
    {
        var samples = new List<CallDiagnostics>();
        var events = new List<string>();
        var sync = new object();
        var success = false;
        await using var engine = new WebRtcCallEngine();
        engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
        try
        {
            await engine.JoinAsync(new Uri(server), room, name);
            for (var second = 0; second < (receiver ? 210 : 170); second++)
            {
                if (receiver && second == 85) await engine.SetOutputLatencyAsync(20);
                // Only the sender emits the explicit acoustic signal, with the receiver's microphone armed remotely.
                if (!receiver && second is 45 or 50 or 55 or 60 or 65 or 125 or 130 or 135 or 140 or 145)
                {
                    try { await engine.MeasureLoopbackLatencyAsync(); }
                    catch (Exception ex) { lock (sync) events.Add("음향 측정 실패: " + ex.Message); }
                }
                samples.Add(engine.GetDiagnostics());
                await Task.Delay(1000);
            }
            success = samples.Any(sample => sample.Peers.Any(peer => peer.TxFrames >= 100 && peer.RxPackets >= 100));
        }
        catch (Exception ex) { lock (sync) events.Add("자동 검사 오류: " + ex); }
        finally
        {
            await engine.LeaveAsync();
            string[] capturedEvents;
            lock (sync) capturedEvents = events.ToArray();
            var report = new
            {
                applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
                generatedAt = DateTimeOffset.Now,
                automated = true,
                success,
                name,
                receiver,
                notes = "수신 측 40→20ms 출력 전환. 원음 저장 없음. 측정 실패는 미측정이며 수집 지점은 실제 출력 장치 WASAPI 루프백이며 물리 입→귀 실측 아님.",
                samples,
                events = capturedEvents
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report,
                VoiceNative.DiagnosticJson.Readable));
        }
        return success ? 0 : 1;
    }
}
