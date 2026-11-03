using VoiceNative;

internal static class GroupQualityChecks
{
    internal static void Run()
    {
        static CallDiagnostics Call(int empty, bool missing = false)
        {
            var peers = Enumerable.Range(0, 5).Select(i => new PeerDiagnostics(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "fixture", "connected", "connected", "fixture",
                null, null, 0, 0, null, null, null, null, null,
                new AudioDiagnostics(null, 0, null, null, Render: missing && i == 0 ? null : new RenderDiagnostics(true, 6, 6, empty, empty)))).ToArray();
            return new(DateTimeOffset.UnixEpoch, true, null, peers);
        }
        var first = new Dictionary<int, CallDiagnostics> { [0] = Call(7), [1] = Call(2) };
        var last = new Dictionary<int, CallDiagnostics> { [0] = Call(10), [1] = Call(6) };
        var shared = LocalGroupQuality.RenderCounters(first, last, true);
        var separate = LocalGroupQuality.RenderCounters(first, last, false);
        if (shared.EmptyEvents != 7 || shared.SafetyFallbacks != 7 || shared.Streams != 2 || shared.MeasuredStreams != 2
            || separate.EmptyEvents != 35 || separate.Streams != 10 || separate.MeasuredStreams != 10)
            throw new InvalidOperationException("Shared render counters were multiplied by peer count.");
        last[0] = Call(10, missing: true);
        var partial = LocalGroupQuality.RenderCounters(first, last, true);
        if (partial.EmptyEvents is not null || partial.SafetyFallbacks is not null || partial.MeasuredStreams != 1
            || partial.ObservedEmptyEvents != 4 || partial.ObservedSafetyFallbacks != 4)
            throw new InvalidOperationException("Missing render counters became zero or hid observed events.");
        var empty = LocalGroupQuality.RenderCounters(new Dictionary<int, CallDiagnostics>(), new Dictionary<int, CallDiagnostics>(), true);
        if (empty.Streams != 0 || empty.EmptyEvents != 0) throw new InvalidOperationException("Empty output scope invented streams.");
        Console.WriteLine("PASS: group render counters counted per owned output, partial coverage preserves observed events and missing values.");
    }
}
