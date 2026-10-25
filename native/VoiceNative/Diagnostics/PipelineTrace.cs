using System.Buffers.Binary;

namespace VoiceNative;

internal readonly record struct CaptureOrigin(double? FirstSampleAt, double FirstCallbackAt,
    double CallbackAt, double LockRequestedAt, double LockAcquiredAt, double FrameReadyAt,
    double EncodeStart, double EncodeEnd, int CallbackSamples, int Flags);
internal sealed record PipelineSend(uint Ssrc, uint Timestamp, long Sequence, double QueuedAt,
    double DequeuedAt, double SendStart, double SendEnd, CaptureOrigin? Capture,
    int? FrameSamples = null, long? SampleOffset = null);
internal sealed record PipelineReceive(string Peer, uint Ssrc, uint Timestamp, ushort Sequence,
    double At, double BufferCallAt, double BufferReturnAt, int PayloadBytes = 0);
internal sealed record PipelineRtpChannel(string Peer, uint Ssrc, uint Timestamp, ushort Sequence,
    int DatagramBytes, int ReceiveBufferBytes, double At);
internal sealed record PipelineAdmission(string Peer, uint Ssrc, uint Timestamp, double CalledAt,
    double LockAt, double ReturnedAt, string Outcome, double QueueMs, int? FrameSamples = null);
internal sealed record PipelinePlayout(string Peer, uint Ssrc, uint Timestamp, double At, double? DecodeStart,
    double? DecodeEnd, double? SubmittedAt, string Outcome, double QueueMs);
internal sealed record PipelineRead(string Peer, double CalledAt, double LockAt, double ReturnedAt,
    int RequestedSamples, int ProvidedSamples, long PlcFrames, double QueueBeforeMs, double QueueAfterMs, bool PlayoutStarted);
internal sealed record PipelineTraceSnapshot(PipelineSend[] Sends, PipelineReceive[] Receives,
    PipelineAdmission[] Admissions, PipelinePlayout[] Playout, PipelineRead[] Reads,
    long EvictedRecords, double MaxRecorderMs, PipelineRtpChannel[]? RtpChannel = null);

// Opt-in, bounded timestamp metadata only. No payload bytes, audio, tokens or raw datagrams.
internal sealed class PipelineTrace
{
    private readonly int capacity;
    public PipelineTrace(int capacity = 24000)
    {
        if (capacity is < 1 or > 24000) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        // Reserve bounded storage before audio starts, avoiding queue growth
        // and large-array allocation on a recording audio callback.
        sends = new(capacity); receives = new(capacity); admissions = new(capacity);
        playout = new(capacity); reads = new(capacity); rtpChannel = new(capacity);
    }
    private readonly object sync = new();
    private readonly Queue<PipelineSend> sends;
    private readonly Queue<PipelineReceive> receives;
    private readonly Queue<PipelineAdmission> admissions;
    private readonly Queue<PipelinePlayout> playout;
    private readonly Queue<PipelineRead> reads;
    private readonly Queue<PipelineRtpChannel> rtpChannel;
    private double until, maxRecorderMs;
    private long evicted;
    public bool Enabled => ProbeClock.Now < Volatile.Read(ref until);
    public void Start(int seconds)
    {
        if (seconds is < 1 or > 180) throw new ArgumentOutOfRangeException(nameof(seconds));
        lock (sync)
        {
            Volatile.Write(ref until, 0);
            sends.Clear(); receives.Clear(); admissions.Clear(); playout.Clear(); reads.Clear(); rtpChannel.Clear();
            evicted = 0; maxRecorderMs = 0;
            Volatile.Write(ref until, ProbeClock.Now + seconds);
        }
    }
    public void Stop() => Volatile.Write(ref until, 0);
    private void Add<T>(Queue<T> queue, T record)
    {
        if (!Enabled) return;
        var began = ProbeClock.Now;
        lock (sync)
        {
            if (!Enabled) return;
            if (queue.Count == capacity) { queue.Dequeue(); evicted++; }
            queue.Enqueue(record);
            maxRecorderMs = Math.Max(maxRecorderMs, (ProbeClock.Now - began) * 1000);
        }
    }
    // SDK RTPChannel event observer, appended after existing SDK handlers.
    // Its timestamp is not socket completion or a pre-decryption timestamp.
    // Only clear fixed RTP header identity/length is retained; never payload bytes.
    public void ObserveRtpChannel(string peer, ReadOnlySpan<byte> packet, int payloadType, int receiveBufferBytes)
    {
        if (!Enabled || packet.Length < 12 || packet[0] >> 6 != 2 || (packet[1] & 127) != payloadType
            || packet.Length < 12 + 4 * (packet[0] & 15)) return;
        Add(rtpChannel, new(peer, BinaryPrimitives.ReadUInt32BigEndian(packet[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(packet[4..]), BinaryPrimitives.ReadUInt16BigEndian(packet[2..]),
            packet.Length, receiveBufferBytes, ProbeClock.Now));
    }
    public void Sent(PipelineSend record) => Add(sends, record);
    public void Received(PipelineReceive record) => Add(receives, record);
    public void Admitted(PipelineAdmission record) => Add(admissions, record);
    public void Played(PipelinePlayout record) => Add(playout, record);
    public void Read(PipelineRead record) => Add(reads, record);
    public PipelineTraceSnapshot Snapshot()
    {
        lock (sync) return new(sends.ToArray(), receives.ToArray(), admissions.ToArray(),
            playout.ToArray(), reads.ToArray(), evicted, maxRecorderMs, rtpChannel.ToArray());
    }
}
