using System.Text.Json;
using VoiceNative;

internal static class AdaptiveNetworkChecks
{
    private sealed record Scenario(string Name, string Impairment, double CaptureMs = 10, int ReadSamples = 480, bool AlternatingRead = false, int DriftPpm = 0);
    private sealed record Packet(uint Stamp, byte[] Payload);
    private sealed record Feedback(double At, ReceiverFrameFeedback Value);
    private sealed record Point(double AtMs, double FrameMs, double QueueMs, double PlcMs, double UnderfillMs,
        double DiscardMs, long Late, long Resyncs, double RatePpm);
    private sealed record Transition(double AtMs, double FrameMs, string Reason);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] Sizes = [120, 240, 480];
    public static void Run(string path, int seconds = 120, bool fineClock = true, bool initialLearning = false, bool includeFractional = false)
    {
        if (seconds is < 120 or > 1800) throw new ArgumentOutOfRangeException(nameof(seconds));
        var durationMs = seconds * 1000.0; var tailAt = durationMs - 20000;
        var options = LatencyExperiment.FromName("opus-auto-frames")! with { FineClock = fineClock, InitialReserveLearning = initialLearning };
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payloads = Sizes.ToDictionary(n => n, n => encoder.EncodeAudio(
            Enumerable.Range(0, n).Select(i => (short)(8000 * Math.Sin(i * Math.PI / 24))).ToArray(), encoder.SupportedFormats.Single()));
        Scenario[] scenarios = [new("steady", "none"), new("forced-transitions", "none"), new("forced-transitions-488", "none", ReadSamples: 488),
            new("loss-1pct", "loss"), new("outage-400ms", "outage"), new("reorder-12ms", "reorder"),
            new("half-loss-7s", "half"), new("render-stall-200ms", "render"), new("capture-burst-20ms", "none", 20), new("capture-cadence-change", "cadence"), new("render-488", "none", ReadSamples: 488),
            new("render-10-20ms", "none", AlternatingRead: true),
            new("loss30-reorder-488", "combined", ReadSamples: 488),
            new("loss30-reorder-10-20ms", "combined", AlternatingRead: true),
            new("clock-fast-200ppm-488", "drift", ReadSamples: 488, DriftPpm: 200),
            new("clock-slow-200ppm-488", "drift", ReadSamples: 488, DriftPpm: -200),
            new("clock-fast-500ppm-variable", "drift", AlternatingRead: true, DriftPpm: 500),
            new("clock-slow-500ppm-variable", "drift", AlternatingRead: true, DriftPpm: -500)];
        if(includeFractional) scenarios=[..scenarios,
            new("render-128", "none", ReadSamples:128), new("render-256", "none", ReadSamples:256),
            new("capture20-render-128", "none", CaptureMs:20, ReadSamples:128),
            new("capture20-render-256", "none", CaptureMs:20, ReadSamples:256),
            new("capture256-render128", "none", CaptureMs:256.0/48, ReadSamples:128),
            new("capture256-render480", "none", CaptureMs:256.0/48, ReadSamples:480),
            new("capture256-render256", "none", CaptureMs:256.0/48, ReadSamples:256),
            new("fractional-clock-fast-200ppm", "drift", ReadSamples:128, DriftPpm:200),
            new("fractional-clock-slow-200ppm", "drift", ReadSamples:128, DriftPpm:-200),
            new("capture20-clock-fast-200ppm", "drift", CaptureMs:20, ReadSamples:256, DriftPpm:200),
            new("capture20-clock-slow-200ppm", "drift", CaptureMs:20, ReadSamples:256, DriftPpm:-200)];
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var scenario in scenarios)
        foreach (var mode in scenario.Name.StartsWith("forced-transitions", StringComparison.Ordinal) ? new[] { "scripted" } : new[] { "fixed-2.5", "fixed-5", "auto" })
        {
            using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
            var policy = new AdaptiveFramePolicy();
            var pending = new PriorityQueue<Packet, (double At, long Order)>();
            var feedback = new Queue<Feedback>();
            var transitions = new List<Transition>();
            var points = new List<Point>();
            var output = new byte[1920];
            var nextRender = 40.0; var renderIndex = 0;
            var nextCapture = 0.0; var nextTelemetry = 0.0;
            long producedSamples = 0, order = 0, received = 0, injectedLoss = 0;
            double peakQueue = 0, frame = mode == "fixed-5" ? 5 : 2.5;
            Point? tailStart = null; Point? warmStart = null;
            double tailQueueWeighted = 0, tailQueueMinimum = double.PositiveInfinity, tailQueueMaximum = 0;
            long tailOutputSamples = 0, tailRenderRequests = 0;
            Point Sample(double at)
            {
                var a = buffer.Snapshot(); var r = buffer.Details();
                return new(at, frame, a.QueueMs, r.ConcealedMs, a.UnderfillMs, r.RecoveryDiscardedMs, r.LatePackets, r.Resyncs, r.RateCorrectionPpm);
            }
            for (var now = 0.0; now <= durationMs;)
            {
                var captureMs = scenario.Impairment == "cadence" && now is >= 30000 and < 75000 ? 20 : scenario.CaptureMs;
                if (now >= nextCapture)
                {
                    nextCapture = now + captureMs / (1 + scenario.DriftPpm / 1000000.0);
                    var target = mode == "auto" ? policy.TargetMs : frame;
                    if (scenario.Name.StartsWith("forced-transitions", StringComparison.Ordinal)) target = now < 30000 ? 2.5 : now < 60000 ? 5 : now < 90000 ? 10 : 5;
                    if (target != frame) { frame = target; transitions.Add(new(now, frame, policy.Reason)); }
                    var samples = (int)(frame * 48);
                    while (producedSamples + samples <= now * 48 * (1 + scenario.DriftPpm / 1000000.0) + .000001)
                    {
                        var stamp = unchecked(uint.MaxValue - 9600 + (uint)producedSamples);
                        producedSamples += samples; order++;
                        var active = now is >= 30000 and < 75000;
                        var lost = active && (scenario.Impairment == "loss" && order % 100 == 0
                            || scenario.Impairment == "combined" && now is >= 45000 and < 52000 && order % 10 < 3
                            || scenario.Impairment == "half" && now is >= 60000 and < 67000 && order % 2 == 0);
                        if (lost) { injectedLoss++; continue; }
                        var arrival = now + 3;
                        if (active && scenario.Impairment == "outage" && (now is >= 40000 and < 40400 || now is >= 65000 and < 65400))
                            arrival = now < 50000 ? 40403 : 65403;
                        if (active && scenario.Impairment is "reorder" or "combined" && order % 7 == 0) arrival += 12;
                        pending.Enqueue(new(stamp, payloads[samples]), (arrival, order));
                    }
                }
                while (pending.TryPeek(out _, out var priority) && priority.At <= now)
                {
                    var packet = pending.Dequeue(); buffer.Add(1, packet.Stamp, packet.Payload, priority.At); received++;
                    peakQueue = Math.Max(peakQueue, buffer.Snapshot().QueueMs);
                }
                var renderPaused = scenario.Impairment == "render" && now is >= 60000 and < 60200;
                if (now >= nextRender)
                {
                    var readSamples = scenario.AlternatingRead && renderIndex % 2 == 1 ? 960 : scenario.ReadSamples;
                    if (!renderPaused)
                    {
                        buffer.Read(output.AsSpan(0, readSamples * 2));
                        if (now >= tailAt)
                        {
                            var queue = buffer.Snapshot().QueueMs;
                            tailQueueWeighted += queue * readSamples; tailOutputSamples += readSamples; tailRenderRequests++;
                            tailQueueMinimum = Math.Min(tailQueueMinimum, queue); tailQueueMaximum = Math.Max(tailQueueMaximum, queue);
                        }
                    }
                    nextRender += readSamples / 48.0; renderIndex++;
                }
                // Model 500ms telemetry polling and 100ms feedback delivery, not instantaneous oracle control.
                if (now >= nextTelemetry)
                {
                    nextTelemetry += 500;
                    var a = buffer.Snapshot(); var r = buffer.Details();
                    feedback.Enqueue(new(now + 100, new(true, received, a.DecodedMs, r.ConcealedMs,
                        r.LatePackets, r.SourceChanges, !renderPaused, a.UnderfillMs, r.RecoveryDiscardedMs)));
                    points.Add(Sample(now));
                }
                while (feedback.TryPeek(out var next) && next.At <= now)
                { feedback.Dequeue(); if (mode == "auto") policy.Observe(now / 1000, next.Value); }
                if (now == 5000) warmStart = Sample(now);
                if (now == tailAt) tailStart = Sample(now);
                if (now == durationMs) break;
                var nextEvent = Math.Min(durationMs, Math.Min(nextCapture, Math.Min(nextRender, nextTelemetry)));
                if (pending.TryPeek(out _, out var nextPacket)) nextEvent = Math.Min(nextEvent, nextPacket.At);
                if (feedback.TryPeek(out var nextReport)) nextEvent = Math.Min(nextEvent, nextReport.At);
                if (now < 5000) nextEvent = Math.Min(nextEvent, 5000);
                if (now < tailAt) nextEvent = Math.Min(nextEvent, tailAt);
                if (nextEvent <= now) throw new InvalidOperationException("Virtual event clock stalled");
                now = nextEvent;
            }
            var last = Sample(durationMs); var detail = buffer.Details();
            var tailPlc = last.PlcMs - tailStart!.PlcMs;
            var tailUnderfill = last.UnderfillMs - tailStart.UnderfillMs;
            var tailDiscard = last.DiscardMs - tailStart.DiscardMs;
            if (detail.InvalidPackets != 0 || peakQueue > 120 || detail.DecodedPackets == 0)
                throw new InvalidOperationException($"Bounds/validity: {scenario.Name}/{mode}");
            if (tailPlc > 10 || tailUnderfill > 10 || tailDiscard > 20)
                failures.Add($"Recovery tail failed {scenario.Name}/{mode}: PLC={tailPlc}, underfill={tailUnderfill}, discard={tailDiscard}");
            // New output geometries may need one pull to learn demand; qualify
            // bounded startup separately and forbid ongoing gaps after 5s.
            var learnedOutput = scenario.ReadSamples != 480 || scenario.AlternatingRead;
            var cleanPlc = last.PlcMs - (learnedOutput ? warmStart!.PlcMs : 0);
            var cleanUnderfill = last.UnderfillMs - (learnedOutput ? warmStart!.UnderfillMs : 0);
            if (scenario.Impairment == "none" && (cleanPlc != 0 || cleanUnderfill != 0
                || learnedOutput && (warmStart!.PlcMs > 20 || warmStart.UnderfillMs > 20)))
                failures.Add($"Clean capture/transition produced gaps: {scenario.Name}/{mode}; PLC={last.PlcMs}, underfill={last.UnderfillMs}");
            results.Add(new { scenario.Name, mode, mediaSeconds = seconds, received, injectedLoss, peakQueue,
                learningState = detail.ReserveLearningState, relearns = detail.ReserveRelearns, final = last, warmStart, tailPlc, tailUnderfill, tailDiscard,
                tailQueueAfterReadMs = tailOutputSamples > 0 ? tailQueueWeighted / tailOutputSamples : (double?)null,
                tailQueueMinimum, tailQueueMaximum, tailRenderRequests, transitions, points });
            Console.WriteLine($"CHECK: {scenario.Name}/{mode}: PLC={last.PlcMs:F1}ms underfill={last.UnderfillMs:F1}ms discard={last.DiscardMs:F1}ms; final20s={tailPlc:F1}/{tailUnderfill:F1}/{tailDiscard:F1}ms");
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(parent);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 2, fineClock, initialLearning, includeFractional,
            failures, limits = "Deterministic synthetic packets/virtual clocks. Not a replay of second-level friend diagnostics; not acoustic/device/external network measurement.", results }, JsonOptions));
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }
}
