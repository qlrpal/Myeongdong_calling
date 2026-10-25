using Concentus.Structs;

namespace VoiceNative;

internal static class AdaptiveFrameChecks
{
    private static readonly int[] SupportedDurations = [120, 240, 480];
    private static readonly int[] MixedDurations = [120, 240, 480, 120];
    private static readonly int[] CaptureTransitions = [120, 480, 240, 120];
    public static async Task RunAsync()
    {
        var sizes = new CaptureFrameSizes(120, true);
        var original = sizes.Bytes; original[0] = 42;
        sizes.Request(480);
        Require(sizes.Samples == 120 && sizes.Bytes[0] == 42, "Request changed a partial capture frame");
        sizes.ApplyAtBoundary(); Require(sizes.Samples == 480 && sizes.Pcm.Length == 480, "Boundary size switch");
        sizes.Request(120); sizes.ApplyAtBoundary(); Require(ReferenceEquals(original, sizes.Bytes), "Frame storage reallocated");
        var queue = new AudioSendQueue(1);
        long offset = 0;
        foreach (var samples in new[] { 120, 240, 480, 120, 240 })
        {
            var frame = queue.CreateFrame([1], samples: samples);
            Require(frame.SampleOffset == offset && frame.Samples == samples, "Variable frame RTP timeline");
            var timestamp = unchecked(uint.MaxValue - 119 + (uint)frame.SampleOffset);
            Require(timestamp == unchecked(uint.MaxValue - 119 + (uint)offset), "RTP wrap");
            offset += samples;
        }
        queue.Enqueue([1], samples: 120); queue.Enqueue([2], samples: 480);
        Require(queue.TryRead(out var retained) && retained.SampleOffset == offset + 120, "Dropped variable frame lost sample time");

        CheckPolicy();
        CheckCaptureBurst();
        CheckFractionalTransitionCapacity();
        await CheckCaptureBoundaryAsync();
        Require(MediaInterfaceSelection.IsSelectable(System.Net.IPAddress.Parse("2001:db8::1"))
            && MediaInterfaceSelection.IsSelectable(System.Net.IPAddress.Parse("fd00::1"))
            && !MediaInterfaceSelection.IsSelectable(System.Net.IPAddress.IPv6Loopback)
            && !MediaInterfaceSelection.IsSelectable(System.Net.IPAddress.Parse("fe80::1"))
            && !MediaInterfaceSelection.IsSelectable(System.Net.IPAddress.Parse("ff02::1")), "IPv6 selection boundaries");
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        options.Validate();
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
        uint stamp = 0;
        foreach (var samples in Enumerable.Repeat(MixedDurations, 12).SelectMany(x => x))
        {
            var payload = encoder.EncodeAudio(new short[samples], encoder.SupportedFormats.Single());
            Require(OpusPacketInfo.GetNumSamples(payload, 48000) == samples, "Encoder duration did not switch");
            buffer.Add(1, stamp, payload); stamp += (uint)samples;
            buffer.Read(new byte[240]);
        }
        Require(buffer.Details().InvalidPackets == 0 && buffer.Details().TargetReserveMs >= 5, "Mixed runtime durations rejected");
        buffer.Suspend(); buffer.Resume();
        using var longEncoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        var ten = longEncoder.EncodeAudio(new short[480], longEncoder.SupportedFormats.First(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS));
        buffer.Add(2, 0, ten);
        Require(buffer.Details().TargetReserveMs == 20, "Long-packet reserve floor not applied");

        var state = new TelemetryState();
        var telemetry = new LatencyTelemetry(1, null, null, null, null, null, null, null,
            FrameFeedback: new(true, 100, 1000, 0, 0, 0, true));
        Require(state.Receive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(telemetry)), "Feedback roundtrip");
        Require(!state.Receive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(telemetry with
            { FrameFeedback = telemetry.FrameFeedback! with { DecodedMs = -1 } })), "Negative feedback accepted");
        Require(!state.Receive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(telemetry with
            { FrameFeedback = telemetry.FrameFeedback! with { UnderfillMs = -1 } })), "Negative underfill accepted");
        Require(!state.Receive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(telemetry with
            { FrameFeedback = telemetry.FrameFeedback! with { RecoveryDiscardedMs = -1 } })), "Negative discard accepted");
        await Task.Factory.StartNew(() =>
        {
            using var registration = new AudioThreadPriority("Pro Audio", true);
            Require(registration.Active ? registration.CriticalSet is not null : registration.Error is not null, "MMCSS result missing");
            Console.WriteLine($"MMCSS probe: active={registration.Active}, critical={registration.CriticalSet}, error={registration.Error}");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Console.WriteLine("PASS: boundary frame storage, variable-sample RTP/drop/wrap, adaptive trial/rollback/recovery, mixed Opus, feedback validation and thread-local MMCSS.");
    }

    private static void CheckFractionalTransitionCapacity()
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payloads = SupportedDurations.ToDictionary(n => n,
            n => encoder.EncodeAudio(new short[n], encoder.SupportedFormats.Single()));
        using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
        var output = new byte[976];
        double captureAt = 10, renderAt = 40;
        long produced = 0; double initialUnderfill = 0;
        for (var now = 0.0; now < 12000;)
        {
            now = Math.Min(captureAt, renderAt);
            if (now == captureAt)
            {
                var samples = now < 3000 ? 120 : now < 6000 ? 480 : 240;
                while (produced + samples <= now * 48)
                { buffer.Add(1, unchecked(uint.MaxValue - 960 + (uint)produced), payloads[samples], now); produced += samples; }
                captureAt += 10;
            }
            if (now == renderAt)
            {
                buffer.Read(output); renderAt += 488 / 48.0;
                if (now < 1000) initialUnderfill = buffer.Snapshot().UnderfillMs;
            }
        }
        if (buffer.Details().InvalidPackets != 0 || buffer.Snapshot().UnderfillMs > initialUnderfill)
            throw new InvalidOperationException("Fractional render/2.5->10->5ms transition exceeded its packet-rounded capacity");
    }

    private static void CheckCaptureBurst()
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
        uint stamp = 0;
        var output = new byte[960];
        for (var at = 0; at <= 2000; at += 10)
        {
            if (at > 0 && at % 20 == 0)
                for (var i = 0; i < 8; i++) { buffer.Add(1, stamp, payload, at); stamp += 120; }
            if (at >= 40) buffer.Read(output);
        }
        Require(buffer.Details().ConcealedMs == 0 && buffer.Snapshot().UnderfillMs == 0,
            "Lossless 20ms arrival batches manufactured PLC/underfill");
        Require(buffer.Details().RecentArrivalBurstMs == 20, "Arrival burst not observed/bounded");
        for (var at = 2010; at <= 4000; at += 10)
        {
            for (var i = 0; i < 4; i++) { buffer.Add(1, stamp, payload, at); stamp += 120; }
            buffer.Read(output);
        }
        Require(buffer.Details().RecentArrivalBurstMs == 10, "Old 20ms burst floor did not expire");
        buffer.Suspend(); buffer.Resume();
        Require(buffer.Details().RecentArrivalBurstMs == 0, "Burst estimate survived timeline reset");
    }

    private static void CheckPolicy()
    {
        ReceiverFrameFeedback F(int windows, bool bad) => new(true, windows * 1000, windows * (bad ? 2940 : 3000), windows * (bad ? 60 : 0), 0, 0, true, 0, 0);
        var p = new AdaptiveFramePolicy();
        p.Observe(0, F(0, true)); p.Observe(3, F(1, true)); p.Observe(6, F(2, true));
        Require(p.TargetMs == 5, "Repeated bad windows did not start trial");
        p.Observe(9, F(3, true)); p.Observe(12, F(4, true));
        Require(p.TargetMs == 2.5, "Ineffective trial did not roll back");
        p.Observe(15, F(5, true)); p.Observe(18, F(6, true));
        Require(p.TargetMs == 2.5, "Cooldown missing");
        p.Observe(30, F(7, true)); p.Observe(33, F(8, true));
        Require(p.TargetMs == 2.5, "Failed trial retried before backoff expired");
        p = new(); p.Observe(0, F(0, true)); p.Observe(3, F(1, true)); p.Observe(6, F(2, true));
        for (var i = 1; i <= 10; i++) p.Observe(6 + i * 3, new(true, 2000 + i * 1000, 5880 + i * 3000, 120, 0, 0, true, 0, 0));
        Require(p.TargetMs == 2.5, "Quiet period did not recover low latency");
        for (var component = 0; component < 2; component++)
        {
            var misleading = new AdaptiveFramePolicy();
            misleading.Observe(0, F(0, true)); misleading.Observe(3, F(1, true)); misleading.Observe(6, F(2, true));
            misleading.Observe(9, new(true, 3000, 8880, 120, 0, 0, true,
                component == 0 ? 90 : 0, component == 1 ? 90 : 0));
            Require(misleading.TargetMs == 2.5, "PLC improvement masked worse underfill or discard in first trial window");
        }
        var legacy = new AdaptiveFramePolicy();
        legacy.Observe(0, F(0, true) with { UnderfillMs = null });
        legacy.Observe(3, F(1, true) with { UnderfillMs = null });
        legacy.Observe(6, F(2, true) with { UnderfillMs = null });
        Require(legacy.Changes == 0, "Legacy feedback enabled incomplete adaptation");
        p.Observe(40, F(0, true)); Require(p.TargetMs == 2.5, "Counter reset caused transition");
        p.Suspend(); p.Observe(50, F(100, true)); Require(p.TargetMs == 2.5, "Stale interval caused transition");
    }
    private delegate void CaptureBlock(ReadOnlySpan<byte> data, double? at, double callbackAt, int samples, int flags);
    private static async Task CheckCaptureBoundaryAsync()
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        // Exercise the actual capture assembler without opening physical devices.
        var source = new WindowsAudioDevice(encoder, disableSource: true, disableSink: true, experiment: options);
        var feed = typeof(WindowsAudioDevice).GetMethod("Capture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate<CaptureBlock>(source);
        var sizes = new List<int>();
        source.OnAudioSourceEncodedSample += (samples, payload) =>
        {
            Require(OpusPacketInfo.GetNumSamples(payload, 48000) == samples, "Capture length/encoded duration mismatch");
            sizes.Add((int)samples);
        };
        try
        {
            feed(new byte[120], null, 0, 60, 0);
            source.RequestFrameDuration(10);
            feed(new byte[120], null, 0, 60, 0);
            Require(sizes.Count == 1 && sizes[0] == 120, "Partial capture was resized or lost");
            feed(new byte[960], null, 0, 480, 0);
            source.RequestFrameDuration(5); feed(new byte[480], null, 0, 240, 0);
            source.RequestFrameDuration(2.5); feed(new byte[240], null, 0, 120, 0);
            Require(sizes.SequenceEqual(CaptureTransitions), "Runtime capture transitions");
        }
        finally { await source.Close(); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
