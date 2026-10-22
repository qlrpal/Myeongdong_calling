
namespace VoiceNative;

// Experimental alternative to inline sends. Each wake drains the bounded
// latest-frame queue; it never polls or grows an unbounded backlog.
internal sealed class DedicatedAudioSender : IDisposable
{
    private readonly AudioSendQueue queue;
    private readonly Action<OutgoingAudio> send;
    private readonly AutoResetEvent ready = new(false);
    private readonly ManualResetEvent stop = new(false);
    private readonly Thread thread;
    private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool mmcssActive, disposed;
    private Exception? failure;
    private readonly bool criticalPriority;
    private AudioThreadPriority? scheduling;
    public Task Completion => completed.Task;
    public bool MmcssActive => Volatile.Read(ref mmcssActive);
    public string? Failure => Volatile.Read(ref failure)?.ToString();

    public DedicatedAudioSender(AudioSendQueue queue, Action<OutgoingAudio> send, bool criticalPriority = false)
    {
        this.criticalPriority = criticalPriority;
        this.queue = queue; this.send = send;
        thread = new Thread(Run) { IsBackground = true, Name = "Voice dedicated sender" };
        try { thread.Start(); }
        catch { ready.Dispose(); stop.Dispose(); throw; }
    }

    public void Notify() => ready.Set();
    public void Stop() => stop.Set();
    private void Run()
    {
        try
        {
            scheduling = new AudioThreadPriority("Pro Audio", criticalPriority);
            Volatile.Write(ref mmcssActive, scheduling.Active);
            WaitHandle[] signals = [stop, ready];
            while (!stop.WaitOne(0))
            {
                while (!stop.WaitOne(0) && queue.TryRead(out var frame))
                {
                    queue.Observe(frame); send(frame);
                }
                if (WaitHandle.WaitAny(signals) == 0) break;
            }
        }
        catch (Exception error) { Volatile.Write(ref failure, error); }
        finally
        {
            scheduling?.Dispose();
            completed.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Thread.CurrentThread == thread) throw new InvalidOperationException("Dispose sender outside its callback.");
        if (disposed) return;
        stop.Set(); thread.Join(); ready.Dispose(); stop.Dispose(); disposed = true;
    }

}
