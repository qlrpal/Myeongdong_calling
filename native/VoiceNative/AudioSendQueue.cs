using System.Threading.Channels;
using System.Diagnostics;
namespace VoiceNative;

internal sealed record OutgoingAudio(long Sequence, long Created, byte[] Payload);
public sealed record SendDiagnostics(int PendingFrames, long DroppedFrames, double MaxWaitMs);
internal sealed class AudioSendQueue
{
    private long sequence, dropped;
    private double maxWait;
    private readonly Channel<OutgoingAudio> channel;
    public AudioSendQueue()
    {
        channel = Channel.CreateBounded<OutgoingAudio>(new BoundedChannelOptions(3)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref dropped));
    }
    public void Enqueue(byte[] payload) => channel.Writer.TryWrite(new(Interlocked.Increment(ref sequence), Stopwatch.GetTimestamp(), payload));
    public IAsyncEnumerable<OutgoingAudio> Read(CancellationToken token) => channel.Reader.ReadAllAsync(token);
    public void Observe(OutgoingAudio frame) => Interlocked.Exchange(ref maxWait, Math.Max(Volatile.Read(ref maxWait), Stopwatch.GetElapsedTime(frame.Created).TotalMilliseconds));
    public SendDiagnostics Snapshot() => new(channel.Reader.Count, Interlocked.Read(ref dropped), Volatile.Read(ref maxWait));
    public void Complete() => channel.Writer.TryComplete();
    public static uint Timestamp(uint basis, long first, long sequence) => unchecked(basis + (uint)(sequence - first) * 960);
}
