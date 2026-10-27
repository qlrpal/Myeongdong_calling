namespace VoiceNative;

internal sealed class CallCleanupException(IReadOnlyList<(string Stage, Exception Error)> failures)
    : AggregateException(Summary(failures), failures.Select(f => f.Error))
{
    private readonly string summary = Summary(failures);
    public override string Message => summary;
    public string[] Stages { get; } = failures.Select(f => f.Stage).ToArray();
    private static string Summary(IReadOnlyList<(string Stage, Exception Error)> failures) =>
        "통화 정리 중 일부 오류: " + string.Join(", ", failures.Select(f => f.Stage).Distinct())
        + " · 진단을 저장하고 앱을 다시 시작하세요.";
}

// Control path only. Attempt every independent resource, then report failures
// together instead of masking the original error in a later finally block.
internal sealed class CallCleanup(Action<string>? afterStep = null)
{
    private readonly List<(string Stage, Exception Error)> failures = [];
    public void Run(string stage, Action action)
    {
        try { action(); afterStep?.Invoke(stage); }
        catch (Exception error) { failures.Add((stage, error)); }
    }
    public async Task RunAsync(string stage, Func<Task> action)
    {
        try { await action(); afterStep?.Invoke(stage); }
        catch (Exception error) { failures.Add((stage, error)); }
    }
    public void ThrowIfFailed()
    {
        if (failures.Count != 0) throw new CallCleanupException(failures);
    }
}

internal static class CallNotifications
{
    public static void Publish(Action<string>? listeners, string message)
    {
        if (listeners is null) return;
        foreach (Action<string> listener in listeners.GetInvocationList())
        {
            try { listener(message); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine("Call observer failed: " + error.GetType().Name); }
        }
    }
}

internal enum CallFailureKind { Server, ServerRejected, InputDevice, OutputDevice }
internal sealed class CallOperationException(CallFailureKind kind, string message, Exception inner) : Exception(message, inner)
{
    internal CallFailureKind Kind { get; } = kind;
}

internal static class CallFailureMessage
{
    internal static bool IsCleanupFailure(Exception error) => error is CallCleanupException
        || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsCleanupFailure)
        || error.InnerException is { } inner && IsCleanupFailure(inner);
    public static string Describe(Exception error) => error switch
    {
        CallCleanupException => error.Message,
        _ when IsCleanupFailure(error) => "통화 정리 오류가 함께 발생했습니다 · 진단을 저장하고 앱을 다시 시작하세요.",
        CallOperationException { Kind: CallFailureKind.InputDevice } => "마이크 연결·Windows 입력 장치를 확인하고 다시 참가하세요.",
        CallOperationException { Kind: CallFailureKind.OutputDevice } => "출력 장치 연결을 확인하고 다시 참가하세요.",
        CallOperationException { Kind: CallFailureKind.ServerRejected } => "서버에서 참가를 거절했습니다 · " + error.Message,
        CallOperationException { Kind: CallFailureKind.Server } or HttpRequestException or IOException or TaskCanceledException => "서버 주소와 네트워크를 확인하세요.",
        ArgumentException => error.Message,
        AggregateException => "여러 통화 오류가 발생했습니다 · 진단을 저장하고 앱을 다시 시작하세요.",
        _ => "진단을 저장하고 다시 참가하세요."
    };
}
