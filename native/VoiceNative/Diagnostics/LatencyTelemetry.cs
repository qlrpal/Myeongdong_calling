using System.Diagnostics;
using System.Text.Json;
namespace VoiceNative;

public sealed record LatencyTelemetry(int Version, double? InputApiMs, double? EncodeMs, double? SendWaitMs,
    double? PlaybackQueueMs, double? OutputApiMs, double? DecodeMs, string? OutputState,
    double? InputFrameAgeMs = null, double? OutputFrameAgeMs = null, double FrameMs = 20,
    ReceiverFrameFeedback? FrameFeedback = null);
public sealed record LatencyView(double? OutboundModelMs, double? InboundModelMs, double? RemoteAgeSeconds,
    LatencyTelemetry? Remote, string Explanation);

internal sealed class TelemetryState
{
    private readonly object sync = new();
    private LatencyTelemetry? remote;
    private double receivedAt;
    public bool Receive(byte[] bytes)
    {
        if (bytes.Length > 2048) return false;
        try
        {
            var value = JsonSerializer.Deserialize<LatencyTelemetry>(bytes);
            if (value is null || value.Version != 1 || value.FrameMs is not (1 or 2.5 or 5 or 10 or 20)) return false;
            foreach (var number in new[] { value.InputApiMs, value.EncodeMs, value.SendWaitMs, value.PlaybackQueueMs, value.OutputApiMs, value.DecodeMs })
                if (number is { } n && (!double.IsFinite(n) || n < 0 || n > 10000)) return false;
            foreach (var age in new[] { value.InputFrameAgeMs, value.OutputFrameAgeMs }) if (age is { } n && (!double.IsFinite(n) || n < 0)) return false;
            if (value.FrameFeedback is { } feedback && (feedback.ReceivedPackets < 0 || feedback.LatePackets < 0 || feedback.SourceChanges < 0
                || !double.IsFinite(feedback.DecodedMs) || !double.IsFinite(feedback.ConcealedMs)
                || feedback.DecodedMs is < 0 or > 1e12 || feedback.ConcealedMs is < 0 or > 1e12)) return false;
            lock (sync) { remote = value; receivedAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
            return true;
        }
        catch (JsonException) { return false; }
    }
    public (LatencyTelemetry? Remote, double? Age) Snapshot()
    {
        lock (sync) return (remote, remote is null ? null : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency - receivedAt);
    }
    public static double? Model(double? input, double? encode, double? sendWait, double? rtt, double? queue, double? output, double? decode, double frameMs = 20)
    {
        if (new[] { input, encode, sendWait, rtt, queue, output, decode }.Any(x => x is null)) return null;
        // Explicit model: the sender frame duration and a symmetric network RTT/2.
        // Device API values may overlap buffering; this is NOT mouth-to-ear measurement.
        return input + encode + frameMs + sendWait + rtt / 2 + queue + output + decode;
    }
}
