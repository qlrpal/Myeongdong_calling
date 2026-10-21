using Concentus;
using SIPSorceryMedia.Abstractions;

namespace VoiceNative;

internal static partial class RenderLatencyProbe
{
    private sealed record RenderPlan(byte[][] Packets, float[] Reference, EncoderDiagnostics Encoder);

    private static RenderPlan BuildRenderPlan(HashSet<int> markers, OpusProfile profile, bool sourceReference)
    {
        var packets = new byte[Seconds * 100][];
        var reference = new float[Seconds * Rate];
        using var encoder = new OpusAudioEncoder(profile);
        using var decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
        var format = encoder.SupportedFormats.First(f => f.Codec == AudioCodecsEnum.OPUS);
        var pcm = new short[Frame]; var decoded = new short[Frame];
        for (var frame = 0; frame < packets.Length; frame++)
        {
            Array.Clear(pcm);
            var marker = markers.Where(k => k <= frame && frame < k + 10).DefaultIfEmpty(-1).Max();
            if (marker >= 0)
                for (var j = 0; j < Frame; j++)
                {
                    var sample = (frame - marker) * Frame + j; var time = sample / (double)Rate;
                    var envelope = Math.Min(1, Math.Min(sample / 96.0, (4800 - sample) / 192.0));
                    pcm[j] = (short)(4500 * envelope * Math.Sin(2 * Math.PI * (500 * time + 15000 * time * time)));
                }
            packets[frame] = encoder.EncodeAudio(pcm, format);
            if (decoder.Decode(packets[frame], decoded, Frame, false) != Frame)
                throw new InvalidOperationException("Reference decode length mismatch.");
            for (var j = 0; j < Frame; j++) reference[frame * Frame + j] = (sourceReference ? pcm[j] : decoded[j]) / 32768f;
        }
        return new(packets, reference, encoder.Snapshot());
    }

    public static void CodecReferenceCheck()
    {
        var offsets = new List<int>();
        foreach (var profile in new[] { OpusProfile.Baseline, OpusProfile.LowDelay64 })
        {
            var plan = BuildRenderPlan([500], profile, true);
            using var decoder = OpusCodecFactory.CreateDecoder(Rate, 1);
            var decoded = new short[Frame]; var signal = new float[Frame * 6];
            for (var frame = 0; frame < 506; frame++)
            {
                if (decoder.Decode(plan.Packets[frame], decoded, Frame, false) != Frame)
                    throw new InvalidOperationException("Codec reference frame length mismatch.");
                if (frame >= 500)
                    for (var i = 0; i < Frame; i++) signal[(frame - 500) * Frame + i] = decoded[i] / 32768f;
            }
            var match = Find(signal, plan.Reference.AsSpan(500 * Frame, 2048));
            if (match.Correlation < .7) throw new InvalidOperationException("Source-reference correlation failed.");
            offsets.Add(match.Offset);
        }
        if (Math.Abs(offsets[0] - 312) > 8 || Math.Abs(offsets[1] - 120) > 8)
            throw new InvalidOperationException("Source reference must retain codec lookahead, rather than cancel it with a decoded template.");
        Console.WriteLine($"PASS: source PCM reference retains baseline/low-delay codec displacement {offsets[0]}/{offsets[1]} samples.");
    }
}
