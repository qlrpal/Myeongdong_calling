namespace VoiceNative;

internal static class CallLifecycleChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static async Task RunAsync()
    {
        var order = new List<string>(); var cleanup = new CallCleanup();
        cleanup.Run("stop", () => { order.Add("stop"); throw new IOException("stop failed"); });
        await cleanup.RunAsync("dispose", () => { order.Add("dispose"); return Task.FromException(new InvalidOperationException("dispose failed")); });
        cleanup.Run("remaining", () => order.Add("remaining"));
        try { cleanup.ThrowIfFailed(); throw new InvalidOperationException("Missing cleanup errors"); }
        catch (CallCleanupException error)
        {
            Require(error.Stages.SequenceEqual(["stop", "dispose"]) && error.InnerExceptions.Count == 2 && order.SequenceEqual(["stop", "dispose", "remaining"]), "Cleanup masked failures or skipped resource");
            Require(!error.Message.Contains("stop failed", StringComparison.Ordinal) && error.InnerExceptions[0].Message == "stop failed", "User summary exposed internal details or lost original error");
        }

        var request = CallRequest.Create("http://localhost:12345/", "lifecycle", "test", null, null);
        var fake = new FakeEngine { FailJoin = true };
        await using (var session = new CallSession(fake))
        {
            var observed = 0;
            session.Changed += _ => throw new InvalidOperationException("Observer failure");
            session.Changed += _ => observed++;
            await ThrowsAsync<IOException>(() => session.ConnectAsync(request));
            Require(!session.WantsCall && !session.IsBusy && !fake.Allocated && observed > 0, "Partial join/observer failure leaked session");
            fake.FailJoin = false; await session.ConnectAsync(request); await session.DisconnectAsync();
            Require(!fake.Allocated, "Manual reconnect after join failure failed");
        }
        fake = new(); var backoff = Signal(); var waits = 0;
        await using (var session = new CallSession(fake, (_, token) => { Interlocked.Increment(ref waits); return backoff.Task.WaitAsync(token); }))
        {
            await session.ConnectAsync(request); fake.Connected = false;
            Parallel.For(0, 30, _ => session.RequestRecovery("duplicate"));
            Require(waits == 1 && session.IsBusy, "Duplicate recovery started multiple loops");
            await session.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(3)); backoff.TrySetResult();
            Require(fake.Joins == 1 && !fake.Allocated && !session.IsBusy && !session.WantsCall, "Leave failed to cancel recovery delay");
        }
        fake = new(); var blocked = Signal(); fake.JoinWait = blocked.Task;
        var racing = new CallSession(fake);
        var joining = racing.ConnectAsync(request);
        await ThrowsAsync<InvalidOperationException>(() => racing.ConnectAsync(request));
        var leaving = racing.DisconnectAsync();
        var firstDispose = racing.DisposeAsync().AsTask(); var secondDispose = racing.DisposeAsync().AsTask();
        Require(ReferenceEquals(firstDispose, secondDispose) && !firstDispose.IsCompleted, "Dispose did not share in-flight completion");
        blocked.TrySetResult();
        await Task.WhenAll(joining, leaving, firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(3));
        Require(!fake.Allocated && !racing.IsBusy && !racing.WantsCall && fake.Joins == 1, "Dispose/leave during join leaked state");
        await ThrowsAsync<ObjectDisposedException>(() => racing.ConnectAsync(request));
        await racing.DisconnectAsync();

        fake = new();
        await using (var session = new CallSession(fake, (_, _) => Task.CompletedTask))
        {
            await session.ConnectAsync(request); fake.FailLeave = 1; session.RequestRecovery("cleanup failure");
            Require(fake.Joins == 1 && !session.WantsCall && !session.IsBusy && session.State.Contains("정리", StringComparison.Ordinal), "Cleanup failure reopened capture automatically");
        }
        fake = new();
        await using (var session = new CallSession(fake, (_, _) => Task.CompletedTask))
        {
            await session.ConnectAsync(request); fake.CleanupError = "Recorded release failure"; fake.Connected = false;
            session.Observe(fake.GetDiagnostics());
            Require(!session.WantsCall && fake.Joins == 1 && !session.IsBusy, "Recorded cleanup warning started recovery");
        }
        fake = new();
        await using (var session = new CallSession(fake))
        {
            Task? leave = null;
            session.Changed += state => { if (state == "방에 연결 중") leave = session.DisconnectAsync(); };
            await session.ConnectAsync(request); if (leave is not null) await leave;
            Require(fake.Joins == 0 && !session.IsBusy && !session.WantsCall, "Observer-triggered leave started a new join");
        }
        fake = new();
        await using (var session = new CallSession(fake, (_, _) => Task.CompletedTask))
        {
            await session.ConnectAsync(request); fake.FailJoin = true; session.RequestRecovery("exhaust");
            Require(fake.Joins == 6 && !fake.Allocated && !session.IsBusy && !session.WantsCall, "Last failed retry retained media");
        }
        fake = new();
        await using (var session = new CallSession(fake, (_, _) => Task.FromException(new IOException("Backoff failed"))))
        {
            await session.ConnectAsync(request); session.RequestRecovery("backoff failure");
            Require(!fake.Allocated && !session.WantsCall && !session.IsBusy && session.State.Contains("중단", StringComparison.Ordinal), "Unexpected recovery error abandoned active media");
        }
        fake = new();
        await using (var session = new CallSession(fake))
        {
            await session.ConnectAsync(request); fake.FailLeave = 1;
            await ThrowsAsync<CallCleanupException>(session.DisconnectAsync);
            Require(!fake.Allocated && !session.IsBusy && !session.WantsCall && session.State.Contains("오류", StringComparison.Ordinal), "Cleanup failure reported idle/success or locked controller");
            await session.ConnectAsync(request); await session.DisconnectAsync();
        }
        Require(CallFailureMessage.Describe(new CallOperationException(CallFailureKind.InputDevice, "test", new IOException())).Contains("마이크", StringComparison.Ordinal)
            && CallFailureMessage.Describe(new CallOperationException(CallFailureKind.OutputDevice, "test", new IOException())).Contains("출력", StringComparison.Ordinal)
            && CallFailureMessage.Describe(new HttpRequestException()).Contains("서버", StringComparison.Ordinal), "Failure guidance conflated device and server");
        Console.WriteLine("PASS: all-stage cleanup errors, partial join, duplicate recovery, cancellation, concurrent leave/dispose, retry exhaustion, observer/backoff failure and guidance.");
    }

    private sealed class FakeEngine : ICallEngine
    {
        public bool Connected, Allocated, FailJoin;
        public int Joins, FailLeave;
        public Task? JoinWait;
        public string? CleanupError;
        public event Action<string>? Status { add { } remove { } }
        public async Task JoinAsync(Uri address, string room, string name)
        { Joins++; Allocated = true; if (JoinWait is not null) await JoinWait; if (FailJoin) throw new IOException("Partial join failure"); Connected = true; }
        public Task LeaveAsync()
        { Connected = Allocated = false; if (FailLeave-- > 0) throw new IOException("Cleanup reported failure"); return Task.CompletedTask; }
        public CallDiagnostics GetDiagnostics() => new(DateTimeOffset.Now, Connected, null, [], CleanupError: CleanupError);
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
