using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VoiceNative;
using SIPSorceryMedia.Abstractions;

// Deterministic media-time replay. No devices, network or wall-clock audio deadlines.
internal static class RenderLoopComparison
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task RunAsync(string report)
    {
        var rows = new List<object>();
        foreach (var samples in new[] { 120, 240, 480 })
        {
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
            var format = new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 1);
            var payloads = Enumerable.Range(0, 16).Select(frame => encoder.EncodeAudio(
                Enumerable.Range(0, samples).Select(i => (short)(10000 * Math.Sin((i + frame * samples) * .039))).ToArray(), format)).ToArray();
            foreach (var read in new[] { 128, 256, 480 })
                foreach (var drift in new[] { -200, 0, 200 })
                    foreach (var disturbed in new[] { false, true })
                        rows.Add(Run(samples, read, drift, disturbed, payloads));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { schemaVersion = 1,
            appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WebRtcCallEngine).Assembly.Location))),
            backend = OpusBackend.Snapshot(), scope = "Synthetic 30s media time per case; no device/network/game/physical latency claims", rows }, Json));
        Console.WriteLine($"PASS: {rows.Count} deterministic render replays written to {report}");
    }

    private static object Run(int samples, int read, int drift, bool disturbed, byte[][] payloads)
    {
        using var buffer = new RtpOpusPlayoutBuffer(new(), adaptiveReserve: false, fixedReserveClockTarget: true,
            transientEmptyQueueHold: true, freshRestart: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var output = new byte[read * 2];
        long renderTicks = 0, allocation = 0;
        var frame = 0; var outputAt = 0.0; var inputAt = 0.0;
        var frameMs = samples / 48.0 / (1 + drift / 1_000_000.0);
        while (outputAt < 30_000)
        {
            while (inputAt <= outputAt)
            {
                // RTP wrap; repeatable jitter, gaps, duplicates and source replacement.
                var source = frame < 15_000 / frameMs ? 1u : 2u;
                var timestamp = unchecked(uint.MaxValue - 959 + (uint)(frame * samples));
                if (!disturbed || frame % 503 != 300)
                {
                    buffer.Add(source, timestamp, payloads[frame % payloads.Length], inputAt);
                    if (disturbed && frame % 271 == 200) buffer.Add(source, timestamp, payloads[frame % payloads.Length], inputAt);
                }
                frame++;
                var jitter = disturbed && frame % 97 == 30 ? 12 : 0;
                inputAt = Math.Max(inputAt, frame * frameMs + jitter);
            }
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var began = Stopwatch.GetTimestamp();
            buffer.Read(output);
            renderTicks += Stopwatch.GetTimestamp() - began;
            allocation += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            hash.AppendData(output);
            outputAt += read / 48.0;
        }
        var d = buffer.Details(); var s = buffer.Snapshot();
        return new { frameSamples = samples, readSamples = read, driftPpm = drift, disturbed,
            pcmHash = Convert.ToHexString(hash.GetHashAndReset()), renderMs = renderTicks * 1000.0 / Stopwatch.Frequency,
            renderAllocatedBytes = allocation, d.DecodedPackets, d.PlcFrames, d.ConcealedMs, d.LatePackets,
            d.DuplicatePackets, d.InvalidPackets, d.Rebuffers, d.Resyncs, d.SourceChanges, d.RecoveryDiscardedMs,
            d.RateCorrectionPpm, s.QueueMs, s.ConsumedMs, s.RequestedMs, s.UnderfillMs };
    }
}
