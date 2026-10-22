namespace VoiceNative;

// Protects a peer's transport from local disposal during an audio send. The
// monitor is held only for bookkeeping, never for encryption, I/O or shutdown.
internal sealed class AudioSendLifetime
{
    private readonly object sync = new();
    private bool stopping;
    private int active;
    private TaskCompletionSource? drained;

    public bool TryEnter()
    {
        lock (sync)
        {
            if (stopping) return false;
            active++;
            return true;
        }
    }

    public void Exit()
    {
        TaskCompletionSource? completion;
        lock (sync)
        {
            if (active == 0) throw new InvalidOperationException("Unbalanced audio send lifetime.");
            completion = --active == 0 ? drained : null;
        }
        completion?.TrySetResult();
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            if (active == 0) return Task.CompletedTask;
            return (drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }
}
