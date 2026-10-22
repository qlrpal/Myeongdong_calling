namespace VoiceNative;

internal static class DedicatedSenderChecks
{
    public static async Task RunAsync()
    {
        if (AudioSendQueue.BurstCapacity(256.0 / 48, 2.5) != 3 || AudioSendQueue.BurstCapacity(10, 2.5) != 4
            || AudioSendQueue.BurstCapacity(10, 5) != 2 || AudioSendQueue.BurstCapacity(double.NaN, 2.5) != 4
            || AudioSendQueue.BurstCapacity(100, 1) != 16)
            throw new InvalidOperationException("Capture burst capacity");
        var burstQueue = new AudioSendQueue(3);
        for (var i = 0; i < 3; i++) burstQueue.Enqueue([1]);
        var burstCount = 0;
        using (var burstSender = new DedicatedAudioSender(burstQueue, _ => Interlocked.Increment(ref burstCount)))
        {
            burstSender.Notify();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref burstCount) < 3) await Task.Delay(1, timeout.Token);
            burstSender.Stop(); await burstSender.Completion;
            if (burstQueue.Snapshot().DroppedFrames != 0) throw new InvalidOperationException("Device burst dropped siblings");
        }
        var queue = new AudioSendQueue(1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var received = new List<long>();
        using (var sender = new DedicatedAudioSender(queue, frame =>
        {
            lock (received) received.Add(frame.Sequence);
            if (frame.Sequence == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
        }))
        {
            try
            {
                queue.Enqueue([1]); sender.Notify();
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Dedicated send did not wake");
                for (var i = 2; i <= 6; i++) { queue.Enqueue([(byte)i]); sender.Notify(); }
                if (queue.Snapshot().DroppedFrames != 4) throw new InvalidOperationException("Dedicated backlog was not bounded");
                release.Set();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    lock (received) if (received.Count == 2) break;
                    await Task.Delay(1, timeout.Token);
                }
                sender.Stop(); await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                if (!received.SequenceEqual(new long[] { 1, 6 }) || sender.Failure is not null)
                    throw new InvalidOperationException("Dedicated sender lost latest-frame/order semantics");
            }
            finally { release.Set(); }
        }
        queue = new(1); entered.Reset(); release.Reset();
        using (var sender = new DedicatedAudioSender(queue, _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }))
        {
            try
            {
                queue.Enqueue([1]); sender.Notify();
                if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Stop fixture did not start");
                sender.Stop();
                if (sender.Completion.IsCompleted) throw new InvalidOperationException("Stop raced an active send");
                release.Set(); await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.Set(); }
        }
        queue = new(1);
        using (var sender = new DedicatedAudioSender(queue, _ => throw new IOException("Simulated sender failure")))
        {
            queue.Enqueue([1]); sender.Notify(); await sender.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (sender.Failure is null) throw new InvalidOperationException("Dedicated failure was hidden");
        }
        try { (LatencyExperiment.FromName("opus-adaptive")! with { DedicatedSend = true }).Validate(); throw new InvalidOperationException("Conflicting sender modes accepted"); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS: dedicated sender bounded backlog, coalesced wake/order, active/idle stop, exception reporting and conflicting mode rejection.");
    }
}
