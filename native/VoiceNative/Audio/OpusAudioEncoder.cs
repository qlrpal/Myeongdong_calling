using Concentus;
using Concentus.Enums;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Buffers.Binary;

namespace VoiceNative;

public enum OpusProfile { Baseline, Voip64, LowDelay64, Voip96, LowDelay96 }
public sealed record EncoderDiagnostics(string Profile, string Application, int? TargetBitrate,
    int? Complexity, bool? Vbr, double? LookaheadMs, string Backend);

// Leave the original encoder/decoder untouched for the baseline. Compare explicit modes
// at equal bitrate, complexity, frame size and VBR; the SDP and decoder remain Opus.
internal sealed class OpusAudioEncoder : IAudioEncoder, IDisposable
{
    public const OpusProfile DefaultProfile = OpusProfile.LowDelay64;
    private readonly AudioEncoder baseline;
    private readonly IOpusEncoder? configured;
    private readonly byte[] packet = new byte[1275];
    private readonly EncoderDiagnostics diagnostics;
    private readonly bool pcmTransport;
    private readonly LatencyExperiment? experiment;
    public OpusProfile Profile { get; }
    internal static AudioFormat ExperimentFormat(LatencyExperiment experiment) => experiment.Pcm
        ? new(AudioCodecsEnum.L16, 112, 48000, 1)
        : new(AudioCodecsEnum.OPUS, 111, 48000, 2, "minptime=" + experiment.FrameMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";useinbandfec=0;stereo=0;sprop-stereo=0");
    public List<AudioFormat> SupportedFormats => experiment is not null ? [ExperimentFormat(experiment)] : baseline.SupportedFormats;
    public OpusAudioEncoder(OpusProfile profile, LatencyExperiment? experiment = null)
    {
        _ = OpusBackend.Snapshot();
        baseline = new(includeOpus: true);
        experiment?.Validate(); this.experiment = experiment; pcmTransport = experiment?.Pcm == true;
        if (!Enum.IsDefined(profile)) { baseline.Dispose(); throw new ArgumentOutOfRangeException(nameof(profile)); }
        Profile = profile;
        if (pcmTransport) { diagnostics = new("PCM experiment", "L16", 768000, null, false, 0, "PCM16 network byte order"); return; }
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
            configured.Complexity = experiment?.Complexity ?? 9;
            configured.UseVBR = true;
            diagnostics = new(Profile.ToString(), configured.Application.ToString(), configured.Bitrate,
                configured.Complexity, configured.UseVBR, configured.Lookahead / 48.0, configured.GetType().Name);
        }
        catch { configured?.Dispose(); baseline.Dispose(); throw; }
    }
    public byte[] EncodeAudio(short[] pcm, AudioFormat format)
    {
        if (pcmTransport)
        {
            if (format.Codec != AudioCodecsEnum.L16 || pcm.Length is not (48 or 120 or 240 or 480)) throw new ArgumentException("Invalid L16 experiment frame.");
            var bytes = new byte[pcm.Length * 2];
            for (var i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2, 2), pcm[i]);
            return bytes;
        }
        if (configured is null) return baseline.EncodeAudio(pcm, format);
        if (format.Codec != AudioCodecsEnum.OPUS || pcm.Length is not (120 or 240 or 480 or 960))
            throw new ArgumentException("Opus requires 48kHz mono 2.5/5/10/20ms PCM.");
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
