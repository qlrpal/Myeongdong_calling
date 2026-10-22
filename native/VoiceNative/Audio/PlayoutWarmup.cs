namespace VoiceNative;

// Run a disposable software path before the first experimental output starts.
// No device is opened and no warm-up samples/counters enter the real call.
internal static class PlayoutWarmup
{
    private static readonly object sync = new();
    private static readonly HashSet<(double FrameMs, bool Pcm)> warmed = new();

    public static void Ensure(LatencyExperiment options)
    {
        options.Validate();
        lock (sync)
        {
            if (warmed.Contains((options.FrameMs, options.Pcm))) return;
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
            var format = encoder.SupportedFormats.Single();
            var tone = Enumerable.Range(0, options.FrameSamples).Select(i => (short)(4000 * Math.Sin(i * .1))).ToArray();
            encoder.EncodeAudio(tone, format); // Cross the short encoder's initial lookahead.
            var payload = encoder.EncodeAudio(tone, format);
            using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
            var packets = (options.ReserveSamples + 1920 + options.FrameSamples - 1) / options.FrameSamples;
            for (var i = 0; i < packets; i++) buffer.Add(1, (uint)(i * options.FrameSamples), payload);
            var output = new byte[960];
            for (var i = 0; i < 12; i++) buffer.Read(output); // Decode, interpolation, PLC, bounded restart.
            warmed.Add((options.FrameMs, options.Pcm));
        }
    }
}
