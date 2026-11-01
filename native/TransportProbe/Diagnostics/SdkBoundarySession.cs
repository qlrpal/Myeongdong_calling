using System.Reflection;
using System.Text.Json;
using SIPSorcery.Net;

namespace VoiceNative;

// Diagnostic SDK is loaded only from an isolated probe folder. Normal app has no hooks.
internal sealed class SdkBoundarySession
{
    private readonly Type? probe;
    private bool stopped;
    internal bool Enabled { get; }
    internal SdkBoundarySession(bool enabled)
    {
        Enabled = enabled;
        if (!enabled) return;
        probe = typeof(UdpReceiver).Assembly.GetType("SIPSorcery.Net.MyeongdongBoundaryProbe")
            ?? throw new InvalidOperationException("SDK boundary recording requires the isolated diagnostic SDK.");
        Invoke("Start", 120, 64000);
    }
    private object? Invoke(string name, params object[] args) =>
        probe!.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);
    internal void Stop()
    {
        if (!Enabled || stopped) return;
        Invoke("Stop"); stopped = true;
    }
    internal object Snapshot()
    {
        Stop();
        if (!Enabled) return new { enabled = false };
        using var json = JsonDocument.Parse((string)Invoke("SnapshotJson")!);
        return new { enabled = true, data = json.RootElement.Clone() };
    }
    internal static async Task CheckAsync(string report)
    {
        var type = typeof(UdpReceiver).Assembly.GetType("SIPSorcery.Net.MyeongdongBoundaryProbe")
            ?? throw new InvalidOperationException("Diagnostic SDK missing");
        type.GetMethod("SelfTest")!.Invoke(null, null);
        var session = new SdkBoundarySession(true);
        try { await WebRtcProbe.RunAsync(["--ten-ms-rtcp-integration"]); }
        finally { session.Stop(); }
        var snapshot = session.Snapshot();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot));
        var records = json.RootElement.GetProperty("data").GetProperty("Records");
        var stages = records.EnumerateArray().Select(r => r.GetProperty("Stage").GetString()).ToHashSet();
        foreach (var stage in new[] { "socket-send-post", "socket-send-end-observed", "udp-receive-end", "srtp-unprotect-enter", "srtp-unprotect-return" })
            if (!stages.Contains(stage)) throw new InvalidOperationException("Missing SDK boundary: " + stage);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(snapshot, DiagnosticJson.Readable));
        SrtpProtectionChecks.Run(Path.ChangeExtension(report, ".srtp.json"));
        Console.WriteLine("PASS diagnostic SDK parser/ring, real WebRTC boundaries and SRTP regression");
    }
}
