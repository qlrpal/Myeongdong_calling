using System.Text.Json;
using System.Security.Cryptography;
using SIPSorceryMedia.Abstractions;
using VoiceNative;

// Reuses recorded scheduling metadata with synthetic Opus. No devices or sockets.
internal static class RecordedReserveReplay
{
    private sealed record Arrival(double AtMs, uint Ssrc, uint Timestamp);
    private sealed record Pull(double AtMs, int Samples);
    private sealed record Timing(Arrival[] Arrivals, Pull[] Pulls, double EndMs);
    private sealed record Policy(string Name, int ReserveMs, bool Responsive);
    private static readonly Policy[] Policies = [new("fixed20", 20, false), new("fixed10", 10, false),
        new("responsive15", 15, true), new("responsive10-candidate", 10, true),
        new("responsive11-candidate", 11, true), new("responsive12-candidate", 12, true),
        new("responsive13-candidate", 13, true), new("responsive14-candidate", 14, true)];
    private static readonly double[] Phases = [0, 2.5, 5, 7.5];
    private static readonly string[] Disturbances = ["recorded", "burst30", "burst30-once", "loss10"];
    private sealed record Result(string Policy, double RenderPhaseMs, string Disturbance,
        double MeanQueueMs, double QueueP95Ms, double MinQueueMs, double MaxQueueMs,
        long ComparedSamples, long DecodedPackets, double ConcealedMs, double MaxConsecutivePlcMs,
        long Rebuffers, long LatePackets, long InvalidPackets, double RecoveryDiscardedMs,
        double UnderfillMs, long Trims, double FinalReserveMs);

