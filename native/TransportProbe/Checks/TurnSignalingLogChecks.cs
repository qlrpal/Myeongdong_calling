using System.Text.Json;
using Microsoft.Extensions.Logging;

internal static class TurnSignalingLogChecks
{
    internal static void Run()
    {
        using var capture = new TurnSignalingLog();
        capture.Phase = "test-recovery";
        var logger = capture.CreateLogger("SIPSorcery.Net.IceServer");
        var state = new KeyValuePair<string, object?>[] {
            new("{OriginalFormat}", "Allocate error {ErrorCode}"), new("ErrorCode",437),
            new("RemoteEndPoint","192.0.2.1:49152"), new("RemoteCandidate","udp:192.0.2.1:49152 (host)"),
            new("credential","DO-NOT-STORE-SECRET"), new("Username","DO-NOT-STORE-USER"),
            new("Nonce","DO-NOT-STORE-NONCE"), new("Message","DO-NOT-STORE-SDP") };
        logger.Log(LogLevel.Warning, new EventId(0), state,
            new InvalidOperationException("DO-NOT-STORE-EXCEPTION"),
            (_,_) => throw new InvalidOperationException("Formatting SDK secrets must never occur"));
        var json = JsonSerializer.Serialize(capture.Snapshot());
        if (!json.Contains("437",StringComparison.Ordinal) || !json.Contains("test-recovery",StringComparison.Ordinal) ||
            !json.Contains("192.0.2.1:49152",StringComparison.Ordinal) ||
            json.Contains("DO-NOT-STORE",StringComparison.Ordinal))
            throw new InvalidOperationException("SDK ICE log metadata privacy contract failed");
        Console.WriteLine("PASS: structured TURN log preserves error codes and excludes SDK credentials, messages and exceptions");
    }
}
