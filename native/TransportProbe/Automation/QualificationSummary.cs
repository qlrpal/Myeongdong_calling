using System.Text.Json;

namespace VoiceNative;

// Explicit, reproducible nominal continuity gates; not a physical latency verdict.
internal static class QualificationSummary
{
    public static void Run(string directory, string output)
    {
        var rows = new List<object>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            if (!Path.GetFileName(file).StartsWith("pc-", StringComparison.Ordinal)
                && !Path.GetFileName(file).StartsWith("laptop-", StringComparison.Ordinal)) continue;
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Output must not overwrite a trial.");
            if (new FileInfo(file).Length > 100 * 1024 * 1024) throw new ArgumentException("Trial exceeds 100MiB.");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (!root.TryGetProperty("samples", out var recorded)) continue;
            var samples = recorded.Deserialize<CallDiagnostics[]>() ?? [];
            if (samples.Length < 2 || samples.Any(s => s.Peers.Length != 1)) throw new ArgumentException("One-peer trial required: " + file);
            var error = root.GetProperty("error").GetString();
            var first = samples[0]; var last = samples[^1];
            var before = first.Peers[0]; var after = last.Peers[0];
            var audioBefore = before.Audio; var audio = after.Audio;
            var duration = last.MonotonicSeconds - first.MonotonicSeconds;
            if (!double.IsFinite(duration) || duration <= 0 || audioBefore.RtpPlayout is null || audio.RtpPlayout is null)
                throw new ArgumentException("RTP playout counters required: " + file);
            var requested = audio.RequestedMs - audioBefore.RequestedMs;
            if (requested is null or <= 0) throw new ArgumentException("No render requests: " + file);
            var underfill = audio.UnderfillMs - audioBefore.UnderfillMs;
            var plc = audio.RtpPlayout.ConcealedMs - audioBefore.RtpPlayout.ConcealedMs;
            var sourceDrops = last.Sender?.DroppedFrames - first.Sender?.DroppedFrames;
            var invalid = audio.RtpPlayout.InvalidPackets - audioBefore.RtpPlayout.InvalidPackets;
            var empty = audio.Render?.EmptyEvents - audioBefore.Render?.EmptyEvents;
            var restarts = audio.DeviceRestarts - audioBefore.DeviceRestarts;
            var captureDiscontinuities = (last.Microphone?.CaptureDiscontinuities ?? 0) - (first.Microphone?.CaptureDiscontinuities ?? 0);
            var sourceAudioRatio = (after.TxFrames - before.TxFrames) * last.FrameMs / (duration * 1000);
            var underfillPercent = underfill / requested * 100;
            var concealedPercent = plc / requested.Value * 100;
            var discardedMs = audio.RtpPlayout.RecoveryDiscardedMs - audioBefore.RtpPlayout.RecoveryDiscardedMs;
            var discardedPercent = discardedMs / requested.Value * 100;
            var trace = root.GetProperty("trace").Deserialize<PipelineTraceSnapshot>() ?? throw new ArgumentException("Trace required.");
            var arrivals = trace.Admissions.GroupBy(a => (a.Ssrc, a.Timestamp)).ToDictionary(g => g.Key, g => g.First().LockAt);
            var delays = trace.Playout.Where(p => p.Outcome == "played" && p.At >= first.MonotonicSeconds && p.At <= last.MonotonicSeconds
                && arrivals.ContainsKey((p.Ssrc, p.Timestamp))).Select(p => (p.At - arrivals[(p.Ssrc, p.Timestamp)]) * 1000).ToArray();
            var captureToSend = trace.Sends.Where(s => s.Capture?.FirstSampleAt is not null)
                .Select(s => (s.SendStart - s.Capture!.Value.FirstSampleAt!.Value) * 1000).ToArray();
            var startQueues = samples.Take(Math.Max(1, samples.Length / 3)).Select(s => s.Peers[0].Audio.QueueMs).OfType<double>();
            var endQueues = samples.Skip(samples.Length * 2 / 3).Select(s => s.Peers[0].Audio.QueueMs).OfType<double>();
            var growth = Percentile(endQueues, .5) - Percentile(startQueues, .5);
            var excess = samples.Select(s => s.Peers[0].Audio.QueueMs - s.Peers[0].Audio.RtpPlayout!.TargetReserveMs).OfType<double>().ToArray();
            var excessGrowth = Percentile(excess.Skip(excess.Length * 2 / 3), .5)
                - Percentile(excess.Take(Math.Max(1, excess.Length / 3)), .5);
            var route = root.GetProperty("route").GetString();
            var mediaAddress = root.TryGetProperty("mediaAddress", out var address) ? address.GetString() : null;
            var mediaSelectionMatches = samples.All(s => s.MediaAddress == mediaAddress);
            var routeMatches = samples.All(s => route == "relay"
                ? s.Peers[0].Route.Contains("TURN", StringComparison.Ordinal)
                : !s.Peers[0].Route.Contains("TURN", StringComparison.Ordinal) && s.Peers[0].Route.Contains("직접", StringComparison.Ordinal));
            var failures = new List<string>();
            void Check(bool ok, string reason) { if (!ok) failures.Add(reason); }
            Check(error is null, "trial-error");
            Check(samples.All(s => s.Peers[0].State == "connected" && s.Peers[0].Audio.DeviceError is null), "disconnect-or-device-error");
            Check(sourceDrops == 0, "sender-drop-or-unknown"); Check(invalid == 0, "invalid-packet");
            Check(restarts == 0, "device-restart"); Check(empty == 0, "output-drain-or-unknown");
            Check(sourceAudioRatio is >= .99 and <= 1.01, "source-duration-mismatch");
            Check(underfillPercent <= .3, "underfill-over-0.3-percent");
            Check(underfillPercent + concealedPercent <= 1, "synthetic-or-missing-audio-over-1-percent");
            Check(discardedPercent <= 1, "discarded-audio-over-1-percent");
            Check(excessGrowth <= 10, "excess-queue-growth-over-10ms-or-unknown");
            Check(trace.EvictedRecords == 0, "truncated-trace"); Check(routeMatches, "wrong-route");
            Check(mediaSelectionMatches, "wrong-media-interface");
            var pass = failures.Count == 0;
            var row = new { trial = Path.GetFileName(file), route, durationSeconds = duration, mode = root.GetProperty("mode").GetString(),
                mediaAddress, mediaSelectionMatches, error, continuityPass = pass, sourceRtpQueuePass = failures.All(r => r == "output-drain-or-unknown"), outputDrainFree = empty == 0,
                failureReasons = failures, strictCapturePass = captureDiscontinuities == 0, longTrial = duration >= 295, sourceAudioRatio,
                underfillMs = underfill, underfillPercent, concealedMs = plc, concealedPercent, discardedMs, discardedPercent, sourceDrops, invalid,
                emptyEvents = empty, startupEmptyEvents = audioBefore.Render?.EmptyEvents, totalEmptyEvents = audio.Render?.EmptyEvents,
                startupOutputDrainFree = audioBefore.Render?.EmptyEvents == 0, restarts, captureDiscontinuities, routeMatches,
                queueGrowthMs = growth, excessQueueGrowthMs = excessGrowth, queueP50Ms = Percentile(samples.Select(s => s.Peers[0].Audio.QueueMs).OfType<double>(), .5),
                queueP95Ms = Percentile(samples.Select(s => s.Peers[0].Audio.QueueMs).OfType<double>(), .95),
                admissionToRenderP50Ms = Percentile(delays, .5), admissionToRenderP95Ms = Percentile(delays, .95), matchedPlayedPackets = delays.Length,
                captureApiToSendP50Ms = Percentile(captureToSend, .5), captureApiToSendP95Ms = Percentile(captureToSend, .95),
                outputTargetMs = audio.Render?.TargetPaddingMs, sourcePeriodMs = last.Microphone?.EnginePeriodMs,
                reserveP50Ms = Percentile(samples.Select(s => s.Peers[0].Audio.RtpPlayout!.TargetReserveMs), .5),
                reserveP95Ms = Percentile(samples.Select(s => s.Peers[0].Audio.RtpPlayout!.TargetReserveMs), .95),
                processCoreUsage = root.GetProperty("processCoreUsage").ValueKind == JsonValueKind.Number ? root.GetProperty("processCoreUsage").GetDouble() : (double?)null };
            rows.Add(row);
            Console.WriteLine($"{row.trial}: continuity={(pass ? "PASS" : "FAIL")} underfill={underfillPercent:F3}% PLC={concealedPercent:F3}% empty={empty} growth={growth:F2}ms admission/render={row.admissionToRenderP50Ms:F2}ms");
        }
        if (rows.Count == 0) throw new ArgumentException("No trials found.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { criteria = new { maxUnderfillPercent = .3, maxUnderfillPlusConcealedPercent = 1, maxDiscardedPercent = 1,
            maxExcessQueueGrowthMs = 10, minSourceAudioRatio = .99, maxSourceAudioRatio = 1.01,
            requireNoDeviceErrorsRestartsEmptyEventsSenderDropsInvalidPackets = true,
            longTrialMinimumSeconds = 295 }, notes = "Provisional latency-first nominal continuity criteria, not a global default-promotion decision. All counters use first/last snapshots. Capture flags are reported separately as strictCapturePass; source duration/decoded continuity gates still apply. Queue growth compares last/first third medians; the gate uses queue minus actual reserve so intentional bounded adaptive reserve increases are not mistaken for clock drift. Raw growth remains reported. Delay statistics cover played packets in the bounded trace only, so inspect PLC/discards and source coverage; they exclude physical device output. Capture API timestamps are device-reported and not physical microphone onset. Direct/relay route is verified from actual peer diagnostics. Gaming, multipeer, adverse-network and subjective audio require separate validation.", rows }, DiagnosticJson.Readable));
    }

    private static double? Percentile(IEnumerable<double> values, double q)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(sorted.Length * q) - 1];
    }
}
