using System.Diagnostics;

namespace VoiceNative;

public sealed record AudioDiagnostics(double? QueueMs, long QueueResets, double? LevelDbFs, double? ProcessingMs,
    long? Underruns = null, double? DecodedMs = null, long? DecodedFrames = null,
    double? ConsumedMs = null, double? RequestedMs = null,
    double? DeviceLatencyMs = null, double? DeviceAverageLatencyMs = null, string? Device = null, string? DeviceFormat = null,
    double? ProcessingP95Ms = null, double? ProcessingMaxMs = null, double? LastFrameAgeMs = null,
    string? PlaybackState = null, string? DeviceError = null, long DeviceRestarts = 0,
    double? CaptureGapP95Ms = null, double? CaptureGapMaxMs = null,
    double? CaptureWorkP95Ms = null, double? CaptureWorkMaxMs = null,
    long CaptureDiscontinuities = 0, long CaptureTimestampErrors = 0,
    double? UnderfillMs = null, double? MaxUnderfillMs = null);
public sealed record PeerDiagnostics(string Id, string Name, string State, string IceState, string Route,
    string? LocalEndpoint, string? RemoteEndpoint, long TxFrames, long RxPackets,
    double? TxPayloadKbps, double? RxPayloadKbps, double? ReceiveJitterMs,
    double? RemoteLossPercent, double? RemoteReportAgeSeconds, AudioDiagnostics Audio, NetworkPrecision? Precision = null);
public sealed record CallDiagnostics(DateTimeOffset Timestamp, bool InCall, AudioDiagnostics? Microphone, PeerDiagnostics[] Peers, double MonotonicSeconds = 0, SendDiagnostics? Sender = null);

internal sealed class PeerMetrics
{
    private readonly object sync = new();
    private long txBytes, rxBytes, txFrames, rxPackets;
    private long previousTx, previousRx;
    private double? previousTime;
    private uint? previousTimestamp, source;
    private double previousArrival, jitter;
    private bool hasJitter;
    private double? remoteLoss, reportTime;
    public void Sent(int length) { lock (sync) { txBytes += length; txFrames++; } }
    public void Received(uint ssrc, uint timestamp, int length, double? arrivalSeconds = null)
    {
        lock (sync)
        {
            var arrival = arrivalSeconds ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            rxBytes += length; rxPackets++;
            if (source != ssrc) { source = ssrc; previousTimestamp = null; jitter = 0; hasJitter = false; }
            if (previousTimestamp is { } previous)
            {
                // RFC 3550 interarrival jitter; Opus RTP clock is always 48kHz.
                var timestampDelta = unchecked((int)(timestamp - previous));
                var differenceMs = Math.Abs((arrival - previousArrival) * 1000 - timestampDelta / 48.0);
                jitter += (differenceMs - jitter) / 16;
                hasJitter = true;
            }
            previousTimestamp = timestamp; previousArrival = arrival;
        }
    }
    public void ReportLoss(byte fractionLost)
    {
        lock (sync) { remoteLoss = fractionLost * 100.0 / 256; reportTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; }
    }
    public (long Tx, long Rx, double? TxKbps, double? RxKbps, double? Jitter, double? Loss, double? Age) Sample(double? nowSeconds = null)
    {
        lock (sync)
        {
            var now = nowSeconds ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var elapsed = previousTime is { } before ? now - before : 0;
            double? txRate = elapsed > 0 ? (txBytes - previousTx) * 8.0 / elapsed / 1000 : null;
            double? rxRate = elapsed > 0 ? (rxBytes - previousRx) * 8.0 / elapsed / 1000 : null;
            previousTime = now; previousTx = txBytes; previousRx = rxBytes;
            return (txFrames, rxPackets, txRate, rxRate, hasJitter ? jitter : null,
                remoteLoss, reportTime is { } at ? Math.Max(0, now - at) : null);
        }
    }
}
