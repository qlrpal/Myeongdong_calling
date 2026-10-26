using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace VoiceNative;

internal static class OpusBackendChecks
{
    internal static void Run()
    {
        var identity = OpusBackend.Snapshot();
        if (identity.NativeActive && identity.NativeLibrarySha256?.Length != 64)
            throw new InvalidOperationException("Native codec fingerprint is missing.");
        using var selectedEncoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        using var selectedDecoder = OpusCodecFactory.CreateDecoder(48000, 1);
        // Explicit managed constructors are necessary for a cross-backend wire test.
#pragma warning disable CS0618
        using var managedEncoder = new OpusEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        using var managedDecoder = new OpusDecoder(48000, 1);
#pragma warning restore CS0618
        selectedEncoder.Bitrate = managedEncoder.Bitrate = 64000;
        selectedEncoder.Complexity = managedEncoder.Complexity = 3;
        if (selectedEncoder.Lookahead != 120 || managedEncoder.Lookahead != 120)
            throw new InvalidOperationException("Restricted low-delay codec lookahead changed.");
        var wire = new byte[1275]; var output = new short[5760];
        foreach (var size in new[] { 120, 240, 480, 960 })
        {
            var source = Enumerable.Range(0, size).Select(i => (short)(8000 * Math.Sin(i * Math.PI * 880 / 24000))).ToArray();
            foreach (var (encoder, decoder) in new (IOpusEncoder, IOpusDecoder)[] {
                (selectedEncoder, managedDecoder), (managedEncoder, selectedDecoder) })
            {
                encoder.ResetState(); decoder.ResetState();
                for (var i = 0; i < 8; i++)
                {
                    var bytes = encoder.Encode(source, size, wire, wire.Length);
                    if (OpusPacketInfo.GetNumSamples(wire.AsSpan(0, bytes), 48000) != size
                        || decoder.Decode(wire.AsSpan(0, bytes), output, output.Length, false) != size)
                        throw new InvalidOperationException("Cross-backend Opus duration mismatch.");
                }
                if (!output.AsSpan(0, size).ContainsAnyExcept((short)0))
                    throw new InvalidOperationException("Cross-backend Opus output is silent.");
                if (decoder.Decode(ReadOnlySpan<byte>.Empty, output, size, false) != size)
                    throw new InvalidOperationException("Cross-backend PLC duration mismatch.");
                decoder.ResetState();
            }
        }
        Console.WriteLine("PASS: selected/managed Opus interoperability, 2.5/5/10/20ms, lookahead, reset and PLC; backend=" + identity.Encoder);
    }
}
