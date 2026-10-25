using NAudio.Wave;

namespace VoiceNative;

internal static class ProductChecks
{
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    public static async Task RunAsync(string directory)
    {
        Require(SdkBuildIdentity.Read().ProductionPatched,"Default product runtime must load the patched SDK");
        TurnSignalingLogChecks.Run();
        MediaPortHistoryChecks.Run();
        await AudioSendLifetimeChecks.RunAsync();
        PacketStorageChecks.Run();
        await DedicatedSenderChecks.RunAsync();
        await AdaptiveFrameChecks.RunAsync();
        ClockEstimatorChecks.Run();
        ReserveLearningChecks.Run();
        await using (var idleEngine = new WebRtcCallEngine())
            Require(idleEngine.GetDiagnostics().Sender is null, "Idle diagnostics require no active sender");
        var request = CallRequest.Create("https://example.com", "테스트_123", "사용자", null, null);
        foreach (var bad in new[] { "", "a b", new string('a', 41), "a\nb" })
        {
            try { CallRequest.Create("https://example.com", bad, "user", null, null); throw new InvalidOperationException("Invalid room accepted"); }
            catch (ArgumentException) { }
        }
        try { CallRequest.Create("https://user:secret@example.com", "room", "user", null, null); throw new InvalidOperationException("Credentials accepted"); }
        catch (ArgumentException) { }
        using (var source = new RawSourceWaveStream(new MemoryStream(new byte[] { 0xff, 0x7f, 0, 0x80, 0x10, 0, 0xf0, 0xff }), new WaveFormat(48000, 16, 1)))
        {
            var gain = new PlaybackGain(source) { Gain = 2 };
            var output = new byte[4]; gain.Read(output);
            Require(BitConverter.ToInt16(output, 0) == short.MaxValue && BitConverter.ToInt16(output, 2) == short.MinValue, "Gain clipping");
            gain.Gain = 0; gain.Read(output); Require(output.All(v => v == 0) && source.Position == 8, "Mute must consume timeline");
            try { gain.Gain = float.NaN; throw new InvalidOperationException("Nonfinite gain accepted"); } catch (ArgumentOutOfRangeException) { }
        }
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings-test.json");
        var settings = new UserSettings("https://example.com/", "한글방", "사용자", "capture", "render", true);
        settings.Save(path); Require(UserSettings.Load(path) == settings, "Settings round trip");
        File.WriteAllText(path, "{broken"); Require(UserSettings.Load(path) == UserSettings.Default, "Corrupt settings fallback");
        Require(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Temporary settings leak");
        var fake = new FakeEngine();
        await using (var session = new CallSession(fake, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }))
        {
            await session.ConnectAsync(request); fake.Failures = 2;
            fake.Connected = false; session.Observe(fake.GetDiagnostics());
            Require(session.WantsCall && !session.IsBusy && fake.Joins == 4, "Successful bounded retry");
            await session.DisconnectAsync(); session.Observe(fake.GetDiagnostics());
            Require(!session.WantsCall && fake.Joins == 4, "Intentional leave rejoined");
        }
        fake = new();
        await using (var session = new CallSession(fake, (_, token) => Task.Delay(Timeout.Infinite, token)))
        {
            await session.ConnectAsync(request); session.RequestRecovery("cancel");
            await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Require(!session.WantsCall && !session.IsBusy && fake.Joins == 1, "Cancel retry wait");
        }
        fake = new();
        await using (var session = new CallSession(fake, (_, _) => Task.CompletedTask))
        {
            await session.ConnectAsync(request); fake.Failures = 100; session.RequestRecovery("exhaust");
            Require(!session.WantsCall && !session.IsBusy && fake.Joins == 6, "Retry limit");
        }
        fake = new(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.JoinWait = gate.Task;
        await using (var session = new CallSession(fake))
        {
            var connect = session.ConnectAsync(request); var disconnect = session.DisconnectAsync();
            gate.SetResult(); await Task.WhenAll(connect, disconnect).WaitAsync(TimeSpan.FromSeconds(2));
            Require(!session.WantsCall && !fake.Connected, "Leave during join must release microphone/session");
        }
        fake = new(); gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var session = new CallSession(fake, (_, _) => Task.CompletedTask))
        {
            await session.ConnectAsync(request); fake.JoinWait = gate.Task;
            session.RequestRecovery("blocked join"); var disconnect = session.DisconnectAsync();
            gate.SetResult(); await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
            Require(!session.WantsCall && !fake.Connected && !session.IsBusy, "Cancel in-flight recovery join");
        }
        Console.WriteLine("PASS: room validation, clipping/mute consumption, readable settings, corruption recovery, retry success/limit/cancellation and leave during join.");
    }
    private sealed class FakeEngine : ICallEngine
    {
        public int Joins, Failures;
        public bool Connected;
        public Task? JoinWait;
        public event Action<string>? Status { add { } remove { } }
        public async Task JoinAsync(Uri address, string room, string name) { Joins++; if (JoinWait is not null) await JoinWait; if (Failures-- > 0) throw new IOException("Simulated connection failure"); Connected = true; }
        public Task LeaveAsync() { Connected = false; return Task.CompletedTask; }
        public CallDiagnostics GetDiagnostics() => new(DateTimeOffset.Now, Connected, null, []);
        public void SetAudioDevices(string? captureId, string? renderId) { }
        public void SetMicrophoneMuted(bool muted) { }
        public void SetPeerVolume(string peerId, float volume) { }
        public void SetLatencyExperiment(LatencyExperiment? experiment) { }
        public Task RestartOutputAsync() => Task.CompletedTask;
        public Task SetOutputLatencyAsync(int milliseconds) => Task.CompletedTask;
        public void SetLowLatencyPreference(bool enabled) { }
        public void SetOpusProfile(OpusProfile profile) { }
        public void SetRtpPlayoutPreference(bool enabled) { }
        public void SetAdaptivePlayoutPreference(bool enabled) { }
        public void SetPlayoutPolicy(PlayoutPolicy policy) { }
        public void SetShortCapturePreference(bool enabled) { }
        public void SetRawOutputPreference(bool enabled) { }
        public void SetMediaInterface(string? address) { }
        public Task MeasureAcousticLatencyAsync() => Task.CompletedTask;
        public Task MeasureLoopbackLatencyAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
