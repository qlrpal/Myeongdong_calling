using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class RtpSoakProbe
{
    public static async Task<int> RunAsync(string server, string room, string reportPath, string role, int seconds, PlayoutPolicy policy = PlayoutPolicy.Compatible, bool measure = true, string? mediaAddress = null, OpusProfile? profile = null, SpeechFixture? speech = null, bool forceRelay = false, int connectTimeoutSeconds = 35, string? captureDeviceId = null, bool intermediateCapture = true, int reserveMs = 20, bool responsiveReserve = false, bool tracePipeline = false, DateTimeOffset? startAt = null, bool requireDirect = false)
    {
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Role must be pc or laptop.");
        if (seconds is < 60 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (reserveMs is < 10 or > 40) throw new ArgumentOutOfRangeException(nameof(reserveMs));
        if (forceRelay && requireDirect) throw new ArgumentException("Conflicting route requirements.");
        var events = new List<string>(); var samples = new List<CallDiagnostics>();
        var measurements = new List<AcousticLatencyResult>(); var sync = new object();
        string? error = null; var attempts = 0;
        PipelineTraceSnapshot? trace = null;
        if (speech is not null && measure) throw new ArgumentException("Speech soak cannot run latency signal measurements.");
        await using var engine = new WebRtcCallEngine { DiagnosticCaptureTransform = speech is null ? null : speech.Fill, ForceRelay = forceRelay, DirectOnly = requireDirect, CaptureDeviceId = captureDeviceId, IntermediateCapture = intermediateCapture, DiagnosticReserveMs = reserveMs, ResponsiveReserve = responsiveReserve };
        engine.SetPlayoutPolicy(policy);
        engine.SetMediaInterface(mediaAddress);
        if (profile is { } selectedProfile) engine.SetOpusProfile(selectedProfile);
        engine.Status += message => { lock (sync) events.Add($"[{DateTimeOffset.Now:HH:mm:ss}] {message}"); };
        if (tracePipeline && startAt is null) engine.StartPipelineTrace(150);
        try
        {
            await engine.JoinAsync(new Uri(server), room, $"soak-{role}");
            var ready = Stopwatch.StartNew();
            while (engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            {
                if (ready.Elapsed.TotalSeconds > connectTimeoutSeconds) throw new TimeoutException($"Peer did not connect in {connectTimeoutSeconds}s.");
                await Task.Delay(200);
            }
            if (startAt is { } scheduled)
            {
                var remaining = scheduled - DateTimeOffset.UtcNow;
                if (remaining < TimeSpan.Zero || remaining > TimeSpan.FromMinutes(2))
                    throw new InvalidOperationException("Scheduled measurement start missed or too distant.");
                await Task.Delay(remaining);
                if (tracePipeline) engine.StartPipelineTrace(150);
            }
            Console.WriteLine($"{DateTimeOffset.Now:O} connected role={role} seconds={seconds}");
            var clock = Stopwatch.StartNew(); var nextLog = 0;
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                if (measure && attempts < seconds / 60 && clock.Elapsed.TotalSeconds >= (role == "pc" ? 15 : 40) + attempts * 60)
                {
                    attempts++;
                    try
                    {
                        await engine.MeasureLoopbackLatencyAsync();
                        if (engine.GetDiagnostics().Acoustic is { } result) measurements.Add(result);
                    }
                    catch (Exception ex) { lock (sync) events.Add("측정 실패: " + ex.Message); }
                }
                var snapshot = engine.GetDiagnostics(); samples.Add(snapshot);
                if (snapshot.Peers.Length != 1 || snapshot.Peers[0].State != "connected") throw new InvalidOperationException("Peer disconnected during soak.");
                if (forceRelay && snapshot.Peers[0].Route != "TURN 중계") throw new InvalidOperationException("TURN-only test did not select a relay route.");
                if (requireDirect && snapshot.Peers[0].Route != "직접 연결") throw new InvalidOperationException("Direct comparison did not select a direct route.");
                if (clock.Elapsed.TotalSeconds > 5 && (snapshot.Peers[0].RxPackets == 0 || snapshot.Peers[0].Precision?.LastPacketAgeSeconds is > 5))
                    throw new InvalidOperationException("Received media stalled for more than five seconds.");
                if (clock.Elapsed.TotalSeconds >= nextLog)
                {
                    var audio = snapshot.Peers[0].Audio;
                    Console.WriteLine($"{DateTimeOffset.Now:O} elapsed={clock.Elapsed.TotalSeconds:F0}s queue={audio.QueueMs:F1}ms PLC={audio.RtpPlayout?.ConcealedMs:F0}ms underfill={audio.UnderfillMs:F1}ms rebuffer={audio.RtpPlayout?.Rebuffers} correction={audio.RtpPlayout?.RateCorrectionPpm:F0}ppm readMax={audio.RtpPlayout?.ReadRequestMaxMs:F2}ms");
                    nextLog += 30;
                }
                await Task.Delay(1000);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { if (tracePipeline) trace = engine.StopPipelineTrace(); if (error is null) await Task.Delay(2000); await engine.LeaveAsync(); }
        if (error is null && tracePipeline && (trace is null || trace.EvictedRecords != 0 || trace.Sends.Length < 11000 || trace.Receives.Length < 11000)) error = "Incomplete policy trace.";
        string[] captured; lock (sync) captured = events.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
            generatedAt = DateTimeOffset.Now,
            role,
            mediaAddress,
            requestedProfile = profile?.ToString(),
            forceRelay,
            requireDirect,
            captureDeviceId,
            intermediateCapture,
            reserveMs,
            responsiveReserve = responsiveReserve || policy == PlayoutPolicy.ResponsiveLowLatency,
            traceIncludesStartup = tracePipeline && startAt is null, scheduledStart = startAt, trace,
            seconds,
            policy = policy.ToString(),
            loopbackProbes = measure,
            speechFixture = speech?.Snapshot(),
            error,
            attempts,
            measurements,
            samples,
            events = captured,
            notes = "RTP 연속 통화. 송신 코덱은 samples[].Encoder, WASAPI 저지연 요청·복귀40ms. 루프백 신호 사용 여부는 loopbackProbes 참조. 물리 입→귀 실측 아님. 원음 저장 없음."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"{DateTimeOffset.Now:O} complete measurements={measurements.Count}/{attempts} error={error is not null}");
        return error is null ? 0 : 1;
    }
}
