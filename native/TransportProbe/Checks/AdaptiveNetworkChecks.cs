using System.Text.Json;
using VoiceNative;

internal static class AdaptiveNetworkChecks
{
    private sealed record Scenario(string Name, string Impairment, int CaptureMs = 10);
    private sealed record Packet(uint Stamp, byte[] Payload);
    private sealed record Feedback(double At, ReceiverFrameFeedback Value);
    private sealed record Point(double AtMs, double FrameMs, double QueueMs, double PlcMs, double UnderfillMs,
        double DiscardMs, long Late, long Resyncs);
    private sealed record Transition(double AtMs, double FrameMs, string Reason);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] Sizes = [120, 240, 480];
    public static void Run(string path)
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payloads = Sizes.ToDictionary(n => n, n => encoder.EncodeAudio(
            Enumerable.Range(0, n).Select(i => (short)(8000 * Math.Sin(i * Math.PI / 24))).ToArray(), encoder.SupportedFormats.Single()));
        Scenario[] scenarios = [new("steady", "none"), new("forced-transitions", "none"),
            new("loss-1pct", "loss"), new("outage-400ms", "outage"), new("reorder-12ms", "reorder"),
            new("half-loss-7s", "half"), new("render-stall-200ms", "render"), new("capture-burst-20ms", "none", 20), new("capture-cadence-change", "cadence")];
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var scenario in scenarios)
        foreach (var mode in scenario.Name == "forced-transitions" ? new[] { "scripted" } : new[] { "fixed-2.5", "fixed-5", "auto" })
        {
            using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options);
            var policy = new AdaptiveFramePolicy();
            var pending = new PriorityQueue<Packet, (double At, long Order)>();
            var feedback = new Queue<Feedback>();
            var transitions = new List<Transition>();
            var points = new List<Point>();
            var output = new byte[960];
            long producedSamples = 0, order = 0, received = 0, injectedLoss = 0;
            double peakQueue = 0, frame = mode == "fixed-5" ? 5 : 2.5;
            Point? tailStart = null;
            Point Sample(double at)
            {
                var a = buffer.Snapshot(); var r = buffer.Details();
                return new(at, frame, a.QueueMs, r.ConcealedMs, a.UnderfillMs, r.RecoveryDiscardedMs, r.LatePackets, r.Resyncs);
            }
            for (var now = 0.0; now <= 120000; now += 2.5)
            {
                var captureMs = scenario.Impairment == "cadence" && now is >= 30000 and < 75000 ? 20 : scenario.CaptureMs;
                if (now % captureMs == 0)
                {
                    var target = mode == "auto" ? policy.TargetMs : frame;
                    if (scenario.Name == "forced-transitions") target = now < 30000 ? 2.5 : now < 60000 ? 5 : now < 90000 ? 10 : 5;
                    if (target != frame) { frame = target; transitions.Add(new(now, frame, policy.Reason)); }
                    var samples = (int)(frame * 48);
                    while (producedSamples + samples <= now * 48)
                    {
                        var stamp = unchecked(uint.MaxValue - 9600 + (uint)producedSamples);
                        producedSamples += samples; order++;
                        var active = now is >= 30000 and < 75000;
                        var lost = active && (scenario.Impairment == "loss" && order % 100 == 0
                            || scenario.Impairment == "half" && now is >= 60000 and < 67000 && order % 2 == 0);
                        if (lost) { injectedLoss++; continue; }
                        var arrival = now + 3;
                        if (active && scenario.Impairment == "outage" && (now is >= 40000 and < 40400 || now is >= 65000 and < 65400))
                            arrival = now < 50000 ? 40403 : 65403;
                        if (active && scenario.Impairment == "reorder" && order % 7 == 0) arrival += 12;
                        pending.Enqueue(new(stamp, payloads[samples]), (arrival, order));
                    }
                }
                while (pending.TryPeek(out _, out var priority) && priority.At <= now)
                {
                    var packet = pending.Dequeue(); buffer.Add(1, packet.Stamp, packet.Payload, priority.At); received++;
                    peakQueue = Math.Max(peakQueue, buffer.Snapshot().QueueMs);
                }
                var renderPaused = scenario.Impairment == "render" && now is >= 60000 and < 60200;
                if (now >= 40 && now % 10 == 0 && !renderPaused) buffer.Read(output);
                // Model 500ms telemetry polling and 100ms feedback delivery, not instantaneous oracle control.
                if (now % 500 == 0)
                {
                    var a = buffer.Snapshot(); var r = buffer.Details();
                    feedback.Enqueue(new(now + 100, new(true, received, a.DecodedMs, r.ConcealedMs,
                        r.LatePackets, r.SourceChanges, !renderPaused, a.UnderfillMs, r.RecoveryDiscardedMs)));
                    points.Add(Sample(now));
                }
                while (feedback.TryPeek(out var next) && next.At <= now)
                { feedback.Dequeue(); if (mode == "auto") policy.Observe(now / 1000, next.Value); }
                if (now == 100000) tailStart = Sample(now);
            }
            var last = Sample(120000); var detail = buffer.Details();
            var tailPlc = last.PlcMs - tailStart!.PlcMs;
            var tailUnderfill = last.UnderfillMs - tailStart.UnderfillMs;
            var tailDiscard = last.DiscardMs - tailStart.DiscardMs;
            if (detail.InvalidPackets != 0 || peakQueue > 120 || detail.DecodedPackets == 0)
                throw new InvalidOperationException($"Bounds/validity: {scenario.Name}/{mode}");
            if (tailPlc > 10 || tailUnderfill > 10 || tailDiscard > 20)
                failures.Add($"Recovery tail failed {scenario.Name}/{mode}: PLC={tailPlc}, underfill={tailUnderfill}, discard={tailDiscard}");
            if (scenario.Impairment == "none" && (last.PlcMs != 0 || last.UnderfillMs != 0))
                throw new InvalidOperationException($"Clean capture/transition produced gaps: {scenario.Name}/{mode}");
            results.Add(new { scenario.Name, mode, mediaSeconds = 120, received, injectedLoss, peakQueue,
                final = last, tailPlc, tailUnderfill, tailDiscard, transitions, points });
            Console.WriteLine($"PASS: {scenario.Name}/{mode}: PLC={last.PlcMs:F1}ms underfill={last.UnderfillMs:F1}ms discard={last.DiscardMs:F1}ms; final20s={tailPlc:F1}/{tailUnderfill:F1}/{tailDiscard:F1}ms");
        }
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!; Directory.CreateDirectory(parent);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1,
            failures, limits = "Deterministic synthetic packets/virtual clocks. Not a replay of second-level friend diagnostics; not acoustic/device/external network measurement.", results }, JsonOptions));
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("; ", failures));
    }
}