    public static async Task RunAsync(string input, string output)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Replay output must not overwrite its input.");
        if (File.Exists(output)) throw new IOException("Replay output already exists; select a new file.");
        if (new FileInfo(input).Length > 100*1024*1024) throw new ArgumentException("Trace exceeds 100MiB.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(input));
        var root = document.RootElement;
        if (!root.GetProperty("completed").GetBoolean() || root.GetProperty("error").ValueKind != JsonValueKind.Null
            || root.GetProperty("stage").GetString() != "complete") throw new ArgumentException("Completed successful trial required.");
        var window = root.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("phase").GetString() == "baseline");
        if (window.TryGetProperty("completed", out var complete) && !complete.GetBoolean()) throw new ArgumentException("Incomplete baseline.");
        if (window.GetProperty("samples")[0].GetProperty("FrameMs").GetDouble() != 10)
            throw new ArgumentException("This comparison requires fixed 10ms source frames.");
        var trace = root.GetProperty("baselineTrace").Deserialize<PipelineTraceSnapshot>()
            ?? throw new ArgumentException("Detailed trace required.");
        if (trace.EvictedRecords != 0 || trace.Admissions.Length is < 100 or > 24000 || trace.Reads.Length is < 100 or > 24000)
            throw new ArgumentException("Missing/evicted/short trace.");
        if (trace.Admissions.Select(a => a.Peer).Distinct().Count() != 1
            || trace.Reads.Any(r => r.Peer != trace.Admissions[0].Peer)
            || trace.Admissions.Select(a => a.Ssrc).Distinct().Count() != 1
            || trace.Admissions.Any(a => a.Outcome is not ("queued" or "late" or "duplicate")
                || a.FrameSamples is not (null or 480))) throw new ArgumentException("Expected one valid 10ms RTP stream.");
        if (trace.Admissions.Select(a => a.LockAt).Intersect(trace.Reads.Select(r => r.LockAt)).Any())
            throw new ArgumentException("Recorded arrival/read order is ambiguous.");
        var origin = Math.Min(trace.Admissions.Min(a => a.LockAt), trace.Reads.Min(r => r.LockAt));
        var arrivals = trace.Admissions.Select(a => new Arrival((a.LockAt-origin)*1000, a.Ssrc, a.Timestamp)).ToArray();
        var pulls = trace.Reads.Select(r => new Pull((r.LockAt-origin)*1000, r.RequestedSamples)).ToArray();
        // Do not manufacture source starvation at the truncated recording tail.
        var timing = new Timing(arrivals, pulls, Math.Min(arrivals.Max(a => a.AtMs), pulls.Max(p => p.AtMs))-50);
        Validate(timing);
        var payload = Payload();
        var rows = (from phase in Phases
                    from disturbance in Disturbances
                    from policy in Policies select Evaluate(timing, payload, policy, phase, disturbance)).ToArray();
        var comparisons = rows.Where(r => r.Policy.EndsWith("-candidate", StringComparison.Ordinal)).Select(candidate =>
        {
            var control = rows.Single(r => r.Policy == "responsive15" && r.RenderPhaseMs == candidate.RenderPhaseMs && r.Disturbance == candidate.Disturbance);
            return new { candidate.Policy, candidate.RenderPhaseMs, candidate.Disturbance,
                queueMeanReductionMs = control.MeanQueueMs-candidate.MeanQueueMs,
                queueP95ReductionMs = control.QueueP95Ms-candidate.QueueP95Ms,
                continuityNotWorse = candidate.ConcealedMs <= control.ConcealedMs
                    && candidate.MaxConsecutivePlcMs <= control.MaxConsecutivePlcMs
                    && candidate.Rebuffers <= control.Rebuffers && candidate.LatePackets <= control.LatePackets
                    && candidate.RecoveryDiscardedMs <= control.RecoveryDiscardedMs
                    && candidate.UnderfillMs <= control.UnderfillMs && candidate.Trims <= control.Trims };
        }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { schemaVersion = 1,
            appHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WebRtcCallEngine).Assembly.Location))),
            probeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RecordedReserveReplay).Assembly.Location))),
            inputHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(input))),
            timing.EndMs, arrivals = arrivals.Length, pulls = pulls.Length, rows, comparisons,
            limits = "Recorded admission/read timings; all variants restart empty with identical synthetic 10ms Opus. " +
                "Not reconstruction of original PCM, warm decoder/queue/clock state, SRTP, CPU contention or device output. " +
                "Queue mean is sample-weighted and P95 is per pull after max(10s, half recording); quality counters cover the whole replay. " +
                "Render phase offsets, repeated/one-time 30ms arrival batching and one omitted 10ms packet are synthetic sensitivity tests. " +
                "A captured render pause is a delayed pull, not simulated hardware underflow. No mouth-to-ear or game claim." }, DiagnosticJson.Readable));
        Console.WriteLine($"PASS: {rows.Length} deterministic reserve comparisons written; candidate failures remain in results.");
    }

    private static void Validate(Timing timing)
    {
        if (!double.IsFinite(timing.EndMs) || timing.EndMs < 20000
            || timing.Arrivals.Any(a => !double.IsFinite(a.AtMs) || a.AtMs < 0)
            || timing.Pulls.Any(p => !double.IsFinite(p.AtMs) || p.AtMs < 0 || p.Samples is < 1 or > 5760))
            throw new ArgumentException("Invalid replay timing/size.");
    }
    private static byte[] Payload()
    {
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        return encoder.EncodeAudio(Enumerable.Range(0, 480).Select(i => (short)(5000*Math.Sin(i*.031))).ToArray(),
            new AudioFormat(AudioCodecsEnum.OPUS, 111, 48000, 1));
    }
    private static Result Evaluate(Timing timing, byte[] payload, Policy policy, double phase, string disturbance)
    {
        using var buffer = new RtpOpusPlayoutBuffer(new(), targetReserveMs: policy.ReserveMs,
            adaptiveReserve: false, fixedReserveClockTarget: true, transientEmptyQueueHold: true,
            freshRestart: true, responsiveReserve: policy.Responsive,
            recoveryReserveMs: policy.Responsive ? 10 : 0, repeatedGapRecovery: policy.Responsive);
        double Batched(double at) => disturbance == "burst30-once" && at is >= 20000 and < 20030 ? 20030
            : disturbance == "burst30" && at >= 5000 && at % 5000 < 30 ? Math.Floor(at/5000)*5000+30 : at;
        var omitted = disturbance == "loss10" ? Array.FindIndex(timing.Arrivals, a => a.AtMs >= 20000) : -1;
        var arrivals = timing.Arrivals.Where((_, i) => i != omitted)
            .Select(a => a with { AtMs = Batched(a.AtMs) }).OrderBy(a => a.AtMs).ToArray();
        var index = 0; var output = new byte[timing.Pulls.Max(p => p.Samples)*2];
        var queues = new List<double>(); double weighted = 0; long comparedSamples = 0;
        foreach (var pull in timing.Pulls.OrderBy(p => p.AtMs))
        {
            var now = pull.AtMs+phase;
            if (now > timing.EndMs) break;
            while (index < arrivals.Length && arrivals[index].AtMs <= now)
            { var a = arrivals[index++]; buffer.Add(a.Ssrc, a.Timestamp, payload, a.AtMs); }
            buffer.Read(output.AsSpan(0, pull.Samples*2));
            if (now < Math.Max(10000, timing.EndMs/2)) continue;
            var q = buffer.Snapshot().QueueMs;
            if (!double.IsFinite(q) || q < 0) throw new InvalidOperationException("Invalid queue.");
            queues.Add(q); weighted += q*pull.Samples; comparedSamples += pull.Samples;
        }
        queues.Sort(); var s = buffer.Snapshot(); var d = buffer.Details();
        if (comparedSamples == 0 || d.InvalidPackets != 0 || d.DecodedPackets < 100)
            throw new InvalidOperationException("Replay made insufficient/invalid progress.");
        return new(policy.Name, phase, disturbance, weighted/comparedSamples,
            queues[(int)Math.Ceiling(queues.Count*.95)-1], queues[0], queues[^1], comparedSamples,
            d.DecodedPackets, d.ConcealedMs, d.MaxConsecutivePlcMs, d.Rebuffers, d.LatePackets,
            d.InvalidPackets, d.RecoveryDiscardedMs, s.UnderfillMs, s.Trims, d.TargetReserveMs);
    }

    public static void Check()
    {
        var payload = Payload();
        foreach (var read in new[] { 128, 480 })
        foreach (var phase in new[] { 0d, 5 })
        foreach (var policy in Policies)
        {
            var timing = new Timing(Enumerable.Range(0, 2500).Select(i => new Arrival(i*10, 7, unchecked(uint.MaxValue-959+(uint)i*480))).ToArray(),
                Enumerable.Range(0, (int)(25000/(read/48d))).Select(i => new Pull(i*read/48d, read)).ToArray(), 24900);
            var row = Evaluate(timing, payload, policy, phase, "recorded");
            if (row.DecodedPackets < 2400 || row.ConcealedMs != 0 || row.UnderfillMs != 0 || row.Rebuffers != 0 || row.Trims != 0)
                throw new InvalidOperationException("Regular replay manufactured missing audio.");
        }
        try { Validate(new([], [new(double.NaN, 480)], 25000)); throw new InvalidOperationException("Bad clock accepted."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: deterministic source/render phase, RTP wrap and replay timing guards.");
    }
}
