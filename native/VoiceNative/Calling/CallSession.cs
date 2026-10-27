namespace VoiceNative;

public sealed record CallRequest(Uri Server, string Room, string Nickname, string? CaptureId, string? RenderId)
{
    public static CallRequest Create(string server, string room, string nickname, string? captureId, string? renderId)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(room) || string.IsNullOrWhiteSpace(nickname))
            throw new ArgumentException("서버·방 이름·닉네임을 입력하세요.");
        if (!Uri.TryCreate(server.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var address)
            || address.Scheme is not ("https" or "http") || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("올바른 HTTP 또는 HTTPS 서버 주소를 입력하세요.");
        room = room.Trim(); nickname = nickname.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(room, @"^[\p{L}\p{N}_-]{1,40}$"))
            throw new ArgumentException("방 이름은 문자·숫자·밑줄·하이픈으로 1~40자 입력하세요.");
        if (nickname.Length is < 1 or > 24 || nickname.Any(char.IsControl)) throw new ArgumentException("닉네임은 줄바꿈 없이 1~24자 입력하세요.");
        return new(address, room, nickname, captureId, renderId);
    }
}

// Session intent survives transport failure. A leave cancels retries immediately;
// accepted control operations remain counted until they have released the gate.
internal sealed class CallSession(ICallEngine engine, Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim operations = new(1);
    private readonly object intent = new();
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private CancellationTokenSource lifetime = new();
    private Task? recovery;
    private TaskCompletionSource? idle, disposal;
    private CallRequest? request;
    private volatile bool desired;
    private bool disposed;
    private int busy;
    private string state = "대기";
    public bool WantsCall => desired;
    public bool IsBusy => Volatile.Read(ref busy) != 0;
    public string State => Volatile.Read(ref state);
    public event Action<string>? Changed;
    private void Publish(string value) { Volatile.Write(ref state, value); CallNotifications.Publish(Changed, value); }
    // Begin is called under intent; End follows operations.Release, so disposal
    // cannot destroy a semaphore still owned by an accepted operation.
    private void Begin() { if (++busy == 1) idle = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private void End() { lock (intent) { if (--busy == 0) idle?.TrySetResult(); } }

    public async Task ConnectAsync(CallRequest next)
    {
        CancellationToken token;
        lock (intent)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (busy != 0 || desired) throw new InvalidOperationException("통화 작업이 진행 중입니다. 완료 후 다시 참가하세요.");
            lifetime.Dispose(); lifetime = new(); recovery = null; request = next; desired = true;
            token = lifetime.Token; Begin();
        }
        var entered = false;
        try
        {
            await operations.WaitAsync(token); entered = true;
            token.ThrowIfCancellationRequested();
            Publish("방에 연결 중");
            token.ThrowIfCancellationRequested();
            engine.SetAudioDevices(next.CaptureId, next.RenderId);
            token.ThrowIfCancellationRequested();
            await engine.JoinAsync(next.Server, next.Room, next.Nickname);
            if (!desired || token.IsCancellationRequested) { await engine.LeaveAsync(); return; }
            Publish("통화 중");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Exception? cleanupError = null;
            if (entered) { try { await engine.LeaveAsync(); } catch (Exception failure) { cleanupError = failure; } }
            lock (intent) desired = false;
            if (cleanupError is not null)
            {
                Publish("참가 실패 후 정리 오류 · 진단을 저장하고 앱을 다시 시작하세요.");
                throw new AggregateException(State, error, cleanupError);
            }
            if (token.IsCancellationRequested) return;
            Publish("참가 실패 · " + CallFailureMessage.Describe(error)); throw;
        }
        finally { if (entered) operations.Release(); End(); }
    }
    public void Observe(CallDiagnostics snapshot)
    {
        if (snapshot.CleanupError is { } failure)
        {
            lock (intent)
            {
                if (disposed || !desired || busy != 0) return;
                desired = false; request = null;
            }
            Publish("자동 복구 중단 · " + failure);
            return;
        }
        if (desired && !snapshot.InCall && !IsBusy) RequestRecovery("서버 연결이 끊어졌습니다");
    }
    public void RequestRecovery(string reason)
    {
        CallRequest current; CancellationToken token; TaskCompletionSource completion;
        lock (intent)
        {
            if (disposed || !desired || request is null || busy != 0) return;
            current = request; token = lifetime.Token; Begin();
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); recovery = completion.Task;
        }
        _ = RecoverAsync(current, reason, completion, token);
    }
    private async Task RecoverAsync(CallRequest current, string reason, TaskCompletionSource completion, CancellationToken token)
    {
        try
        {
            Publish(reason);
            for (var attempt = 0; attempt < 5 && desired; attempt++)
            {
                token.ThrowIfCancellationRequested();
                Publish($"재연결 중 · {attempt + 1}/5");
                await wait(TimeSpan.FromSeconds(Math.Min(15, 1 << attempt)), token);
                await operations.WaitAsync(token);
                var cleaning = true;
                try
                {
                    token.ThrowIfCancellationRequested();
                    await engine.LeaveAsync(); token.ThrowIfCancellationRequested();
                    if (engine.GetDiagnostics().CleanupError is { } failure)
                    {
                        lock (intent) desired = false;
                        Publish("자동 복구 중단 · " + failure); return;
                    }
                    cleaning = false;
                    engine.SetAudioDevices(current.CaptureId, current.RenderId);
                    await engine.JoinAsync(current.Server, current.Room, current.Nickname);
                    if (token.IsCancellationRequested || !desired) { await engine.LeaveAsync(); return; }
                    Publish("통화 중 · 재연결 완료"); return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    // Engines can fail after partially allocating media. Never
                    // abandon the final failed attempt with resources attached.
                    Exception? cleanupFailure = null;
                    try { await engine.LeaveAsync(); }
                    catch (Exception failure) { cleanupFailure = failure; Publish("재연결 정리 오류 · " + CallFailureMessage.Describe(failure)); }
                    if (token.IsCancellationRequested) return;
                    if (cleaning || CallFailureMessage.IsCleanupFailure(error) || cleanupFailure is not null || engine.GetDiagnostics().CleanupError is not null)
                    {
                        lock (intent) desired = false;
                        Publish("자동 복구 중단 · 통화 정리 오류가 있어 앱을 다시 시작하세요."); return;
                    }
                    Publish("재연결 시도 실패 · " + CallFailureMessage.Describe(error));
                }
                finally { operations.Release(); }
            }
            if (!token.IsCancellationRequested) { lock (intent) desired = false; Publish("재연결 실패 · 설정을 확인하고 다시 참가하세요."); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await operations.WaitAsync(CancellationToken.None); // Cleanup must finish even after leave cancels retries.
            try { await engine.LeaveAsync(); }
            catch (Exception failure) { Publish("재연결 정리 오류 · " + CallFailureMessage.Describe(failure)); }
            finally { operations.Release(); }
            lock (intent) desired = false;
            Publish("재연결 중단 · " + CallFailureMessage.Describe(error));
        }
        finally { End(); completion.TrySetResult(); }
    }
    public Task DisconnectAsync() => StartDisconnect(disposing: false);
    private Task StartDisconnect(bool disposing)
    {
        Task? pending; Exception? cancelError = null;
        lock (intent)
        {
            if (disposed && !disposing) return disposal!.Task;
            desired = false; request = null;
            try { lifetime.Cancel(); } catch (Exception error) { cancelError = error; }
            pending = recovery; Begin();
        }
        return DisconnectCoreAsync(pending, cancelError);
    }
    private async Task DisconnectCoreAsync(Task? pending, Exception? cancelError)
    {
        var cleanup = new CallCleanup(); var entered = false;
        try
        {
            if (cancelError is not null) cleanup.Run("재연결 취소", () => throw cancelError);
            if (pending is not null) await cleanup.RunAsync("재연결 종료", () => pending);
            await operations.WaitAsync(); entered = true;
            await cleanup.RunAsync("통화 종료", engine.LeaveAsync);
            cleanup.ThrowIfFailed(); Publish("대기");
        }
        catch (Exception error) { Publish("통화 정리 오류 · " + CallFailureMessage.Describe(error)); throw; }
        finally { if (entered) operations.Release(); End(); }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (intent)
        {
            if (disposal is not null) return new(disposal.Task);
            disposed = true;
            disposal = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }
    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try { await StartDisconnect(disposing: true); }
        catch (Exception error) { failure = error; }
        Task pending;
        lock (intent) pending = idle?.Task ?? Task.CompletedTask;
        var cleanup = new CallCleanup();
        await cleanup.RunAsync("진행 작업 종료", () => pending);
        cleanup.Run("재연결 토큰 해제", lifetime.Dispose);
        cleanup.Run("통화 제어 해제", operations.Dispose);
        try { cleanup.ThrowIfFailed(); }
        catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
}
