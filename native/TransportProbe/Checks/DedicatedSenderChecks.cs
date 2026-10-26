namespace VoiceNative;

internal static class DedicatedSenderChecks
{
    private static async Task CheckFreshBurstsAsync()
    {
        static void Require(bool value,string message) { if(!value) throw new InvalidOperationException(message); }
        var limits=AudioSendQueue.DedicatedLimits(256.0/48,2.5);
        Require(limits.Capacity==6 && limits.MaximumAgeMs==256.0/48,"Fractional device burst bounds");
        long now=0;
        var original=new AudioSendQueue(3,timestamp:()=>now);
        var candidate=new AudioSendQueue(limits.Capacity,limits.MaximumAgeMs,()=>now);
        foreach(var queue in new[]{original,candidate})
        {
            for(var i=0;i<3;i++)queue.Enqueue([1],samples:120);
            Require(queue.TryRead(out _),"First worker send missing");
            // Worker is busy on frame one; both adjacent batches remain fresh.
            now=1;
            for(var i=0;i<3;i++)queue.Enqueue([1],samples:120);
        }
        Require(original.Snapshot().DroppedFrames==2 && candidate.Snapshot().DroppedFrames==0,"Fresh overlap burst not preserved");
        var frames=new List<OutgoingAudio>();
        while(candidate.TryRead(out var frame))frames.Add(frame);
        Require(frames.Select(f=>f.Sequence).SequenceEqual(new long[]{2,3,4,5,6}),"Fresh burst reordered");
        Require(frames.Select(f=>f.SampleOffset).SequenceEqual(new long[]{120,240,360,480,600}),"Fresh burst lost RTP time");
        now=0;var timed=new AudioSendQueue(6,5,()=>now);
        timed.Enqueue([1],samples:120);
        now=(long)System.Math.Ceiling(5*System.Diagnostics.Stopwatch.Frequency/1000.0)+1;
        timed.Enqueue([2],samples:120);
        Require(timed.Snapshot().OldestPendingAgeMs>5,"Oldest age hidden");
        Require(timed.TryRead(out var retained) && retained.Sequence==2 && retained.SampleOffset==120,"Expired audio transmitted or time collapsed");
        Require(AudioSendQueue.Timestamp(1000,0,retained)==1120,"Expiry changed RTP offset");
        Require(timed.Snapshot() is {DroppedFrames:1,ExpiredFrames:1,PendingFrames:0},"Expiry accounting incorrect");
        now=0;var asyncQueue=new AudioSendQueue(2,5,()=>now);
        asyncQueue.Enqueue([1],samples:120);
        now=(long)System.Math.Ceiling(6*System.Diagnostics.Stopwatch.Frequency/1000.0);
        asyncQueue.Enqueue([2],samples:120);asyncQueue.Complete();
        var count=0;
        await foreach(var frame in asyncQueue.Read(CancellationToken.None)) { Require(frame.Sequence==2,"Async expiry not applied");count++; }
        Require(count==1 && asyncQueue.Snapshot().ExpiredFrames==1,"Async expiry accounting incorrect");
        foreach(var invalid in new[]{0,-1,double.NaN,double.PositiveInfinity,41})
        {
            try { _=new AudioSendQueue(1,invalid);throw new InvalidOperationException("Invalid age budget accepted"); }
            catch(ArgumentOutOfRangeException) { }
        }
        Console.WriteLine("PASS: fresh adjacent device batches preserved; actual-age expiry keeps RTP offsets and counts audio omissions.");
    }
    public static async Task RunAsync()
    {
        await CheckFreshBurstsAsync();
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
