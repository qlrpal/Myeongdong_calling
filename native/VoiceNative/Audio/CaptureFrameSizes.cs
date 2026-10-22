namespace VoiceNative;

// Allocate supported sizes before capture starts. Requests are applied only by
// the capture loop at an empty frame boundary, never to a partial PCM frame.
internal sealed class CaptureFrameSizes
{
    private sealed class Storage(int samples)
    {
        public int Samples { get; } = samples;
        public byte[] Bytes { get; } = new byte[samples * 2];
        public short[] Pcm { get; } = new short[samples];
    }
    private readonly Storage[] supported;
    private Storage current, requested;
    public int Samples => Volatile.Read(ref current).Samples;
    public byte[] Bytes => current.Bytes;
    public short[] Pcm => current.Pcm;
    public CaptureFrameSizes(int initial, bool adaptive)
    {
        supported = adaptive ? [new(120), new(240), new(480)] : [new(initial)];
        current = requested = supported.Single(s => s.Samples == initial);
    }
    public void Request(int samples)
    {
        var next = Array.Find(supported, s => s.Samples == samples)
            ?? throw new ArgumentOutOfRangeException(nameof(samples));
        Volatile.Write(ref requested, next);
    }
    public void ApplyAtBoundary() => Volatile.Write(ref current, Volatile.Read(ref requested));
}
