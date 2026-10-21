using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceNative;

// Consume the original timeline even at zero gain; muting must never build backlog.
internal sealed class PlaybackGain(IWaveProvider source) : IWaveProvider
{
    private float gain = 1;
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Gain
    {
        get => Volatile.Read(ref gain);
        set { if (!float.IsFinite(value) || value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value)); Volatile.Write(ref gain, value); }
    }
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer);
        var volume = Gain;
        if (volume == 1) return read;
        var samples = MemoryMarshal.Cast<byte, short>(buffer[..read]);
        foreach (ref var sample in samples) sample = (short)Math.Clamp((int)Math.Round(sample * volume), short.MinValue, short.MaxValue);
        return read;
    }
}
