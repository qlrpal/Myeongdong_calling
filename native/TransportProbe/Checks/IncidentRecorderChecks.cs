using System.Diagnostics;
using VoiceNative;

internal static class IncidentRecorderChecks
{
    private static readonly string[] ExpectedStages = ["warmup", "before", "fault", "after", "boundary"];
    public static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var recorder = new IncidentRecorder(8);
        recorder.Write(new(0, "ignored"), "ignored");
        Require(recorder.Snapshot(0).Records.Length == 0, "Stopped recorder accepted data");
        recorder.Reset(0, armTriggers: false);
        recorder.Write(new(100, "capture-startup"), "capture-gap");
        Require(recorder.Snapshot(100) is { Trigger: null, TriggerArmedAt: null, Records.Length: 1 },
            "Pre-media capture startup froze the incident");
        recorder.ArmTriggers(101); recorder.ArmTriggers(500);
        recorder.Write(new(102, "media-warmup"), "capture-gap");
        Require(recorder.Snapshot(102).Trigger is null, "Media warmup did not suppress startup");
        recorder.Write(new(103, "media-fault"), "receive-gap");
        Require(recorder.Snapshot(103) is { Trigger: "receive-gap", TriggerArmedAt: 101 },
            "First media arm was postponed by later packets or failed to trigger");
        recorder.Reset(0);
        recorder.Write(new(1, "warmup"), "startup");
        Require(recorder.Snapshot(1).Trigger is null, "Warmup triggered an incident");
        recorder.Write(new(2, "before"));
        recorder.Write(new(3, "fault"), "send-overflow");
        recorder.Write(new(4, "after"), "second-cause");
        recorder.Write(new(5, "boundary"));
        recorder.Write(new(5.01, "too-late"));
        var frozen = recorder.Snapshot(6);
        Require(frozen.State == "Frozen" && frozen.Trigger == "send-overflow" && frozen.TriggerAt == 3,
            "Incident cause/deadline changed");
        Require(frozen.Records.Select(x => x.Stage).SequenceEqual(ExpectedStages),
            "Lost before/after metadata or accepted late data");
        recorder.Stop();
        Require(recorder.Snapshot(6).Trigger == "send-overflow", "Stop erased incident evidence");
        recorder.Reset(10);
        for (var i = 0; i < 20; i++) recorder.Write(new(10 + i * .01, "wrap", Sequence: i));
        var wrapped = recorder.Snapshot(11);
        Require(wrapped.Records.Length == 8 && wrapped.Overwritten == 12 && wrapped.Records[0].Sequence == 12
            && wrapped.Trigger is null, "Reset/wrap bounds incorrect");
        using (var held = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var holder = Task.Run(() => { lock (recorder.SyncForChecks) { held.Set(); release.Wait(); } });
            Require(held.Wait(TimeSpan.FromSeconds(5)), "Contention fixture did not start");
            try { recorder.Write(new(11, "contended")); }
            finally { release.Set(); holder.GetAwaiter().GetResult(); }
            Require(recorder.Snapshot(11).SkippedContention == 1, "Realtime contention was not counted");
        }
        recorder.Reset(0);
        for (var i = 0; i < 1000; i++) recorder.Write(new(1, "steady"));
        var allocation = GC.GetAllocatedBytesForCurrentThread();
        var began = Stopwatch.GetTimestamp();
        for (var i = 0; i < 100000; i++) recorder.Write(new(1, "steady"));
        var elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocation;
        Require(allocated == 0, "Steady incident writes allocated managed memory");
        Console.WriteLine($"PASS: incident warmup/first trigger/pre/post/freeze/reset/bounds/nonblocking contention; 100000 writes {elapsed:F2}ms, {allocated} bytes (microbenchmark, not audio latency).");

