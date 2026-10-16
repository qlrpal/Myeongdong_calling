using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using VoiceNative;
using System.Text.Json;

internal static class RtpStressChecks
{
    private sealed record Scenario(string Name, int JitterMs = 0, double Loss = 0, bool Burst = false, int DriftPpm = 0,
        int BacklogMs = 0, int ReadMs = 10, bool CorrectClock = true, bool Adaptive = true, int PauseMs = 0);
    private sealed record Arrival(double AtMs, uint Timestamp, byte[] Payload);

    public static void Run(string reportPath)
    {
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var payloads = Enumerable.Range(0, 40).Select(frame => encoder.EncodeAudio(
            Enumerable.Range(0, 480).Select(i => (short)(3000 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 440 / 48000))).ToArray(), format)).ToArray();
        Scenario[] scenarios = [new("steady"), new("jitter-25ms", JitterMs: 25), new("loss-1pct", Loss: 0.01),
            new("burst-100ms", Burst: true), new("clock-fast-200ppm", DriftPpm: 200), new("clock-slow-200ppm", DriftPpm: -200),
            new("clock-fast-200ppm-off", DriftPpm: 200, CorrectClock: false), new("clock-slow-200ppm-off", DriftPpm: -200, CorrectClock: false),
            new("backlog-80ms", BacklogMs: 80), new("clock-fast-500ppm-40ms-read", DriftPpm: 500, ReadMs: 40),
            new("clock-slow-500ppm-40ms-read", DriftPpm: -500, ReadMs: 40),
            new("loss-jitter-fast-clock", JitterMs: 10, Loss: 0.01, DriftPpm: 200),
            new("loss-jitter-slow-clock", JitterMs: 10, Loss: 0.01, DriftPpm: -200),
            new("pause-30ms-fixed", PauseMs: 30, Adaptive: false), new("pause-30ms-adaptive", PauseMs: 30),
            new("pause-45ms-fixed", PauseMs: 45, Adaptive: false), new("pause-45ms-adaptive", PauseMs: 45)];
        const int durationMs = 600000;
        var results = new List<object>();
        var fixedPauses = new Dictionary<int, (double Plc, long Late)>();
        foreach (var scenario in scenarios)
        {
            var random = new Random(1234);
            var arrivals = new List<Arrival>(); var injectedLoss = 0;
            // Source and render clocks are independent; no wall-clock sleeps or physical devices.
            var sourceStep = 10.0 / (1 + scenario.DriftPpm / 1000000.0);
            for (var frame = 0; frame * sourceStep < durationMs + scenario.BacklogMs; frame++)
            {
                var sendAt = frame * sourceStep;
                var lost = random.NextDouble() < scenario.Loss || scenario.Burst && sendAt >= 30000 && sendAt % 30000 < 100;
                if (lost) { injectedLoss++; continue; }
                var arrivalAt = sendAt + 3 + random.NextDouble() * scenario.JitterMs - scenario.BacklogMs;
                if (scenario.PauseMs > 0 && sendAt is >= 2000 and < 120000 && sendAt % 2000 < scenario.PauseMs)
                    arrivalAt = Math.Floor(sendAt / 2000) * 2000 + scenario.PauseMs + 3;
                arrivals.Add(new(arrivalAt,
                    unchecked(uint.MaxValue - 959 + (uint)frame * 480), payloads[frame % payloads.Length]));
            }
            arrivals.Sort((a, b) => a.AtMs.CompareTo(b.AtMs));
            using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: scenario.CorrectClock, adaptiveReserve: scenario.Adaptive);
            var output = new byte[scenario.ReadMs * 96]; var next = 0; double peakQueue = 0;
            double? queueAt60Seconds = null;
            for (var renderAt = 0; renderAt < durationMs; renderAt += scenario.ReadMs)
            {
                while (next < arrivals.Count && arrivals[next].AtMs <= renderAt)
                {
                    var packet = arrivals[next++]; buffer.Add(1, packet.Timestamp, packet.Payload, packet.AtMs);
                    peakQueue = Math.Max(peakQueue, buffer.Snapshot().QueueMs);
                }
                buffer.Read(output);
                if (renderAt == 60000) queueAt60Seconds = buffer.Snapshot().QueueMs;
            }
            var state = buffer.Snapshot(); var detail = buffer.Details();
            if (peakQueue > 120 || detail.InvalidPackets != 0 || detail.DecodedPackets < 50000)
                throw new InvalidOperationException($"Stress bounds/progress failed: {scenario.Name}");
            if (scenario.Name == "steady" && (state.Underruns != 0 || detail.PlcFrames != 0 || state.Trims != 0))
                throw new InvalidOperationException("Steady source/render clocks must remain lossless.");
            if (scenario.Loss > 0 && detail.PlcFrames == 0) throw new InvalidOperationException("Injected loss was not concealed.");
            if (scenario.Burst && detail.Rebuffers == 0) throw new InvalidOperationException("100ms outages must exceed bounded PLC and rebuffer.");
            if (scenario.CorrectClock && scenario.DriftPpm != 0 && (state.Underruns != 0 || state.Trims != 0 || detail.Rebuffers != 0))
                throw new InvalidOperationException($"Clock correction must avoid trimming and underflow: {scenario.Name}");
            if (scenario.CorrectClock && scenario.DriftPpm != 0 && scenario.Loss == 0 && detail.PlcFrames != 0)
                throw new InvalidOperationException($"Clock skew alone must not require PLC: {scenario.Name}");
            if (scenario.BacklogMs > 0 && (queueAt60Seconds is > 45 || state.Trims != 0 || detail.PlcFrames != 0))
                throw new InvalidOperationException("Backlog must recover below 45ms in 60s without frame trimming or PLC.");
            if (detail.TargetReserveMs is < 20 or > 40 || detail.MaxConsecutivePlcMs > 60)
                throw new InvalidOperationException("Adaptive reserve or PLC exceeded the latency/loss bounds.");
            if (scenario.PauseMs > 0 && scenario.Adaptive && (detail.TargetReserveMs != 20 || detail.HeldMs == 0 || detail.Rebuffers != 0))
                throw new InvalidOperationException("Short stalls must recover without rebuffering and return to the low-latency floor.");
            if (scenario.PauseMs > 0)
            {
                if (!scenario.Adaptive) fixedPauses[scenario.PauseMs] = (detail.ConcealedMs, detail.LatePackets);
                else if (detail.ConcealedMs >= fixedPauses[scenario.PauseMs].Plc || detail.LatePackets >= fixedPauses[scenario.PauseMs].Late)
                    throw new InvalidOperationException("Adaptive reserve must reduce both PLC and late drops against identical fixed-reserve arrivals.");
            }
            if (scenario.Name == "loss-1pct" && (detail.TargetReserveMs != 20 || detail.HeldMs != 0))
                throw new InvalidOperationException("Random packet loss alone must not inflate the jitter reserve.");
            results.Add(new
            {
                scenario.Name,
                durationMs,
                scenario.JitterMs,
                scenario.Loss,
                scenario.DriftPpm,
                scenario.CorrectClock,
                scenario.ReadMs,
                scenario.Adaptive,
                queueAt60Seconds,
                injectedLoss,
                delivered = next,
                peakQueueMs = peakQueue,
                finalQueueMs = state.QueueMs,
                state.Trims,
                state.Underruns,
                state.UnderfillMs,
                state.MaxUnderfillMs,
                detail,
                plcPercent = detail.ConcealedMs * 100 / durationMs,
                continuousWithoutTrim = state.Underruns == 0 && state.Trims == 0
            });
            Console.WriteLine($"BOUNDS/PROGRESS PASS {scenario.Name}: PLC={detail.ConcealedMs:F0}ms late={detail.LatePackets} rebuffer={detail.Rebuffers} peak={peakQueue:F0}ms trims={state.Trims} underfill={state.UnderfillMs:F0}ms");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
            generatedAt = DateTimeOffset.Now,
            notes = "가상 시간 기반 결정적 검사: 17조건×10분 상당. 소비율/적응 여유 on/off 비교 포함. 실제 네트워크·장치·음질 측정이 아님.",
            results
        }, DiagnosticJson.Readable));
    }
}
