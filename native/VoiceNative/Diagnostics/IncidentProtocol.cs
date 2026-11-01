using System.Text.Json;

namespace VoiceNative;

internal sealed record IncidentSignal(string Type, string? Id = null, string? Status = null);
internal sealed record IncidentPeerStatus(string Peer, bool Supported, string? SentId, string? Response,
    int Attempts, string? ReceivedId, string? ReceivedResult, bool Connected = true);
internal static class IncidentProtocol
{
    private const string Prefix = "voice-incident-v1:";
    internal static bool IsMessage(ReadOnlySpan<byte> bytes) => bytes.StartsWith("voice-incident-v1:"u8);
    internal static string Encode(IncidentSignal signal) => Prefix + JsonSerializer.Serialize(signal);
    internal static IncidentSignal? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 384 || !IsMessage(bytes)) return null;
        try
        {
            var result = JsonSerializer.Deserialize<IncidentSignal>(bytes[Prefix.Length..]);
            if (result is null) return null;
            if (result.Type is "hello" or "hello-ack") return result.Id is null && result.Status is null ? result : null;
            if (!Guid.TryParseExact(result.Id, "N", out _)) return null;
            return result.Type switch
            {
                "notice" when result.Status is null => result,
                "ack" when result.Status is "accepted" or "linked" or "busy" or "not-ready" => result,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }
}

// Slow diagnostic/control path only. Never called by capture/render callbacks.
internal sealed class IncidentPeerLink
{
    private readonly object sync = new();
    private bool supported;
    private int helloAttempts, attempts;
    private double helloAt = double.NegativeInfinity, sentAt = double.NegativeInfinity, receivedAt = double.NegativeInfinity;
    private double helloResponseAt = double.NegativeInfinity;
    private string? sentId, response, receivedId, receivedResult;
    internal bool HelloDue(double now)
    {
        lock (sync)
        {
            if (supported || helloAttempts >= 3 || now - helloAt < 1) return false;
            helloAt = now; helloAttempts++; return true;
        }
    }
    internal void SetSupported() { lock (sync) supported = true; }
    internal bool HelloResponseDue(double now)
    { lock (sync) { if (now - helloResponseAt < 1) return false; helloResponseAt = now; return true; } }
    internal bool NoticeDue(string id, double now)
    {
        lock (sync)
        {
            if (!supported) return false;
            if (sentId != id) { sentId = id; response = null; attempts = 0; sentAt = double.NegativeInfinity; }
            if (response is "accepted" or "linked" or "busy" || attempts >= 3 || now - sentAt < 1) return false;
            sentAt = now; attempts++; return true;
        }
    }
    internal void Acknowledge(string id, string status)
    { lock (sync) { if (sentId == id) response = status; } }
    internal bool ReceiveDue(string id, double now)
    {
        lock (sync)
        {
            supported = true;
            if (now - receivedAt < 1) return false;
            receivedAt = now; receivedId = id; return true;
        }
    }
    internal void ReceivedResult(string result) { lock (sync) receivedResult = result; }
    internal void ResetRound()
    { lock (sync) { sentId = response = receivedId = receivedResult = null; attempts = 0; sentAt = receivedAt = double.NegativeInfinity; } }
    internal IncidentPeerStatus Snapshot(string peer)
    { lock (sync) return new(peer, supported, sentId, response, attempts, receivedId, receivedResult); }
}
