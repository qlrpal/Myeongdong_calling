using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using VoiceNative;

internal static class OpusComparison
{
    public static void Run(string reportPath)
    {
        const int frameSamples = 480, frames = 300;
        var random = new Random(3241);
        var signal = Enumerable.Range(0, frameSamples * frames).Select(i =>
        {
            var seconds = i / 48000.0;
            var envelope = i % 12000 < 2400 ? 0.1 : 1.0;
            return (short)(envelope * (5000 * Math.Sin(2 * Math.PI * (180 * seconds + 15 * seconds * seconds))
                + 2000 * Math.Sin(2 * Math.PI * 1300 * seconds) + random.Next(-2500, 2501)));
        }).ToArray();
        // Confirm the baseline wrapper reproduces the original bitstream exactly.
        using (var original = new AudioEncoder(includeOpus: true))
        using (var baseline = new OpusAudioEncoder(OpusProfile.Baseline))
        {
            var format = original.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
            for (var i = 0; i < 20; i++)
            {
                var pcm = signal.AsSpan(i * frameSamples, frameSamples).ToArray();
                if (!original.EncodeAudio(pcm, format).SequenceEqual(baseline.EncodeAudio(pcm, format)))
                    throw new InvalidOperationException("Baseline bitstream changed.");
            }
        }
        var results = new List<object>();
        var observed = new Dictionary<OpusProfile, (int Lag, double Correlation)>();
        foreach (var profile in Enum.GetValues<OpusProfile>())
        {
            using var encoder = new OpusAudioEncoder(profile);
            using var decoder = new AudioEncoder(includeOpus: true);
            var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
            var output = new List<short>(); var times = new List<double>(); long bytes = 0;
            // Warm processing/JIT without changing the measured signal's codec state.
            for (var i = 0; i < 50; i++) encoder.EncodeAudio(new short[frameSamples], format);
            for (var i = 0; i < frames; i++)
            {
                var pcm = signal.AsSpan(i * frameSamples, frameSamples).ToArray();
                var started = Stopwatch.GetTimestamp();
                var packet = encoder.EncodeAudio(pcm, format);
                times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                bytes += packet.Length;
                var decoded = decoder.DecodeAudio(packet, format);
                if (decoded.Length != frameSamples) throw new InvalidOperationException("Decoder compatibility/frame duration failed.");
                output.AddRange(decoded);
            }
            var samples = output.ToArray(); var bestLag = 0; double best = -1;
            for (var lag = 0; lag <= 480; lag++)
            {
                double cross = 0, inputEnergy = 0, outputEnergy = 0;
                for (var i = 960; i < signal.Length - 960; i++)
                {
                    double input = signal[i], decoded = samples[i + lag];
                    cross += input * decoded; inputEnergy += input * input; outputEnergy += decoded * decoded;
                }
                var correlation = cross / Math.Sqrt(inputEnergy * outputEnergy);
                if (correlation > best) { best = correlation; bestLag = lag; }
            }
            if (best < 0.2) throw new InvalidOperationException("No reliable synthetic alignment peak for " + profile);
            var config = encoder.Snapshot();
            if (config.LookaheadMs is { } lookahead && Math.Abs(bestLag / 48.0 - lookahead) > 0.1)
                throw new InvalidOperationException("Observed codec alignment disagrees with reported lookahead for " + profile);
            var sorted = times.Order().ToArray();
            observed[profile] = (bestLag, best);
            results.Add(new
            {
                profile,
                config,
                frameMs = 10,
                encodedFrames = frames,
                decodedSamples = samples.Length,
                payloadKbps = bytes * 8 / 3.0 / 1000,
                encodeMeanMs = times.Average(),
                encodeP95Ms = sorted[(int)(sorted.Length * 0.95)],
                encodeMaxMs = sorted[^1],
                observedDisplacementMs = bestLag / 48.0,
                alignedCorrelation = best
            });
            Console.WriteLine($"{profile}: lookahead={config.LookaheadMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "baseline"}ms, observed={bestLag / 48.0:F2}ms, encode P95={sorted[(int)(sorted.Length * 0.95)]:F3}ms, payload={bytes * 8 / 3000.0:F1}kbps, synthetic correlation={best:F3}");
        }
        foreach (var pair in new[] { (OpusProfile.Voip64, OpusProfile.LowDelay64), (OpusProfile.Voip96, OpusProfile.LowDelay96) })
            if (observed[pair.Item2].Lag >= observed[pair.Item1].Lag) throw new InvalidOperationException("Restricted low-delay did not reduce codec displacement.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now,
            applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
            notes = "고정 합성 신호·48kHz 모노·10ms. 같은 64/96kbps에서 모드만 비교. 실제 발화 음질/MOS, 마이크→상대 출력, 장치·네트워크 지연 검사가 아님. 상관계수는 주관적 음질 점수가 아님. 코덱 대기는 합산 모델에 자동 가산하지 않음.",
            results
        }, VoiceNative.DiagnosticJson.Readable));
        Console.WriteLine("PASS: unchanged baseline bitstream, 10ms standard-decoder compatibility and same-bitrate codec-delay comparisons; " + reportPath);
    }
}
