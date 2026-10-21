using System.Text.Json;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace VoiceNative;

internal static class PlayoutReplay
{
    private sealed record Event(double At, int Index, bool Read);
    private sealed record Mode(string Name, bool Fixed, bool Adaptive, bool TransientHold = false, bool FreshRestart = false, bool FreshInitialStart = false, int ReserveMs = 20, bool Responsive = false, int RecoveryReserveMs = 0, bool Contiguous = false, bool Repeated = false);
    private sealed record Point(double At, double QueueMs, long Plc, long Late, long Rebuffers, double UnderfillMs);
    private static object Summary(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        double? At(double q) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * q) - 1];
        return new { count = sorted.Length, p50 = At(.5), p95 = At(.95), p99 = At(.99), max = At(1) };
    }

    public static void Run(string input, string output, bool compareReserve = false, bool compareRecovery = false, bool compareRestart = false, bool comparePressure = false)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Replay output must not overwrite its input.");
        if (new FileInfo(input).Length > 100 * 1024 * 1024) throw new ArgumentException("Trace exceeds 100MiB.");
        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var root = document.RootElement;
        var samples = root.GetProperty("samples").EnumerateArray().ToArray();
        var error = root.GetProperty("error").GetString();
        var span = samples.Length > 0 ? samples[^1].GetProperty("MonotonicSeconds").GetDouble() - samples[0].GetProperty("MonotonicSeconds").GetDouble() : 0;
        var boundary = error?.StartsWith("System.InvalidOperationException: Peer disconnected during pipeline trace.", StringComparison.Ordinal) == true && span >= 118;
        if (error is not null && !boundary) throw new ArgumentException("Incomplete trace.");
        if (samples.Length == 0 || samples.Any(s => s.GetProperty("FrameMs").GetInt32() != 10))
            throw new ArgumentException("Replay requires an explicitly recorded 10ms codec configuration.");
        var trace = root.GetProperty("trace").Deserialize<PipelineTraceSnapshot>() ?? throw new ArgumentException("Missing trace.");
        if (trace.EvictedRecords != 0 || trace.Admissions.Length is < 1 or > 24000 || trace.Reads.Length is < 1 or > 24000)
            throw new ArgumentException("Missing, evicted or oversized trace.");
        if (trace.Admissions.Select(a => a.Peer).Distinct().Count() != 1 || trace.Reads.Select(r => r.Peer).Distinct().Count() != 1 || trace.Reads[0].Peer != trace.Admissions[0].Peer)
            throw new ArgumentException("Replay requires one receiver peer.");
        if (trace.Admissions.Any(a => a.Outcome is not ("queued" or "late" or "duplicate")))
            throw new ArgumentException("Unsupported admission outcome; synthetic payload cannot reproduce invalid input.");
        var events = trace.Admissions.Select((a, i) => new Event(a.LockAt, i, false))
            .Concat(trace.Reads.Select((r, i) => new Event(r.LockAt, i, true))).OrderBy(e => e.At).ThenBy(e => e.Read).ToArray();
        if (events.Any(e => !double.IsFinite(e.At)) || trace.Reads.Any(r => r.RequestedSamples is < 1 or > 5760))
            throw new ArgumentException("Invalid time or render request.");
        if (events.GroupBy(e => e.At).Any(g => g.Select(e => e.Read).Distinct().Count() > 1))
            throw new ArgumentException("Arrival/render ordering is ambiguous at the recorded clock resolution.");
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var payload = encoder.EncodeAudio(new short[480], format);
        var results = new List<object>();
        Mode[] modes = comparePressure
            ? [new("responsive", true, false, true, true, ReserveMs: 15, Responsive: true), new("pressure-restart10", true, false, true, true, ReserveMs: 15, Responsive: true, RecoveryReserveMs: 10, Repeated: true), new("pressure-restart10-tail", true, false, true, true, ReserveMs: 15, Responsive: true, RecoveryReserveMs: 10, Repeated: true, Contiguous: true)]
            : compareRestart
            ? [new("current20", true, false, true, true), new("restart10", true, false, true, true, RecoveryReserveMs: 10), new("restart15", true, false, true, true, RecoveryReserveMs: 15),
               new("responsive", true, false, true, true, ReserveMs: 15, Responsive: true), new("responsive-restart10", true, false, true, true, ReserveMs: 15, Responsive: true, RecoveryReserveMs: 10), new("responsive-restart15", true, false, true, true, ReserveMs: 15, Responsive: true, RecoveryReserveMs: 15),
               new("responsive-restart10-tail", true, false, true, true, ReserveMs: 15, Responsive: true, RecoveryReserveMs: 10, Contiguous: true)]
            : compareRecovery
            ? [new("current20", true, false, true, true), new("responsive15-30", true, false, true, true, ReserveMs: 15, Responsive: true)]
            : compareReserve
            ? [new("reserve20", true, false, true, true), new("reserve10", true, false, true, true, ReserveMs: 10)]
            : [new("learned-fixed-reserve", false, false), new("fixed-target", true, false), new("fixed-target-adaptive-reserve", true, true), new("fixed-target-transient-hold", true, false, true), new("fixed-target-hold-fresh-restart", true, false, true, true), new("fixed-target-hold-fresh-initial", true, false, true, true, true)];
        foreach (var mode in modes)
        {
            var recorder = new PipelineTrace(); recorder.Start(180);
            using var buffer = new RtpOpusPlayoutBuffer(new(), targetReserveMs: mode.ReserveMs, adaptiveReserve: mode.Adaptive, pipeline: recorder,
                tracePeer: "replay", fixedReserveClockTarget: mode.Fixed, transientEmptyQueueHold: mode.TransientHold, freshRestart: mode.FreshRestart, freshInitialStart: mode.FreshInitialStart, responsiveReserve: mode.Responsive, recoveryReserveMs: mode.RecoveryReserveMs, contiguousRestart: mode.Contiguous, boundedRestartWait: mode.Contiguous, repeatedGapRecovery: mode.Repeated);
            var points = new List<Point>(); var readTimes = new List<double>();
            var arrivals = new Dictionary<(uint, uint), double>();
            var destination = new byte[11520];
            foreach (var e in events)
            {
                if (!e.Read)
                {
                    var a = trace.Admissions[e.Index];
                    arrivals.TryAdd((a.Ssrc, a.Timestamp), e.At);
                    buffer.Add(a.Ssrc, a.Timestamp, payload, (e.At - events[0].At) * 1000);
                }
                else
                {
                    buffer.Read(destination.AsSpan(0, trace.Reads[e.Index].RequestedSamples * 2));
                    var state = buffer.Snapshot(); var detail = buffer.Details();
                    points.Add(new(e.At, state.QueueMs, detail.PlcFrames, detail.LatePackets, detail.Rebuffers, state.UnderfillMs));
                    readTimes.Add(e.At);
                }
            }
            recorder.Stop(); var replay = recorder.Snapshot();
            if (replay.EvictedRecords != 0 || replay.Reads.Length != readTimes.Count) throw new InvalidOperationException("Replay trace truncated.");
            var actualLocks = replay.Reads.Select(r => r.LockAt).ToArray();
            var delays = new List<(double Arrived, double Delay)>();
            foreach (var p in replay.Playout.Where(p => p.Outcome == "played"))
            {
                var index = Array.BinarySearch(actualLocks, p.At);
                if (index < 0) index = ~index - 1;
                if (index >= 0 && arrivals.TryGetValue((p.Ssrc, p.Timestamp), out var arrived))
                    delays.Add((arrived, (readTimes[index] - arrived) * 1000));
            }
            var windows = new List<object>();
            foreach (var trim in new[] { 5, 60 })
            {
                var low = events[0].At + trim; var high = events[^1].At - 1;
                var selected = points.Where(p => p.At >= low && p.At <= high).ToArray();
                if (selected.Length < 2) throw new ArgumentException("Insufficient interior replay window.");
                var first = selected[0]; var last = selected[^1];
                windows.Add(new { trimSeconds = trim, durationSeconds = last.At - first.At,
                    queueAfterReadMs = Summary(selected.Select(p => p.QueueMs)),
                    admissionToDecodeRenderMs = Summary(delays.Where(p => p.Arrived >= low && p.Arrived <= high).Select(p => p.Delay)),
                    plcMs = (last.Plc - first.Plc) * 10, latePackets = last.Late - first.Late,
                    rebuffers = last.Rebuffers - first.Rebuffers, underfillMs = last.UnderfillMs - first.UnderfillMs });
            }
            var startupPoints = points.Where(p => p.At < events[0].At + 5).ToArray();
            var startupMetrics = new {
                queueAfterReadMs = Summary(startupPoints.Select(p => p.QueueMs)),
                admissionToDecodeRenderMs = Summary(delays.Where(p => p.Arrived < events[0].At + 5).Select(p => p.Delay)),
                underfillMs = startupPoints.LastOrDefault()?.UnderfillMs,
                plcMs = (startupPoints.LastOrDefault()?.Plc ?? 0) * 10,
                rebuffers = startupPoints.LastOrDefault()?.Rebuffers ?? 0
            };
            var final = buffer.Snapshot();
            results.Add(new { mode.Name, mode.ReserveMs, mode.RecoveryReserveMs, startup = startupMetrics, windows, final = buffer.Details(),
                final.Trims, final.Underruns, final.UnderfillMs });
            Console.WriteLine($"Replay {mode.Name}: PLC={buffer.Details().ConcealedMs:F0}ms late={buffer.Details().LatePackets} rebuffer={buffer.Details().Rebuffers}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { source = Path.GetFileName(input), originalError = error,
            startupRecorded = root.TryGetProperty("traceIncludesStartup", out var startup) && startup.GetBoolean(),
            notes = "Cold-start counterfactual replay of receiver lock ordering and requested PCM sizes, using synthetic 10ms Opus silence. Traces without startup recording lack initial backlog/decoder history; interior windows do not eliminate that limitation. Same arrivals across modes; no physical audio, network or subjective quality measurement. Delay is admission to render call, not physical output or original measured decode time.", results }, DiagnosticJson.Readable));
    }
}
