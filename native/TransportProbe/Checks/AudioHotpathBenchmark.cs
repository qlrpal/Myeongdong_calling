using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class AudioHotpathBenchmark
{
    public static void Run(string output)
    {
        var results = new List<object>();
        foreach (var mode in new[] { "opus-adaptive", "opus-adaptive-5", "pcm-1" })
        {
            var options = LatencyExperiment.FromName(mode)!;
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
            var input = new short[options.FrameSamples];
            for (var i = 0; i < input.Length; i++) input[i] = (short)(8000 * Math.Sin(i * Math.PI * 880 / 48000));
            var payload = encoder.EncodeAudio(input, encoder.SupportedFormats.Single());
            for (var repeat = 0; repeat < 3; repeat++)
            {
                using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
                var destination = new byte[options.FrameSamples * 2];
                uint timestamp = 0;
                var prime = (int)Math.Ceiling(options.ReserveMs / options.FrameMs) + 1;
                for (var i = 0; i < prime; i++) { buffer.Add(1, timestamp, payload); timestamp += (uint)options.FrameSamples; }
                buffer.Read(destination);
                for (var i = 0; i < 2000; i++) { buffer.Add(1, timestamp, payload); timestamp += (uint)options.FrameSamples; buffer.Read(destination); }
                const int count = 10000;
                var admission = new long[count]; var playback = new long[count];
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < count; i++)
                {
                    var begin = Stopwatch.GetTimestamp(); buffer.Add(1, timestamp, payload);
                    var middle = Stopwatch.GetTimestamp(); buffer.Read(destination);
                    var end = Stopwatch.GetTimestamp(); timestamp += (uint)options.FrameSamples;
                    admission[i] = middle - begin; playback[i] = end - middle;
                }
                var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                var detail = buffer.Details();
                if (detail.InvalidPackets != 0 || detail.ConcealedMs != 0 || buffer.Snapshot().UnderfillMs != 0)
                    throw new InvalidOperationException("Benchmark media continuity failed");
                results.Add(new { mode, repeat, bytesPerPacket = bytes / (double)count, admission = Summary(admission), playback = Summary(playback) });
            }
        }
        var queue = new AudioSendQueue(); var sharedPayload = new byte[40];
        for (var i = 0; i < 1000; i++) queue.Observe(queue.CreateFrame(sharedPayload));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) queue.Observe(queue.CreateFrame(sharedPayload));
        var senderBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 10000.0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { build = typeof(WebRtcCallEngine).Assembly.ManifestModule.ModuleVersionId,
            opusBackend = OpusBackend.Snapshot(),
            runtime = Environment.Version.ToString(), senderMetadataBytesPerPacket = senderBytes, results,
            note = "Synthetic warmed synchronous buffer and sender metadata microbenchmark; excludes encoder, transport, device, scheduling and physical latency. Stopwatch overhead retained; no general tail-latency guarantee." }, DiagnosticJson.Readable));
        Console.WriteLine($"PASS: hot-path continuity and allocation benchmark: {output}");
    }

    private static object Summary(long[] ticks)
    {
        Array.Sort(ticks);
        double Us(long value) => value * 1000000.0 / Stopwatch.Frequency;
        return new { p50Us = Us(ticks[ticks.Length / 2]), p95Us = Us(ticks[(int)(ticks.Length * .95)]),
            p99Us = Us(ticks[(int)(ticks.Length * .99)]), maxUs = Us(ticks[^1]) };
    }
}
