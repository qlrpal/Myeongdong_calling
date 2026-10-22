namespace VoiceNative;

internal static class AudioSendLifetimeChecks
{
    public static async Task RunAsync()
    {
        var lifetime = new AudioSendLifetime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportClosed = false;
        var sender = Task.Run(async () =>
        {
            Require(lifetime.TryEnter(), "Initial send rejected");
            try
            {
                entered.SetResult();
                await release.Task;
                Require(!Volatile.Read(ref transportClosed), "Transport disposed during send");
            }
            finally { lifetime.Exit(); }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var stop = lifetime.StopAsync();
            Require(!stop.IsCompleted, "Shutdown did not wait for active send");
            Require(!lifetime.TryEnter(), "Stale peer snapshot admitted a new send");
            Require(ReferenceEquals(stop, lifetime.StopAsync()), "Concurrent stop lost drain task");
            var other = new AudioSendLifetime();
            Require(other.TryEnter(), "Unrelated peer blocked by stopping peer");
            other.Exit();
            await other.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref transportClosed, true);
            await sender.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!lifetime.TryEnter(), "Stopped transport reopened");
            await lifetime.StopAsync();
        }
        finally { release.TrySetResult(); await sender.WaitAsync(TimeSpan.FromSeconds(5)); }

        var multiple = new AudioSendLifetime();
        Require(multiple.TryEnter() && multiple.TryEnter(), "Multiple active sends rejected");
        var drained = multiple.StopAsync();
        multiple.Exit();
        Require(!drained.IsCompleted, "Only one of two sends drained");
        multiple.Exit();
        await drained.WaitAsync(TimeSpan.FromSeconds(5));

        var exceptional = new AudioSendLifetime();
        Require(exceptional.TryEnter(), "Exception fixture entry");
        try { throw new IOException("Simulated send failure"); }
        catch (IOException) { }
        finally { exceptional.Exit(); }
        await exceptional.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        // The steady-state guard must not add one lease object per audio packet.
        var steady = new AudioSendLifetime();
        for (var i = 0; i < 1000; i++) { Require(steady.TryEnter(), "Warmup entry"); steady.Exit(); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) { Require(steady.TryEnter(), "Steady entry"); steady.Exit(); }
        Require(GC.GetAllocatedBytesForCurrentThread() == before, "Per-send guard allocated");
        await steady.StopAsync();
        Console.WriteLine("PASS: in-flight send drain, stale snapshot rejection, peer independence, repeated stop, exception release and allocation-free send guard.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
