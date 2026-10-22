using System.Threading.Channels;
using System.Diagnostics;
namespace VoiceNative;

internal readonly record struct OutgoingAudio(long Sequence, long Created, byte[] Payload, string? TraceHash = null, CaptureOrigin? Origin = null);
public sealed record SendDiagnostics(int PendingFrames, long DroppedFrames, double MaxWaitMs, double? LastWaitMs = null,
    string Mode = "queued", bool? MmcssActive = null, string? Error = null, int Capacity = 3);
internal sealed class AudioSendQueue
{
    private long sequence, dropped;
    private double maxWait;
    private double lastWait = -1;
    private readonly Channel<OutgoingAudio> channel;
    private readonly int capacity;
    public AudioSendQueue(int capacity = 3)
    {
        if (capacity is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        channel = Channel.CreateBounded<OutgoingAudio>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref dropped));
    }
    public OutgoingAudio CreateFrame(byte[] payload, string? traceHash = null, CaptureOrigin? origin = null) => new(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), payload, traceHash, origin);
    public void Enqueue(byte[] payload, string? traceHash = null, CaptureOrigin? origin = null) => channel.Writer.TryWrite(CreateFrame(payload, traceHash, origin));
    public IAsyncEnumerable<OutgoingAudio> Read(CancellationToken token) => channel.Reader.ReadAllAsync(token);
    public bool TryRead(out OutgoingAudio frame) => channel.Reader.TryRead(out frame);
    public void Observe(OutgoingAudio frame)
    {
        var wait = Stopwatch.GetElapsedTime(frame.Created).TotalMilliseconds;
        Interlocked.Exchange(ref lastWait, wait); Interlocked.Exchange(ref maxWait, Math.Max(Volatile.Read(ref maxWait), wait));
    }
    public SendDiagnostics Snapshot() => new(channel.Reader.Count, Interlocked.Read(ref dropped), Volatile.Read(ref maxWait), Volatile.Read(ref lastWait) < 0 ? null : Volatile.Read(ref lastWait), Capacity: capacity);
    internal static int BurstCapacity(double periodMs, double frameMs)
    {
        if (!double.IsFinite(frameMs) || frameMs < 1) throw new ArgumentOutOfRangeException(nameof(frameMs));
        if (!double.IsFinite(periodMs) || periodMs <= 0) periodMs = 10;
        return (int)Math.Clamp(Math.Ceiling(periodMs / frameMs), 1, 16);
    }
    public void Complete() => channel.Writer.TryComplete();
    public static uint Timestamp(uint basis, long first, long sequence, uint frameSamples = 960) => unchecked(basis + (uint)(sequence - first) * frameSamples);
}
