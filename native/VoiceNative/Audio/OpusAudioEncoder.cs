using Concentus;
using Concentus.Enums;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace VoiceNative;

public enum OpusProfile { Baseline, Voip64, LowDelay64, Voip96, LowDelay96 }
public sealed record EncoderDiagnostics(string Profile, string Application, int? TargetBitrate,
    int? Complexity, bool? Vbr, double? LookaheadMs, string Backend);

// Leave the original encoder/decoder untouched for the baseline. Compare explicit modes
// at equal bitrate, complexity, frame size and VBR; the SDP and decoder remain Opus.
internal sealed class OpusAudioEncoder : IAudioEncoder, IDisposable
{
    private readonly AudioEncoder baseline = new(includeOpus: true);
    private readonly IOpusEncoder? configured;
    private readonly byte[] packet = new byte[1275];
    private readonly EncoderDiagnostics diagnostics;
    public OpusProfile Profile { get; }
    public List<AudioFormat> SupportedFormats => baseline.SupportedFormats;
    public OpusAudioEncoder(OpusProfile profile)
    {
        if (!Enum.IsDefined(profile)) { baseline.Dispose(); throw new ArgumentOutOfRangeException(nameof(profile)); }
        Profile = profile;
        if (profile == OpusProfile.Baseline)
        {
            diagnostics = new(Profile.ToString(), "라이브러리 기본값", null, null, null, null, "SIPSorcery AudioEncoder");
            return;
        }
        try
        {
            configured = OpusCodecFactory.CreateEncoder(48000, 1,
                profile is OpusProfile.LowDelay64 or OpusProfile.LowDelay96
                    ? OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY : OpusApplication.OPUS_APPLICATION_VOIP);
            configured.Bitrate = profile is OpusProfile.Voip64 or OpusProfile.LowDelay64 ? 64000 : 96000;
            configured.Complexity = 9;
            configured.UseVBR = true;
            diagnostics = new(Profile.ToString(), configured.Application.ToString(), configured.Bitrate,
                configured.Complexity, configured.UseVBR, configured.Lookahead / 48.0, configured.GetType().Name);
        }
        catch { configured?.Dispose(); baseline.Dispose(); throw; }
    }
    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        if (configured is null) return baseline.EncodeAudio(pcm, format);
        if (format.Codec != AudioCodecsEnum.OPUS || pcm.Length is not (480 or 960))
            throw new ArgumentException("Opus comparison requires 48kHz mono 10/20ms PCM.");
        var length = configured.Encode(pcm, pcm.Length, packet, packet.Length);
        if (length <= 0) throw new InvalidOperationException("Opus encoding failed: " + length);
        return packet.AsSpan(0, length).ToArray();
    }
    public short[] DecodeAudio(byte[] bytes, AudioFormat format) => baseline.DecodeAudio(bytes, format);
    // Immutable startup configuration: diagnostics never call codec controls concurrently with encoding.
    public EncoderDiagnostics Snapshot() => diagnostics;
    public void Dispose() { configured?.Dispose(); baseline.Dispose(); }
    public static string Label(OpusProfile profile) => profile switch
    {
        OpusProfile.Baseline => "Opus 기존 0.4.8 설정",
        OpusProfile.Voip64 => "Opus VOIP · 64kbps",
        OpusProfile.LowDelay64 => "Opus 저지연 · 64kbps",
        OpusProfile.Voip96 => "Opus VOIP · 96kbps",
        OpusProfile.LowDelay96 => "Opus 저지연 · 96kbps",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
}
