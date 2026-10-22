using System.Buffers.Binary;
using System.Text.Json;

namespace VoiceNative;

internal static class ExtremeChecks
{
    private static void Require(bool ok, string detail) { if (!ok) throw new InvalidOperationException(detail); }
    public static void Run()
    {
        Require(!LatencyExperiment.FromName("opus-2.5")!.MinimumCapture, "Presets must retain the supported ~5ms capture floor; absolute minimum requires explicit opt-in.");
        foreach (var name in LatencyExperiment.Names.Skip(1))
        {
            var options = LatencyExperiment.FromName(name)!; options.Validate();
            PlayoutWarmup.Ensure(options);
            PlayoutWarmup.Ensure(options); // Idempotence; real buffer counters below must stay pristine.
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
            var format = encoder.SupportedFormats.Single();
            var frames = Enumerable.Range(0, 100).Select(f => Enumerable.Range(0, options.FrameSamples)
                .Select(i => (short)(8000 * Math.Sin((f * options.FrameSamples + i) * Math.PI * 880 / 48000))).ToArray()).ToArray();
            var payloads = frames.Select(p => encoder.EncodeAudio(p, format)).ToArray();
            if (options.Pcm)
                for (var i = 0; i < frames[2].Length; i++) Require(BinaryPrimitives.ReadInt16BigEndian(payloads[2].AsSpan(i * 2)) == frames[2][i], "L16 byte order");
            using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
            var output = new byte[options.FrameSamples * 2];
            var origin = uint.MaxValue - (uint)options.FrameSamples;
            uint Timestamp(int i) => unchecked(origin + (uint)(i * options.FrameSamples));
            var prime = (int)Math.Ceiling(options.ReserveMs / options.FrameMs) + 1;
            for (var i = prime - 1; i >= 0; i--) buffer.Add(1, Timestamp(i), payloads[i]);
            for (var i = 0; i < 100; i++) { if (i >= prime) buffer.Add(1, Timestamp(i), payloads[i]); buffer.Read(output); }
            var detail = buffer.Details();
            Require(detail.InvalidPackets == 0 && detail.DecodedPackets == 100 && detail.ConcealedMs == 0, name + " short-frame order/wrap decoding");
            Require(Math.Abs(detail.TargetReserveMs - options.ReserveMs) < .001, "Fractional reserve diagnostics");
            buffer.Read(output); var concealed = buffer.Details();
            Require(Math.Abs(concealed.ConcealedMs - options.FrameMs) < .001, "PLC must use actual packet quantum");
            for (var i = 0; i < 25; i++) buffer.Read(output);
            Require(buffer.Details().MaxConsecutivePlcMs <= options.PlcLimitMs, "Bounded concealment");
            for (var i = 200; i < 400; i++) buffer.Add(1, Timestamp(i), payloads[i % 100]);
            Require(buffer.Snapshot().QueueMs <= options.BacklogMs && buffer.Snapshot().Trims > 0, "Bounded backlog");
            buffer.Suspend(); buffer.Resume(); buffer.Add(2, Timestamp(0), payloads[0]);
            buffer.Read(new byte[480 * 2]);
            Require(buffer.Details().SourceChanges >= 1 && buffer.Details().InvalidPackets == 0, "Device/source reset");
            Console.WriteLine($"PASS: {name}, {options.FrameSamples} samples, reserve {detail.TargetReserveMs}ms; decode/order/wrap/PLC/backlog/reset.");
        }
        var queue = new AudioSendQueue(1);
        queue.Enqueue([1]); queue.Enqueue([2]); queue.Enqueue([3]);
        Require(queue.Snapshot().DroppedFrames == 2 && queue.Snapshot().PendingFrames == 1, "Latest-only queue");
        var telemetry = new TelemetryState();
        Require(telemetry.Receive(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new LatencyTelemetry(1, 1, 1, 0, 2.5, 3, 1, "재생", FrameMs: 2.5))), "Fractional telemetry");
        CheckMixedCadence();
        CheckEmptyHold();
        CheckMixedOpusDurations();
        CheckShortAdaptiveReserve();
        CheckCallbackStorage();
        CheckPrimingBoundary();
    }

    private static void CheckMixedCadence()
    {
        var options = LatencyExperiment.FromName("opus-2.5-r10")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: true, experiment: options);
        var available = 0; uint timestamp = 0;
        // 256-sample capture bursts and ~10.16ms render pulls are independent
        // clocks. Short codec packets must not imply equally short device pulls.
        for (var time = 0; time < 48000 * 3; time++)
        {
            if (time % 256 == 0)
            {
                available += 256;
                while (available >= 120) { buffer.Add(1, timestamp, payload); timestamp += 120; available -= 120; }
            }
            if (time % 488 == 0) buffer.Read(new byte[488 * 2]);
        }
        Require(buffer.Snapshot().UnderfillMs == 0 && buffer.Details().ConcealedMs == 0,
            "Supported capture cadence and 10ms reserve must bridge independent render pulls without loss.");
        Console.WriteLine("PASS: 5.33ms capture bursts / 10.17ms render pulls / 2.5ms packets, no underfill or PLC.");
    }

    private static void CheckEmptyHold()
    {
        var options = new LatencyExperiment(2.5, 0, EmptyHoldMs: 2.5);
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
        var output = new byte[240];
        buffer.Add(1, 0, payload); buffer.Read(output);
        buffer.Read(output); // Empty without evidence of a loss: retain timestamp 120.
        buffer.Add(1, 120, payload); buffer.Read(output);
        Require(buffer.Details() is { DecodedPackets: 2, LatePackets: 0, HeldMs: 2.5 }, "Short arrival-phase miss must preserve the delayed packet.");
        buffer.Add(1, 360, payload); buffer.Read(output); // Timestamp 240 is truly absent.
        Require(buffer.Details().HeldMs == 2.5 && buffer.Details().ConcealedMs == 5, "Known RTP gap must advance ordinary PLC, not hold.");
        buffer.Read(output);
        Require(buffer.Details().DecodedPackets == 3, "Future packet must follow one lost quantum.");
        for (var i = 0; i < 20; i++) buffer.Read(output);
        Require(buffer.Details().MaxConsecutivePlcMs <= options.PlcLimitMs && buffer.Snapshot().UnderfillMs > 0,
            "Empty hold must share the continuous PLC budget and eventually rebuffer.");
        Console.WriteLine("PASS: short empty hold preserves late-arriving RTP; real loss advances and continuous outage stays bounded.");
    }

    private static void CheckMixedOpusDurations()
    {
        var options = new LatencyExperiment(2.5, 10, EmptyHoldMs: 2.5, OutputHeadroomMs: 1);
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var shortPacket = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        var longPacket = encoder.EncodeAudio(new short[480], encoder.SupportedFormats.Single());
        using var normalReceiver = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false);
        for (var i = 0; i < 12; i++) normalReceiver.Add(1, (uint)i * 120, shortPacket);
        for (var i = 0; i < 3; i++) normalReceiver.Read(new byte[960]);
        Require(normalReceiver.Details() is { InvalidPackets: 0, DecodedPackets: 12, ConcealedMs: 0, PacketQuantumMs: 2.5 },
            "Production 10ms receiver must consume a short-frame Opus peer without rejecting its duration.");
        normalReceiver.Add(1, 1560, shortPacket); normalReceiver.Read(new byte[480]);
        Require(normalReceiver.Details().ConcealedMs == 2.5, "Mixed-mode loss must follow remote packet granularity.");
        using var shortReceiver = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
        shortReceiver.Add(2, 0, longPacket); shortReceiver.Add(2, 480, longPacket);
        shortReceiver.Read(new byte[960]); shortReceiver.Read(new byte[960]);
        Require(shortReceiver.Details() is { InvalidPackets: 0, DecodedPackets: 2, ConcealedMs: 0 },
            "Short-frame receiver must consume production 10ms Opus packets.");
        shortReceiver.Add(2, 1440, longPacket); shortReceiver.Read(new byte[960]); shortReceiver.Read(new byte[960]);
        Require(shortReceiver.Details() is { DecodedPackets: 3, ConcealedMs: 10, PacketQuantumMs: 10, LatePackets: 0 },
            "Missing production packet must advance one remote 10ms quantum, not a local 2.5ms quantum.");
        using var adaptiveReceiver = new RtpOpusPlayoutBuffer(new(), experiment: options with { ReserveMs = 5, AdaptiveShortReserve = true });
        adaptiveReceiver.Add(3, 0, longPacket); adaptiveReceiver.Add(3, 480, longPacket); adaptiveReceiver.Add(3, 960, longPacket);
        adaptiveReceiver.Read(new byte[960]); adaptiveReceiver.Read(new byte[960]); adaptiveReceiver.Read(new byte[960]); adaptiveReceiver.Read(new byte[960]);
        adaptiveReceiver.Add(3, 1440, longPacket); adaptiveReceiver.Read(new byte[960]);
        Require(adaptiveReceiver.Details() is { DecodedPackets: 4, LatePackets: 0, HeldMs: 10, MaxConsecutivePlcMs: 10, TargetReserveMs: 15 },
            "Mixed-mode adaptive hold must reserve and preserve exactly the remote packet duration within the PLC budget.");
        Console.WriteLine("PASS: production/short-frame Opus peers accept each other's packet durations and preserve loss accounting.");
    }

    private static void CheckShortAdaptiveReserve()
    {
        var reserve = new AdaptivePlayoutReserve(240, true, shortFrames: true);
        Require(reserve.TargetSamples == 240, "Adaptive short reserve must start at its low-latency floor.");
        for (var i = 0; i < 4; i++) Require(reserve.TryHold(), "Bounded short hold headroom");
        Require(reserve.TargetSamples == 720 && !reserve.TryHold(), "Short reserve must never exceed 15ms.");
        for (var i = 0; i < 5; i++) reserve.Advance(48000, false);
        Require(reserve.TargetSamples == 720, "Transient quiet must not prematurely shrink reserve.");
        reserve.Advance(48000, false);
        Require(reserve.TargetSamples == 672, "Quiet playout should shed one millisecond per second.");
        for (var i = 0; i < 9; i++) reserve.Advance(48000, false);
        Require(reserve.TargetSamples == 240, "Reserve must return to the 5ms floor.");
        reserve.Rebuffer(); Require(reserve.TargetSamples == 720, "Long gaps restart with bounded recovery reserve.");
        reserve.Reset(); Require(reserve.TargetSamples == 240, "Device/source reset must restart latency learning.");
        var fast = new AdaptivePlayoutReserve(240, true, shortFrames: true, shortQuietSeconds: 2);
        Require(fast.TryHold(), "Fast decay starts with bounded hold.");
        fast.Advance(48000, false);
        Require(fast.TargetSamples == 360, "One quiet second must not shrink fast reserve.");
        fast.Advance(48000, true); fast.Advance(48000, false);
        Require(fast.TargetSamples == 360, "Concealment resets the quiet interval.");
        fast.Advance(48000, false);
        Require(fast.TargetSamples == 312, "Two quiet seconds reduce only one millisecond.");
        fast.Late(); fast.Advance(48000, false);
        Require(fast.TargetSamples == 312, "Late arrivals reset fast decay too.");
        var recovery = new AdaptivePlayoutReserve(240, true, shortFrames: true, maximumShortSamples: 960);
        for (var i = 0; i < 6; i++) Require(recovery.TryHold(), "20ms recovery range");
        Require(recovery.TargetSamples == 960 && !recovery.TryHold(), "Recovery must never exceed the explicit 20ms ceiling.");
        var options = new LatencyExperiment(2.5, 5, EmptyHoldMs: 2.5, OutputHeadroomMs: 1, AdaptiveShortReserve: true);
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
        for (var i = 0; i < 6; i++) buffer.Add(1, (uint)i * 120, payload);
        var output = new byte[960]; buffer.Read(output);
        for (var i = 0; i < 10; i++) buffer.Read(output);
        Require(buffer.Details().TargetReserveMs == 15, "A prolonged gap should select bounded recovery reserve.");
        var before = buffer.Snapshot();
        for (var i = 6; i < 18; i++) buffer.Add(1, (uint)i * 120, payload);
        buffer.Read(output);
        Require(buffer.Snapshot().Trims == before.Trims && buffer.Snapshot().UnderfillMs == before.UnderfillMs,
            "Adaptive startup demand must fit the queue bound; retaining a stale floor must not cause perpetual trimming.");
        Console.WriteLine("PASS: short adaptive reserve stays within 5–15ms and relinquishes recovered latency after quiet playback.");
    }

    private static void CheckCallbackStorage()
    {
        static void Feed(AdaptivePlayoutReserve reserve, ProcessingWindow window)
        {
            for (var i = 0; i < 10000; i++)
            {
                reserve.Arrival(i * 120L, i * 2.5 + (i % 8 == 0 ? 1 : 0));
                window.Add(i);
            }
        }
        Feed(new(240, true, shortFrames: true), new()); // Warm JIT/runtime helpers off the measured path.
        var reserve = new AdaptivePlayoutReserve(240, true, shortFrames: true);
        var window = new ProcessingWindow();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Feed(reserve, window);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated <= 512 && Math.Abs(reserve.JitterP95Ms - 1) < .000001,
            "Arrival jitter and processing windows must preserve percentiles without growing/allocating in callbacks.");
        Console.WriteLine($"PASS: 10,000 arrival/window updates allocated {allocated} bytes after construction; jitter P95 remains 1ms.");
    }
    private static void CheckPrimingBoundary()
    {
        var options = LatencyExperiment.FromName("opus-adaptive")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(new short[120], encoder.SupportedFormats.Single());
        using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
        var signal = buffer.PrimingSignal;
        Require(!buffer.CanPrimeOutput(480) && !signal.WaitOne(0), "Empty media must not be primed.");
        for (uint i = 0; i < 5; i++) buffer.Add(1, i * 120, payload);
        Require(signal.WaitOne(0) && !buffer.CanPrimeOutput(480), "A wake is not proof that reserve plus first pull is ready.");
        buffer.Add(1, 600, payload);
        Require(signal.WaitOne(0) && buffer.CanPrimeOutput(480) && buffer.Details().DecodedPackets == 0,
            "Priming readiness must preserve compressed media and counters at the exact reserve/pull boundary.");
        buffer.Read(new byte[960]);
        Require(buffer.Details() is { DecodedPackets: 4, ConcealedMs: 0 } && buffer.Snapshot().QueueMs == 5,
            "First media pull must retain the same reserve without an added startup queue.");
        buffer.Suspend(); Require(!buffer.CanPrimeOutput(480) && !signal.WaitOne(0), "Suspend clears readiness and stale wakes.");
        Console.WriteLine("PASS: event-driven startup readiness preserves RTP and counters, exact reserve, and suspend lifecycle.");
    }
    public static async Task PrimingDeviceAsync(string output)
    {
        var results = new List<object>();
        foreach (var closeEarly in new[] { false, true })
        {
            var options = LatencyExperiment.FromName("opus-adaptive")!;
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
            var device = new WindowsAudioDevice(encoder, disableSource: true, useRtpPlayout: true,
                boundedRender: true, rawRender: true, renderCategory: NAudio.CoreAudioApi.AudioStreamCategory.GameChat, experiment: options);
            var at = ProbeClock.Now;
            try
            {
                var start = Task.Run(device.StartAudioSink);
                if (closeEarly)
                {
                    await Task.Delay(10);
                    await Task.WhenAll(start, device.Close()).WaitAsync(TimeSpan.FromSeconds(3));
                    results.Add(new { scenario = "close-during-prime", elapsedMs = (ProbeClock.Now - at) * 1000 });
                }
                else
                {
                    await start.WaitAsync(TimeSpan.FromSeconds(3));
                    await Task.Delay(50);
                    var audio = device.Snapshot();
                    Require(audio.Render?.Execution is { PrimedWithMedia: false, PrimingWaitMs: >= 80 and < 1500 }
                        && audio.DeviceError is null, "No-media startup must time out safely and play silence, not hang.");
                    results.Add(new { scenario = "no-media-timeout", elapsedMs = (ProbeClock.Now - at) * 1000, audio.Render });
                }
            }
            finally { await device.Close().WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { results, notes = "Real shared render, no microphone/network/media payload. Silence after bounded priming timeout; close during startup must complete." }, DiagnosticJson.Readable));
        Console.WriteLine("PASS: real shared render handles no-media timeout and close-during-priming without hanging.");
    }
}
