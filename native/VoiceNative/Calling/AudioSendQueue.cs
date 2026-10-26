using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace VoiceNative;

internal readonly record struct OutgoingAudio(long Sequence, long Created, byte[] Payload, string? TraceHash = null, CaptureOrigin? Origin = null,
    long SampleOffset = 0, int Samples = 960);
public sealed record SendDiagnostics(int PendingFrames, long DroppedFrames, double MaxWaitMs, double? LastWaitMs = null,
    string Mode = "queued", bool? MmcssActive = null, string? Error = null, int Capacity = 3, long ExpiredFrames = 0, double? MaximumQueueAgeMs = null, double? OldestPendingAgeMs = null);
internal sealed class AudioSendQueue
{
    private long sequence, dropped, nextSample;
    private double maxWait;
    private double lastWait = -1;
    private readonly Channel<OutgoingAudio> channel;
    private readonly int capacity;
    private readonly Func<long> timestamp;
    private readonly double? maximumAgeMs;
    private readonly long maximumAgeTicks;
    private long expired;
    public AudioSendQueue(int capacity = 3, double? maximumAgeMs = null, Func<long>? timestamp = null)
    {
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumAgeMs is { } age && (!double.IsFinite(age) || age <= 0 || age > 40)) throw new ArgumentOutOfRangeException(nameof(maximumAgeMs));
        this.capacity = capacity; this.maximumAgeMs = maximumAgeMs;
        maximumAgeTicks = maximumAgeMs is { } milliseconds ? (long)Math.Ceiling(milliseconds * Stopwatch.Frequency / 1000) : 0;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        channel = Channel.CreateBounded<OutgoingAudio>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref dropped));
    }
    public OutgoingAudio CreateFrame(byte[] payload, string? traceHash = null, CaptureOrigin? origin = null, int samples = 960)
    {
        if (samples is not (48 or 120 or 240 or 480 or 960)) throw new ArgumentOutOfRangeException(nameof(samples));
        return new(Interlocked.Increment(ref sequence), timestamp(), payload, traceHash, origin,
            Interlocked.Add(ref nextSample, samples) - samples, samples);
    }
    public void Enqueue(byte[] payload, string? traceHash = null, CaptureOrigin? origin = null, int samples = 960) => channel.Writer.TryWrite(CreateFrame(payload, traceHash, origin, samples));
    public IAsyncEnumerable<OutgoingAudio> Read(CancellationToken token) => maximumAgeMs is null ? channel.Reader.ReadAllAsync(token) : ReadFresh(token);
    private async IAsyncEnumerable<OutgoingAudio> ReadFresh([EnumeratorCancellation] CancellationToken token)
    {
        await foreach(var frame in channel.Reader.ReadAllAsync(token)) if (Fresh(frame)) yield return frame;
    }
    public bool TryRead(out OutgoingAudio frame)
    {
        while (channel.Reader.TryRead(out frame)) if (Fresh(frame)) return true;
        return false;
    }
    private bool Fresh(OutgoingAudio frame)
    {
        if (maximumAgeMs is null || timestamp() - frame.Created <= maximumAgeTicks) return true;
        Interlocked.Increment(ref dropped); Interlocked.Increment(ref expired); return false;
    }
    public void Observe(OutgoingAudio frame)
    {
        var wait = Math.Max(0, timestamp() - frame.Created) * 1000.0 / Stopwatch.Frequency;
        Interlocked.Exchange(ref lastWait, wait); Interlocked.Exchange(ref maxWait, Math.Max(Volatile.Read(ref maxWait), wait));
    }
    public SendDiagnostics Snapshot() => new(channel.Reader.Count, Interlocked.Read(ref dropped), Volatile.Read(ref maxWait), Volatile.Read(ref lastWait) < 0 ? null : Volatile.Read(ref lastWait), Capacity: capacity, ExpiredFrames: Interlocked.Read(ref expired), MaximumQueueAgeMs: maximumAgeMs,
        OldestPendingAgeMs: channel.Reader.TryPeek(out var oldest) ? Math.Max(0, timestamp() - oldest.Created) * 1000.0 / Stopwatch.Frequency : null);
    internal static int BurstCapacity(double periodMs, double frameMs)
    {
        if (!double.IsFinite(frameMs) || frameMs < 1) throw new ArgumentOutOfRangeException(nameof(frameMs));
        if (!double.IsFinite(periodMs) || periodMs <= 0) periodMs = 10;
        return (int)Math.Clamp(Math.Ceiling(periodMs / frameMs), 1, 16);
    }
    internal static (int Capacity, double MaximumAgeMs) DedicatedLimits(double periodMs, double frameMs)
    {
        var burst = BurstCapacity(periodMs, frameMs);
        if (!double.IsFinite(periodMs) || periodMs <= 0) periodMs = 10;
        // Two adjacent device batches can overlap a busy worker. Capacity never
        // exceeds 16; dequeue age, not capacity alone, limits extra waiting.
        return (Math.Min(16, burst * 2), Math.Clamp(Math.Max(periodMs, frameMs), 1, 40));
    }
    public void Complete() => channel.Writer.TryComplete();
    internal static uint Timestamp(uint basis, long firstSampleOffset, OutgoingAudio frame) => unchecked(basis + (uint)(frame.SampleOffset - firstSampleOffset));
    public static uint Timestamp(uint basis, long first, long sequence, uint frameSamples = 960) => unchecked(basis + (uint)(sequence - first) * frameSamples);
}
