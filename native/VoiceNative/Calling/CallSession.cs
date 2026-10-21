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

// Session intent survives a transport failure; an explicit leave cancels retries.
internal sealed class CallSession(ICallEngine engine, Func<TimeSpan, CancellationToken, Task>? delay = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim operations = new(1);
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private CancellationTokenSource lifetime = new();
    private Task? recovery;
    private CallRequest? request;
    private volatile bool desired;
    private int busy;
    private int disposed;
    public bool WantsCall => desired;
    public bool IsBusy => Volatile.Read(ref busy) != 0;
    public string State { get; private set; } = "대기";
    public event Action<string>? Changed;
    private void Publish(string state) { State = state; Changed?.Invoke(state); }
    public async Task ConnectAsync(CallRequest next)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await operations.WaitAsync(); Interlocked.Increment(ref busy);
        try
        {
            lifetime.Dispose(); lifetime = new(); request = next; desired = true;
            Publish("방에 연결 중");
            engine.SetAudioDevices(next.CaptureId, next.RenderId);
            await engine.JoinAsync(next.Server, next.Room, next.Nickname);
            if (!desired || lifetime.IsCancellationRequested) { await engine.LeaveAsync(); Publish("대기"); return; }
            Publish("통화 중");
        }
        catch { desired = false; Publish("참가 실패 · 설정을 확인하고 다시 참가하세요"); throw; }
        finally { Interlocked.Decrement(ref busy); operations.Release(); }
    }
    public void Observe(CallDiagnostics snapshot)
    {
        if (desired && !snapshot.InCall && !IsBusy) RequestRecovery("서버 연결이 끊어졌습니다");
    }
    public void RequestRecovery(string reason)
    {
        if (Volatile.Read(ref disposed) != 0 || !desired || request is null || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        recovery = RecoverAsync(request, reason, lifetime.Token);
    }
    private async Task RecoverAsync(CallRequest current, string reason, CancellationToken token)
    {
        try
        {
            Publish(reason);
            for (var attempt = 0; attempt < 5 && desired; attempt++)
            {
                Publish($"재연결 중 · {attempt + 1}/5");
                await wait(TimeSpan.FromSeconds(Math.Min(15, 1 << attempt)), token);
                await operations.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await engine.LeaveAsync();
                    token.ThrowIfCancellationRequested();
                    engine.SetAudioDevices(current.CaptureId, current.RenderId);
                    await engine.JoinAsync(current.Server, current.Room, current.Nickname);
                    if (token.IsCancellationRequested || !desired) { await engine.LeaveAsync(); return; }
                    Publish("통화 중 · 재연결 완료"); return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { Publish("재연결 시도 실패 · " + ex.Message); }
                finally { operations.Release(); }
            }
            desired = false; Publish("재연결 실패 · 다시 참가하세요");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Interlocked.Decrement(ref busy); }
    }
    public async Task DisconnectAsync()
    {
        desired = false; lifetime.Cancel();
        if (recovery is not null) await recovery;
        await operations.WaitAsync(); Interlocked.Increment(ref busy);
        try { await engine.LeaveAsync(); Publish("대기"); }
        finally { Interlocked.Decrement(ref busy); operations.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { await DisconnectAsync(); }
        finally { lifetime.Dispose(); operations.Dispose(); }
    }
}
