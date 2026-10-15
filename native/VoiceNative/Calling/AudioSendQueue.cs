using System.Threading.Channels;
using System.Diagnostics;
namespace VoiceNative;

internal sealed record OutgoingAudio(long Sequence, long Created, byte[] Payload, string? TraceHash = null);
public sealed record SendDiagnostics(int PendingFrames, long DroppedFrames, double MaxWaitMs, double? LastWaitMs = null);
internal sealed class AudioSendQueue
{
    private long sequence, dropped;
    private double maxWait;
    private double lastWait = -1;
    private readonly Channel<OutgoingAudio> channel;
    public AudioSendQueue()
    {
        channel = Channel.CreateBounded<OutgoingAudio>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref dropped));
    }
    public void Enqueue(byte[] payload, string? traceHash = null) => channel.Writer.TryWrite(new(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), payload, traceHash));
    public IAsyncEnumerable<OutgoingAudio> Read(CancellationToken token) => channel.Reader.ReadAllAsync(token);
    public void Observe(OutgoingAudio frame)
    {
        var wait = Stopwatch.GetElapsedTime(frame.Created).TotalMilliseconds;
        Interlocked.Exchange(ref lastWait, wait); Interlocked.Exchange(ref maxWait, Math.Max(Volatile.Read(ref maxWait), wait));
    }
    public SendDiagnostics Snapshot() => new(channel.Reader.Count, Interlocked.Read(ref dropped), Volatile.Read(ref maxWait), Volatile.Read(ref lastWait) < 0 ? null : Volatile.Read(ref lastWait));
    public void Complete() => channel.Writer.TryComplete();
    public static uint Timestamp(uint basis, long first, long sequence, uint frameSamples = 960) => unchecked(basis + (uint)(sequence - first) * frameSamples);
}