        long ticks = 0;
        recorder.Reset(0);
        var queue = new AudioSendQueue(1, timestamp: () => ticks, incidents: recorder);
        queue.Enqueue([1], samples: 480);
        ticks = 3 * Stopwatch.Frequency;
        queue.Enqueue([2], samples: 240);
        var overflow = recorder.Snapshot(3);
        Require(overflow.Trigger == "send-overflow" && overflow.Records.Any(x => x.Stage == "send-drop"
            && x.Sequence == 1 && x.Samples == 480 && x.SampleOffset == 0 && x.Outcome == "capacity"),
            "Queue overflow lost frame identity");
        Require(queue.TryRead(out var retained) && retained.Sequence == 2 && retained.SampleOffset == 480,
            "Incident recording changed retained RTP timeline");
        var crowded = new IncidentRecorder(4); crowded.Reset(0);
        for (var i = 0; i < 10; i++) crowded.Write(new(2, "before", Sequence: i));
        crowded.Write(new(3, "trigger"), "fault");
        for (var i = 0; i < 10; i++) crowded.Write(new(3.1, "after", Sequence: i));
        var capped = crowded.Snapshot(3.2);
        Require(capped.EndReason == "capacity-limit" && capped.Records.Length == 4
            && capped.Records.Any(x => x.Stage == "trigger") && capped.Records[0].Stage == "before",
            "High event volume erased trigger/prehistory");
        ComparePlayout();
        CheckShortPlcCapture();
    }

    private static void CheckShortPlcCapture()
    {
        using var encoder = new SIPSorcery.Media.AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS);
        var payload = encoder.EncodeAudio(Enumerable.Range(0, 480).Select(i => (short)(6000 * Math.Sin(i * .05))).ToArray(), format);
        byte[]? reference = null;
        foreach (var enabled in new[] { false, true, false })
        {
            var trace = new PipelineTrace(); var now = ProbeClock.Now;
            if (enabled) trace.Incidents.Reset(now - 3);
            using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false,
                transientEmptyQueueHold: true, pipeline: trace, tracePeer: "fixture");
            const uint basis = uint.MaxValue - 959;
            for (var i = 0; i < 3; i++) buffer.Add(7, unchecked(basis + (uint)i * 480), payload);
            var output = new byte[8 * 480];
            for (var i = 0; i < 6; i++) buffer.Read(output.AsSpan(i * 480, 480));
            if (buffer.Details().ConcealedMs != 0) throw new InvalidOperationException("PLC fixture starved early.");
            trace.Incidents.Write(new(ProbeClock.Now, "before-conceal"));
            buffer.Read(output.AsSpan(6 * 480, 480)); // 240 samples/5ms held timestamp.
            if (buffer.Details().ConcealedMs != 5) throw new InvalidOperationException("Short PLC duration changed.");
            if (enabled)
            {
                var snapshot = trace.Incidents.Snapshot(ProbeClock.Now);
                if (snapshot.Trigger != "playout-plc" || snapshot.State != "AfterTrigger"
                    || !snapshot.Records.Any(x => x.Stage == "before-conceal")
                    || !snapshot.Records.Any(x => x.Stage == "playout-conceal" && x.Ssrc == 7
                        && x.RtpTimestamp == unchecked(basis + 1440) && x.Samples == 240
                        && x.DurationMs == 5 && x.Outcome == "hold-timestamp"))
                    throw new InvalidOperationException("Short PLC capsule lost cause/prehistory/original RTP position.");
            }
            buffer.Add(7, unchecked(basis + 1920), payload); // A queued future packet exposes an RTP hole.
            buffer.Read(output.AsSpan(7 * 480, 480));
            if (reference is null) reference = output;
            else if (!reference.AsSpan().SequenceEqual(output)) throw new InvalidOperationException("PLC recording changed output PCM.");
            if (enabled)
            {
                var frozen = trace.Incidents.Snapshot(ProbeClock.Now + 3);
                if (frozen.State != "Frozen" || frozen.Trigger != "playout-plc"
                    || !frozen.Records.Any(x => x.Stage == "playout-conceal" && x.Outcome == "advance-timestamp"
                        && x.RtpTimestamp == unchecked(basis + 1440)))
                    throw new InvalidOperationException("PLC aftermath or bounded freeze was lost.");
            }
        }
        Console.WriteLine("PASS: short 5ms PLC triggers bounded pre/post capture; held/advanced RTP positions including wrap and PCM off/on/off match.");
    }

    private static void ComparePlayout()
    {
        using var encoder = new SIPSorcery.Media.AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS);
        var payload = encoder.EncodeAudio(Enumerable.Range(0, 480)
            .Select(i => (short)(3000 * Math.Sin(i * 2 * Math.PI * 440 / 48000))).ToArray(), format);
        long? reference = null;
        foreach (var enabled in new[] { false, true, false })
        {
            var trace = new PipelineTrace();
            if (enabled) trace.Incidents.Reset(ProbeClock.Now);
            using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false, pipeline: trace);
            var output = new byte[960];
            for (var i = 0; i < 3; i++) buffer.Add(7, (uint)(i * 480), payload, i * 10);
            for (var i = 3; i < 103; i++) { buffer.Add(7, (uint)(i * 480), payload, i * 10); buffer.Read(output); }
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var began = Stopwatch.GetTimestamp();
            long checksum = 0;
            for (var i = 103; i < 10103; i++)
            {
                buffer.Add(7, (uint)(i * 480), payload, i * 10); buffer.Read(output);
                foreach (var value in output) checksum += value;
            }
            var elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            reference ??= checksum;
            if (reference != checksum || buffer.Snapshot().UnderfillMs != 0)
                throw new InvalidOperationException("Automatic incident recorder changed steady decoded audio/continuity.");
            Console.WriteLine($"Incident playout A/B/A enabled={enabled}: 10000 Opus admission/read pairs {elapsed:F2}ms, {allocated} bytes, checksum={checksum}. Synthetic single-thread throughput; not device/game timing.");
        }
    }
}
