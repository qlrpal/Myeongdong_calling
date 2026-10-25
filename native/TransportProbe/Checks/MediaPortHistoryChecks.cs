using VoiceNative;

internal static class MediaPortHistoryChecks
{
    internal static void Run()
    {
        var history = new MediaPortHistory();
        if (!history.TryActivate(52000,0,out var first) || history.TryActivate(52000,3600,out _))
            throw new InvalidOperationException("Active media port must remain reserved through a long call");
        history.Retire(first,3600);
        if (history.TryActivate(52000,4199.99,out _) || !history.TryActivate(52000,4200,out _))
            throw new InvalidOperationException("TURN port retention must start at close, not construction");
        var successes=0;
        Parallel.For(0,16,i=> { if(history.TryActivate(52002,4200,out _))Interlocked.Increment(ref successes); });
        if(successes!=1) throw new InvalidOperationException("Concurrent peers reused a media port");
        history.Retire(first,5000);
        if(history.TryActivate(52000,5000,out _)) throw new InvalidOperationException("Stale retire released a newer active lease");
        Console.WriteLine("PASS: media port history retains active/closed ports and serializes concurrent reuse");
    }
}
