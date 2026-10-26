using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Text.Json;
using VoiceNative;

internal static class PlayoutTargetChecks
{
    public static async Task PolicyAsync()
    {
        await using var engine = new WebRtcCallEngine();
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
        engine.SetRawOutputPreference(false); engine.SetRawOutputPreference(true);
        engine.SetMediaInterface(null);
        try { engine.SetMediaInterface("0.0.0.0"); throw new InvalidOperationException("Nonlocal media binding was accepted."); }
        catch (ArgumentException) { }
        if (engine.GetDiagnostics().MediaAddress is not null) throw new InvalidOperationException("Rejected media selection changed the OS-routing default.");
        if (engine.GetDiagnostics().PlaybackPolicy != "LowLatency") throw new InvalidOperationException("Low-latency policy was not selected.");
        try { engine.SetAdaptivePlayoutPreference(true); throw new InvalidOperationException("Incompatible adaptive policy was accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("기존 재생 정책", StringComparison.Ordinal)) { }
        engine.SetPlayoutPolicy(PlayoutPolicy.ResponsiveLowLatency);
        if (engine.GetDiagnostics().PlaybackPolicy != "ResponsiveLowLatency") throw new InvalidOperationException("Responsive policy was not selected.");
        try { engine.SetAdaptivePlayoutPreference(true); throw new InvalidOperationException("Legacy adaptation mixed with responsive policy."); }
        catch (InvalidOperationException error) when (error.Message.Contains("기존 재생 정책", StringComparison.Ordinal)) { }
        engine.SetPlayoutPolicy(PlayoutPolicy.Compatible); engine.SetAdaptivePlayoutPreference(true);
        if (engine.GetDiagnostics().PlaybackPolicy != "Compatible") throw new InvalidOperationException("Compatible policy was not restored.");
        try { engine.SetPlayoutPolicy((PlayoutPolicy)999); throw new InvalidOperationException("Invalid policy was accepted."); }
        catch (ArgumentOutOfRangeException) { }
        Console.WriteLine("PASS: low-latency selection, compatible fallback, adaptive conflict and invalid-policy rejection.");
    }
    public static void Run(string path)
    {
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var payload = encoder.EncodeAudio(new short[480], format);
        CheckTransientHold(payload);
        var results = new List<object>();
        foreach (var readSamples in new[] { 240, 480, 488, 960, 1920 })
        foreach (var phaseMs in new[] { 0, 3, 7 })
        foreach (var pauseMs in new[] { 0, 30 })
        foreach (var driftPpm in new[] { -200, 0, 200 })
        foreach (var (fixedTarget, transientHold, demandBoundedHold) in new[] { (false, false, true), (true, false, true), (true, true, false), (true, true, true) })
        {
            using var buffer = new RtpOpusPlayoutBuffer(new(), adaptiveReserve: false, fixedReserveClockTarget: fixedTarget, transientEmptyQueueHold: transientHold, demandBoundedHold: demandBoundedHold);
            var readMs = readSamples / 48.0;
            var output = new byte[readSamples * 2];
            var next = 0; var queues = new List<double>();
            for (var now = 0.0; now < 60000; now += readMs)
            {
                while (next < 6010)
                {
                    var arrival = next * 10 / (1 + driftPpm / 1000000.0) + phaseMs;
                    if (pauseMs > 0 && arrival >= 2000 && arrival % 2000 < pauseMs)
                        arrival = Math.Floor(arrival / 2000) * 2000 + pauseMs;
                    if (arrival > now) break;
                    buffer.Add(1, (uint)next * 480, payload, arrival); next++;
                }
                buffer.Read(output);
                if (now >= 30000) queues.Add(buffer.Snapshot().QueueMs);
            }
            var state = buffer.Snapshot(); var detail = buffer.Details();
            if (detail.InvalidPackets != 0 || detail.DecodedPackets < 5800 || state.Trims != 0)
                throw new InvalidOperationException("Target comparison bounds/progress failed.");
            if (pauseMs == 0 && (detail.PlcFrames != 0 || state.Underruns != 0))
                throw new InvalidOperationException("Regular source must remain lossless in either target mode.");
            results.Add(new { readMs, phaseMs, pauseMs, driftPpm, fixedTarget, transientHold, demandBoundedHold, meanQueueLast30SecondsMs = queues.Average(),
                state.Trims, state.Underruns, state.UnderfillMs, state.MaxUnderfillMs, detail });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { notes = "Deterministic 60s simulation per case; identical arrivals in both modes. Not physical latency or an audio quality test.", results }, DiagnosticJson.Readable));
        Console.WriteLine($"PASS: {results.Count} matched target/hold/phase/render-period/stall/drift comparisons; regular sources remain lossless.");
    }

    private static void CheckTransientHold(byte[] payload)
    {
        static RtpOpusPlayoutBuffer Create() => new(new(), correctClock: false, adaptiveReserve: false, transientEmptyQueueHold: true);
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        using var delayed = Create();
        for (uint stamp = 0; stamp < 1440; stamp += 480) delayed.Add(1, stamp, payload);
        var output = new byte[960];
        for (var i = 0; i < 4; i++) delayed.Read(output);
        delayed.Add(1, 1440, payload); delayed.Read(output);
        Require(delayed.Details() is { DecodedPackets: 4, PlcFrames: 1, HeldMs: 10, LatePackets: 0, TargetReserveMs: 20 },
            "An empty-queue hold must retain the delayed packet's RTP position without raising reserve.");
        using var lost = Create();
        foreach (var stamp in new uint[] { 0, 480, 960, 1920 }) lost.Add(1, stamp, payload);
        for (var i = 0; i < 5; i++) lost.Read(output);
        Require(lost.Details() is { DecodedPackets: 4, PlcFrames: 1, HeldMs: 0 },
            "A missing packet with later RTP queued must advance ordinary PLC without holding.");
        lost.Add(1, 1440, payload);
        Require(lost.Details().LatePackets == 1, "Already concealed RTP must remain late.");
        using var outage = Create();
        for (uint stamp = 0; stamp < 1440; stamp += 480) outage.Add(1, stamp, payload);
        for (var i = 0; i < 12; i++) outage.Read(output);
        Require(outage.Details() is { HeldMs: 10, PlcFrames: 6, MaxConsecutivePlcMs: 60, Rebuffers: 1 },
            "A long outage must hold once, then reach the existing 60ms PLC/rebuffer bound.");
        using var fresh = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false, transientEmptyQueueHold: true, freshRestart: true);
        for (uint stamp = 0; stamp < 1440; stamp += 480) fresh.Add(1, stamp, payload);
        for (var i = 0; i < 12; i++) fresh.Read(output);
        for (uint stamp = 3840; stamp < 8160; stamp += 480) fresh.Add(1, stamp, payload);
        fresh.Read(output);
        Require(fresh.Details() is { RecoveryDiscardedPackets: 6, RecoveryDiscardedMs: 60, Rebuffers: 1 } && fresh.Snapshot().QueueMs == 20,
            "Outage recovery must keep the fresh reserve and explicitly count discarded speech.");
        using var initial = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false, freshRestart: true);
        for (uint stamp = 0; stamp < 4320; stamp += 480) initial.Add(1, stamp, payload);
        initial.Read(output);
        Require(initial.Details().RecoveryDiscardedPackets == 0 && initial.Snapshot().QueueMs == 80,
            "Fresh recovery must not trim the initial startup.");
        Console.WriteLine("PASS: transient hold retains late arrivals, preserves genuine-loss progression and bounds long outages.");
    }
}
