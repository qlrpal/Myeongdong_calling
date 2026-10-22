using System.Text.Json;

namespace VoiceNative;

// Counterfactual queue simulation with recorded lock ordering and device pull sizes.
// Payload contents are synthetic; this does not reproduce sound quality or device output.
internal static class ExtremeReplay
{
    private sealed record Event(double At, int Index, bool Read);
    private sealed record Point(double At, double QueueMs, double UnderfillMs, double ConcealedMs,
        long Late, long Decoded, double DiscardedMs, long Resyncs);

    public static void Run(string input, string output)
    {
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must not overwrite input.");
        if (new FileInfo(input).Length > 100 * 1024 * 1024) throw new ArgumentException("Trace exceeds 100MiB.");
        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var root = document.RootElement;
        if (root.GetProperty("error").ValueKind != JsonValueKind.Null) throw new ArgumentException("Incomplete trial.");
        var original = root.GetProperty("options").Deserialize<LatencyExperiment>() ?? throw new ArgumentException("Experiment options required.");
        original.Validate();
        var trace = root.GetProperty("trace").Deserialize<PipelineTraceSnapshot>() ?? throw new ArgumentException("Trace required.");
        if (trace.EvictedRecords != 0 || trace.Admissions.Length is < 1 or > 24000 || trace.Reads.Length is < 1 or > 24000)
            throw new ArgumentException("Missing or evicted records.");
        if (trace.Admissions.Select(a => a.Peer).Distinct().Count() != 1
            || trace.Reads.Any(r => r.Peer != trace.Admissions[0].Peer || r.RequestedSamples is < 1 or > 5760)
            || trace.Admissions.Any(a => a.Outcome is not ("queued" or "late" or "duplicate")))
            throw new ArgumentException("Replay requires one peer and valid uniform packet durations.");
        var samples = root.GetProperty("samples").EnumerateArray().ToArray();
        if (samples.Length < 2 || samples.Any(s => s.GetProperty("FrameMs").GetDouble() != original.FrameMs
            || s.GetProperty("Peers").GetArrayLength() != 1
            || !s.GetProperty("Peers")[0].GetProperty("Audio").GetProperty("RtpPlayout").TryGetProperty("PacketQuantumMs", out var remoteQuantum)
            || remoteQuantum.GetDouble() != original.FrameMs))
            throw new ArgumentException("Replay requires verified matching local/remote packet durations; mixed-mode traces are unsupported.");
        var events = trace.Admissions.Select((a, i) => new Event(a.LockAt, i, false))
            .Concat(trace.Reads.Select((r, i) => new Event(r.LockAt, i, true))).OrderBy(e => e.At).ThenBy(e => e.Read).ToArray();
        if (events.Any(e => !double.IsFinite(e.At)) || events[^1].At - events[0].At < 8
            || events.GroupBy(e => e.At).Any(g => g.Select(e => e.Read).Distinct().Count() > 1))
            throw new ArgumentException("Insufficient or ambiguous event timing.");
        var low = events[0].At + 2; var high = events[^1].At - 1;
        var arrivals = trace.Admissions.GroupBy(a => (a.Ssrc, a.Timestamp)).ToDictionary(g => g.Key, g => g.First().LockAt);
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, original);
        var payload = encoder.EncodeAudio(new short[original.FrameSamples], encoder.SupportedFormats.Single());
        var results = new List<object>();
        foreach (var quiet in original.AdaptiveShortReserve ? new[] { 2, 3, 4, 6 } : new[] { 6 })
        foreach (var reserve in original.AdaptiveShortReserve ? new[] { original.ReserveMs } : new[] { 0, 2.5, 5, 7.5, 10, 15 })
        foreach (var hold in (original.AdaptiveShortReserve ? new[] { 0, 2.5, 5, 10, 15, 20, 30 } : new[] { 0, 2.5, 5, 10 })
            .Append(original.EmptyHoldMs).Distinct().Order().Where(h => h * 48 % original.FrameSamples == 0 && h <= original.PlcLimitMs))
        {
            var options = original with { ReserveMs = reserve, EmptyHoldMs = hold, ShortReserveQuietSeconds = quiet };
            options.Validate();
            var recorder = new PipelineTrace(); recorder.Start(180);
            using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options, pipeline: recorder, tracePeer: "replay");
            var destination = new byte[11520]; var points = new List<Point>(); var readTimes = new List<double>();
            foreach (var e in events)
            {
                if (!e.Read)
                {
                    var a = trace.Admissions[e.Index];
                    buffer.Add(a.Ssrc, a.Timestamp, payload, (e.At - events[0].At) * 1000);
                    continue;
                }
                buffer.Read(destination.AsSpan(0, trace.Reads[e.Index].RequestedSamples * 2));
                var state = buffer.Snapshot(); var detail = buffer.Details();
                points.Add(new(e.At, state.QueueMs, state.UnderfillMs, detail.ConcealedMs, detail.LatePackets,
                    detail.DecodedPackets, detail.RecoveryDiscardedMs, detail.Resyncs));
                readTimes.Add(e.At);
            }
            recorder.Stop(); var replay = recorder.Snapshot();
            if (replay.EvictedRecords != 0 || replay.Reads.Length != readTimes.Count) throw new InvalidOperationException("Replay truncated.");
            var selected = points.Where(p => p.At >= low && p.At <= high).ToArray();
            if (selected.Length < 2) throw new ArgumentException("Insufficient interior window.");
            var locks = replay.Reads.Select(r => r.LockAt).ToArray(); var delays = new List<double>();
            foreach (var played in replay.Playout.Where(p => p.Outcome == "played"))
            {
                var index = Array.BinarySearch(locks, played.At); if (index < 0) index = ~index - 1;
                if (index >= 0 && readTimes[index] >= low && readTimes[index] <= high
                    && arrivals.TryGetValue((played.Ssrc, played.Timestamp), out var arrival))
                    delays.Add((readTimes[index] - arrival) * 1000);
            }
            var first = selected[0]; var last = selected[^1];
            var underfill = last.UnderfillMs - first.UnderfillMs;
            var plc = last.ConcealedMs - first.ConcealedMs;
            var row = new { reserveMs = reserve, emptyHoldMs = hold, quietSeconds = quiet, durationSeconds = last.At - first.At,
                queueP50Ms = Percentile(selected.Select(p => p.QueueMs), .5),
                queueP95Ms = Percentile(selected.Select(p => p.QueueMs), .95),
                admissionToRenderP50Ms = Percentile(delays, .5), admissionToRenderP95Ms = Percentile(delays, .95),
                playedWithDelayCount = delays.Count, underfillMs = underfill, concealedMs = plc,
                latePackets = last.Late - first.Late, decodedPackets = last.Decoded - first.Decoded,
                discardedMs = last.DiscardedMs - first.DiscardedMs, resyncs = last.Resyncs - first.Resyncs };
            results.Add(row);
            Console.WriteLine($"reserve={reserve} hold={hold} quiet={quiet}: queueP50={row.queueP50Ms:F2}ms admission/render={row.admissionToRenderP50Ms:F2}ms underfill={underfill:F2}ms PLC={plc:F2}ms decoded={row.decodedPackets}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { source = Path.GetFileName(input),
            notes = "Synthetic uniform-duration payloads and recorded lock ordering. Cold start omits original queue history; skip first 2s and last 1s but history effects can remain. Admission-to-render is only for surviving played packets; inspect decoded/late/discard/PLC together. No network/device/codec execution timing or physical latency claim. Empty hold is bounded PLC without advancing RTP time, not blocking the render thread.", results }, DiagnosticJson.Readable));
    }

    private static double? Percentile(IEnumerable<double> values, double quantile)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * quantile) - 1];
    }
}
