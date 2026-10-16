using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace VoiceNative;

public sealed record AudioFrameStages(string Hash, double? CaptureAt = null, double? EncodeStart = null,
    double? EncodeEnd = null, double? SentAt = null, double? ReceivedAt = null, double? DecodeStart = null,
    double? DecodeEnd = null, double? QueuedAt = null, double? OutputSubmittedAt = null, int Receipts = 0,
    bool Discarded = false, double? RtpNotificationAt = null);
public sealed record AudioStageBreakdown(double? CaptureToEncodeMs, double? EncodeMs, double? EncodeToSendMs,
    double? NetworkMinimumMs, double? NetworkMaximumMs, double? ReceiveToDecodeMs, double? DecodeMs,
    double? PcmQueueMs, double? OutputSubmissionToDetectionMs);

// Bounded metadata only; no encoded or raw audio is retained.
internal sealed class AudioFrameTimeline
{
    private readonly ConcurrentDictionary<string, AudioFrameStages> frames = new();
    private readonly ConcurrentQueue<string> order = new();
    private double enabledUntil;
    public bool Enabled => ProbeClock.Now < Volatile.Read(ref enabledUntil);
    public void EnableFor(double seconds)
    {
        Volatile.Write(ref enabledUntil, 0);
        frames.Clear(); while (order.TryDequeue(out _)) { }
        Volatile.Write(ref enabledUntil, ProbeClock.Now + seconds);
    }
    public AudioFrameTimeline()
    {
        var hash = Hash(new byte[1920]); Update(hash, old => old with { SentAt = ProbeClock.Now }); frames.Clear(); while (order.TryDequeue(out _)) { }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public void Update(string hash, Func<AudioFrameStages, AudioFrameStages> update)
    {
        if (frames.TryAdd(hash, new(hash))) order.Enqueue(hash);
        frames.AddOrUpdate(hash, h => update(new(h)), (_, old) => update(old));
        while (frames.Count > 512 && order.TryDequeue(out var expired)) frames.TryRemove(expired, out _);
    }
    public AudioFrameStages? Find(string hash) => frames.TryGetValue(hash, out var frame) ? frame : null;
    public static bool IsDetectionCoherent(double submittedAt, double detectedAt, bool loopback)
        => double.IsFinite(submittedAt) && double.IsFinite(detectedAt) && detectedAt >= submittedAt && (!loopback || detectedAt - submittedAt <= 0.6);
    public static bool HasVerifiedPlayout(AudioFrameStages? source, AudioFrameStages? target) =>
        source is { CaptureAt: not null, SentAt: not null }
        && target is { Receipts: 1, Discarded: false, DecodeStart: not null, DecodeEnd: not null, QueuedAt: not null, OutputSubmittedAt: not null }
        && source.Hash == target.Hash;
    public static AudioStageBreakdown Breakdown(AudioFrameStages? source, AudioFrameStages? target,
        double lowOffset, double highOffset, double detectedAt)
    {
        static double? Difference(double? end, double? start) => end is { } e && start is { } s ? (e - s) * 1000 : null;
        var network = Difference(target?.ReceivedAt, source?.SentAt);
        return new(Difference(source?.EncodeStart, source?.CaptureAt), Difference(source?.EncodeEnd, source?.EncodeStart),
            Difference(source?.SentAt, source?.EncodeEnd), network is { } n && n - lowOffset * 1000 >= 0 ? Math.Max(0, n - highOffset * 1000) : null, network is { } upper && upper - lowOffset * 1000 >= 0 ? upper - lowOffset * 1000 : null,
            Difference(target?.DecodeStart, target?.ReceivedAt), Difference(target?.DecodeEnd, target?.DecodeStart),
            Difference(target?.OutputSubmittedAt, target?.QueuedAt), Difference(detectedAt, target?.OutputSubmittedAt));
    }
}
