using System.Threading.Channels;
using System.Diagnostics;
using System.Runtime.CompilerServices;
namespace VoiceNative;

internal readonly record struct OutgoingAudio(long Sequence, long Created, byte[] Payload, string? TraceHash = null, CaptureOrigin? Origin = null,
    long SampleOffset = 0, int Samples = 960);
public sealed record SendDiagnostics(int PendingFrames, long DroppedFrames, double MaxWaitMs, double? LastWaitMs = null,
    string Mode = "queued", bool? MmcssActive = null, string? Error = null, int Capacity = 3, long ExpiredFrames = 0, double? MaximumQueueAgeMs = null, double? OldestPendingAgeMs = null,
    double OverflowDroppedMs = 0, double OverflowMaxAgeMs = 0);
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
    private readonly IncidentRecorder? incidents;
    private long expired;
    private long overflowSamples, overflowMaxAgeTicks;
    public AudioSendQueue(int capacity = 3, double? maximumAgeMs = null, Func<long>? timestamp = null, IncidentRecorder? incidents = null)
    {
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumAgeMs is { } age && (!double.IsFinite(age) || age <= 0 || age > 40)) throw new ArgumentOutOfRangeException(nameof(maximumAgeMs));
        this.capacity = capacity; this.maximumAgeMs = maximumAgeMs; this.incidents = incidents;
        maximumAgeTicks = maximumAgeMs is { } milliseconds ? (long)Math.Ceiling(milliseconds * Stopwatch.Frequency / 1000) : 0;
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        channel = Channel.CreateBounded<OutgoingAudio>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, frame =>
        {
            Interlocked.Increment(ref dropped);
            Interlocked.Add(ref overflowSamples, frame.Samples);
            var age = Math.Max(0, this.timestamp() - frame.Created);
            long previous;
            do { previous = Volatile.Read(ref overflowMaxAgeTicks); if (age <= previous) break; }
            while (Interlocked.CompareExchange(ref overflowMaxAgeTicks, age, previous) != previous);
            incidents?.Write(new(this.timestamp() / (double)Stopwatch.Frequency, "send-drop", Sequence: frame.Sequence,
                SampleOffset: frame.SampleOffset, Samples: frame.Samples, DurationMs: age * 1000.0 / Stopwatch.Frequency,
                Outcome: "capacity"), "send-overflow");
        });
    }
    public OutgoingAudio CreateFrame(byte[] payload, string? traceHash = null, CaptureOrigin? origin = null, int samples = 960)
    {
        if (samples is not (48 or 120 or 240 or 480 or 960)) throw new ArgumentOutOfRangeException(nameof(samples));
        var frame = new OutgoingAudio(Interlocked.Increment(ref sequence), timestamp(), payload, traceHash, origin,
            Interlocked.Add(ref nextSample, samples) - samples, samples);
        incidents?.Write(new(frame.Created / (double)Stopwatch.Frequency, "frame-created", Sequence: frame.Sequence,
            SampleOffset: frame.SampleOffset, Samples: samples,
            DurationMs: origin is { } capture ? (capture.EncodeEnd - capture.EncodeStart) * 1000 : 0,
            SourceAt: origin?.FirstSampleAt));
        return frame;
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
        Interlocked.Increment(ref dropped); Interlocked.Increment(ref expired);
        incidents?.Write(new(timestamp() / (double)Stopwatch.Frequency, "send-drop", Sequence: frame.Sequence,
            SampleOffset: frame.SampleOffset, Samples: frame.Samples,
            DurationMs: Math.Max(0, timestamp() - frame.Created) * 1000.0 / Stopwatch.Frequency, Outcome: "expired"), "send-expired");
        return false;
    }
    public void Observe(OutgoingAudio frame)
    {
        var wait = Math.Max(0, timestamp() - frame.Created) * 1000.0 / Stopwatch.Frequency;
        Interlocked.Exchange(ref lastWait, wait); Interlocked.Exchange(ref maxWait, Math.Max(Volatile.Read(ref maxWait), wait));
    }
    public SendDiagnostics Snapshot() => new(channel.Reader.Count, Interlocked.Read(ref dropped), Volatile.Read(ref maxWait), Volatile.Read(ref lastWait) < 0 ? null : Volatile.Read(ref lastWait), Capacity: capacity, ExpiredFrames: Interlocked.Read(ref expired), MaximumQueueAgeMs: maximumAgeMs,
        OldestPendingAgeMs: channel.Reader.TryPeek(out var oldest) ? Math.Max(0, timestamp() - oldest.Created) * 1000.0 / Stopwatch.Frequency : null,
        OverflowDroppedMs: Interlocked.Read(ref overflowSamples) / 48.0, OverflowMaxAgeMs: Volatile.Read(ref overflowMaxAgeTicks) * 1000.0 / Stopwatch.Frequency);
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
