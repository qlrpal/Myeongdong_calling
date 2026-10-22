using System.Runtime.InteropServices;

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
    public Task Completion => completed.Task;
    public bool MmcssActive => Volatile.Read(ref mmcssActive);
    public string? Failure => Volatile.Read(ref failure)?.ToString();

    public DedicatedAudioSender(AudioSendQueue queue, Action<OutgoingAudio> send)
    {
        this.queue = queue; this.send = send;
        thread = new Thread(Run) { IsBackground = true, Name = "Voice dedicated sender" };
        try { thread.Start(); }
        catch { ready.Dispose(); stop.Dispose(); throw; }
    }

    public void Notify() => ready.Set();
    public void Stop() => stop.Set();
    private void Run()
    {
        IntPtr priority = IntPtr.Zero;
        try
        {
            uint index = 0;
            priority = AvSetMmThreadCharacteristics("Pro Audio", ref index);
            Volatile.Write(ref mmcssActive, priority != IntPtr.Zero);
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
            if (priority != IntPtr.Zero) AvRevertMmThreadCharacteristics(priority);
            completed.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Thread.CurrentThread == thread) throw new InvalidOperationException("Dispose sender outside its callback.");
        if (disposed) return;
        stop.Set(); thread.Join(); ready.Dispose(); stop.Dispose(); disposed = true;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
