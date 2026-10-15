using NAudio.Wave;
using System.Buffers.Binary;

namespace VoiceNative;

// No resampling or extra buffering: convert 48kHz native PCM/float channels to mono16.
internal sealed class NativeCaptureConverter
{
    private readonly WaveFormat format;
    private readonly bool floating;
    public NativeCaptureConverter(WaveFormat format)
    {
        this.format = format;
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extended)
            encoding = extended.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat
                : extended.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm : WaveFormatEncoding.Unknown;
        floating = encoding == WaveFormatEncoding.IeeeFloat;
        if (format.SampleRate != 48000 || format.Channels is < 1 or > 8
            || !(floating && format.BitsPerSample == 32 || encoding == WaveFormatEncoding.Pcm && format.BitsPerSample is 16 or 24 or 32))
            throw new NotSupportedException("저지연 입력 변환은 48kHz PCM16/24/32 또는 float32 형식이 필요합니다.");
    }
    public int BlockAlign => format.BlockAlign;
    public int Convert(ReadOnlySpan<byte> source, Span<byte> target)
    {
        if (source.Length % BlockAlign != 0 || source.Length / BlockAlign * 2 > target.Length)
            throw new ArgumentException("Incomplete capture frame or destination too small.");
        var width = format.BitsPerSample / 8;
        var frames = source.Length / BlockAlign;
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var sample = source.Slice(frame * BlockAlign + channel * width, width);
                double value = floating ? BinaryPrimitives.ReadSingleLittleEndian(sample)
                    : width == 2 ? BinaryPrimitives.ReadInt16LittleEndian(sample) / 32768.0
                    : width == 3 ? ((sample[0] | sample[1] << 8 | sample[2] << 16) << 8 >> 8) / 8388608.0
                    : BinaryPrimitives.ReadInt32LittleEndian(sample) / 2147483648.0;
                if (double.IsFinite(value)) sum += value;
            }
            BinaryPrimitives.WriteInt16LittleEndian(target.Slice(frame * 2, 2),
                (short)Math.Clamp(sum / format.Channels * 32768, short.MinValue, short.MaxValue));
        }
        return frames * 2;
    }
}
