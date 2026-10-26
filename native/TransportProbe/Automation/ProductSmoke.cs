using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class ProductSmoke
{
    public static async Task RunAsync(string server, string room, string report, string role, string wave, bool osMediaPorts = false)
    {
        using var sdkLog = new TurnSignalingLog();
        SIPSorcery.LogFactory.Set(sdkLog);
        var eventClock = Stopwatch.StartNew();
        var samples = new List<object>(); var events = new List<string>(); var sync = new object();
        var fixture = SpeechFixture.Load(wave);
        await using var engine = new WebRtcCallEngine { ForceRelay = true, IntermediateCapture = false, DiagnosticCaptureTransform = fixture.Fill, DiagnosticOsMediaPorts = osMediaPorts };
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
        await using var session = new CallSession(engine);
        engine.Status += value => { lock (sync) events.Add($"{eventClock.Elapsed.TotalSeconds:F3}s {value}"); };
        session.Changed += value => { lock (sync) events.Add($"{eventClock.Elapsed.TotalSeconds:F3}s {value}"); };
        string? error = null;
        CallDiagnostics? failureDiagnostics = null;
        var muteLevels = new List<double>(); var activeLevels = new List<double>();
        async Task Connected()
        {
            var clock = Stopwatch.StartNew();
            while (session.IsBusy || engine.GetDiagnostics().Peers.Count(p => p.State == "connected") != 1)
            { if (clock.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException("Product peer not ready"); await Task.Delay(200); }
        }
        async Task Window(string phase, int seconds)
        {
            sdkLog.Phase = phase;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < seconds)
            {
                var sample = engine.GetDiagnostics();
                if (!sample.InCall || sample.Peers.Length != 1 || sample.Peers[0].State != "connected") throw new InvalidOperationException("Product media disconnected");
                if (sample.Peers[0].Audio.DeviceError is not null) throw new InvalidOperationException("Product output failed");
                samples.Add(new { phase, sample });
                if (clock.Elapsed.TotalSeconds > 1 && sample.Microphone?.LevelDbFs is { } level)
                { if (phase == "muted") muteLevels.Add(level); if (phase == "baseline" || phase == "unmuted") activeLevels.Add(level); }
                await Task.Delay(500);
            }
        }
        try
        {
            var request = CallRequest.Create(server, room, "product-" + role, AudioDeviceChoice.DefaultId(true), AudioDeviceChoice.DefaultId(false));
            await session.ConnectAsync(request); await Connected(); Console.WriteLine("Product connected: " + role);
            await Window("baseline", 8);
            engine.SetMicrophoneMuted(true); await Window("muted", 8);
            engine.SetMicrophoneMuted(false); await Window("unmuted", 6);
            var peer = engine.GetDiagnostics().Peers.Single(); engine.SetPeerVolume(peer.Id, 0); await Window("peer-muted", 3);
            engine.SetPeerVolume(peer.Id, 1); await Window("peer-restored", 3);
            engine.SetMicrophoneMuted(true);
            sdkLog.Phase = "recovery";
            session.RequestRecovery("Product forced recovery"); await Connected();
            if (!session.WantsCall || !session.State.Contains("재연결 완료", StringComparison.Ordinal)) throw new InvalidOperationException("Product recovery failed");
            await Window("muted", 4);
            engine.SetMicrophoneMuted(false); await Window("reconnected", 5);
            await Task.Delay(2000); // Let the other host finish its observation before the deliberate switch.
            sdkLog.Phase = "default-device-rejoin";
            await session.DisconnectAsync();
            await session.ConnectAsync(request with { CaptureId = null, RenderId = null }); await Connected();
            await Window("default-devices", 5);
            if (muteLevels.Count < 5 || muteLevels.Any(v => v > -119) || !activeLevels.Any(v => v > -50)) throw new InvalidOperationException("Microphone mute/unmute PCM verification failed");
            await Task.Delay(2000);
            await session.DisconnectAsync(); session.Observe(engine.GetDiagnostics());
            await Task.Delay(500);
            if (session.WantsCall || engine.GetDiagnostics().InCall) throw new InvalidOperationException("Leave rejoined unexpectedly");
        }
        catch (Exception ex) { error = ex.ToString(); failureDiagnostics = engine.GetDiagnostics(); }
        finally { await session.DisconnectAsync(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        string[] captured; lock (sync) captured = events.ToArray();
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { role, error, osMediaPorts, sdkBuild=SdkBuildIdentity.Read(), opusBackend=OpusBackend.Snapshot(), samples, events = captured,
            muteLevels, activeLevels, failureDiagnostics, sdkIceLog = sdkLog.Snapshot(),
            sdkReceiveDiagnostics = SdkReceiveDiagnostics.Snapshot(),
            notes = "Real shared capture/render and TURN using synthetic speech. Explicit endpoint selection, local PCM mute/unmute, peer gain control, full-room recovery and leave. No physical or subjective audio quality claim." }, DiagnosticJson.Readable));
        if (error is not null) throw new InvalidOperationException(error);
        Console.WriteLine("PASS: selected devices, live microphone mute/unmute, peer volume, reconnect and leave: " + role);
    }
}
