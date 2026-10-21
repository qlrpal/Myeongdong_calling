using System.Text.Json;
using NAudio.CoreAudioApi;

namespace VoiceNative;

internal static partial class RenderLatencyProbe
{
    // Real shared render pulls, synthetic silence and three deliberate 150ms
    // receive gaps. No microphone/network/physical-latency claim.
    public static async Task GapRestartAsync(string output)
    {
        var rounds = new List<object>();
        foreach (var quick in new[] { false, true })
        {
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
            var format = encoder.SupportedFormats.Single(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS);
            var payload = encoder.EncodeAudio(new short[480], format);
            var sink = new WindowsAudioDevice(encoder, disableSource: true, frameMs: 10, useRtpPlayout: true,
                adaptiveReserve: false, fixedReserveClockTarget: true, transientEmptyQueueHold: true,
                freshRestart: true, boundedRender: true, rawRender: true, renderCategory: AudioStreamCategory.GameChat,
                rtpReserveMs: 15, responsiveReserve: true, quickGapRecovery: quick);
            var maxLate = 0.0;
            var injected = 0;
            AudioDiagnostics snapshot;
            try
            {
                await sink.StartAudioSink();
                var begin = ProbeClock.Now + .2;
                await Task.Run(() =>
                {
                    using var timer = new Deadline();
                    for (var i = 0; i < 1200; i++)
                    {
                        timer.Until(begin + i * .01, CancellationToken.None);
                        maxLate = Math.Max(maxLate, (ProbeClock.Now - begin - i * .01) * 1000);
                        if (i is >= 400 and < 415 or >= 500 and < 515 or >= 600 and < 615) { injected++; continue; }
                        sink.GotRtpAudioPacket(1, (uint)i * 480, payload);
                    }
                });
                snapshot = sink.Snapshot();
            }
            finally { await sink.Close(); }
            if (snapshot.DeviceError is not null || snapshot.RtpPlayout is not { } detail
                || detail.Rebuffers < 3 || detail.MaxConsecutivePlcMs > 60
                || quick && detail.ShortReserveRestarts < 1 || !quick && detail.ShortReserveRestarts != 0)
                throw new InvalidOperationException("Controlled shared-render recovery did not exercise the expected path.");
            rounds.Add(new { quick, injectedPackets = injected, maxPacingLateMs = maxLate, snapshot });
            Console.WriteLine($"shared gap quick={quick} restarts={detail.ShortReserveRestarts} underfill={snapshot.UnderfillMs:F1}ms PLC={detail.ConcealedMs:F0}ms pacingMax={maxLate:F2}ms");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            rounds,
            notes = "Real shared output with synthetic 10ms Opus silence and three 150ms receive omissions one source-clock second apart. Sequential unpaired render phases; do not infer physical latency or subjective voice quality. No network or microphone."
        }, DiagnosticJson.Readable));
    }
}
