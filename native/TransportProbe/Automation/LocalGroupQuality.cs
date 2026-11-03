namespace VoiceNative;

internal sealed record GroupRenderCounters(long ObservedEmptyEvents, long ObservedSafetyFallbacks,
    int Streams, int MeasuredStreams)
{
    public long? EmptyEvents => Streams == MeasuredStreams ? ObservedEmptyEvents : null;
    public long? SafetyFallbacks => Streams == MeasuredStreams ? ObservedSafetyFallbacks : null;
}

internal static class LocalGroupQuality
{
    internal static GroupRenderCounters RenderCounters(IReadOnlyDictionary<int, CallDiagnostics> first,
        IReadOnlyDictionary<int, CallDiagnostics> last, bool shared)
    {
        long empty = 0, safety = 0;
        var streams = 0; var measured = 0;
        foreach (var (slot, call) in last)
        {
            // Shared sinks forward the same device's render counters to every
            // peer. Count that owned output once, not once per mixer source.
            foreach (var peer in shared ? call.Peers.Take(1) : call.Peers)
            {
                streams++;
                var before = first[slot].Peers.Single(p => p.Id == peer.Id).Audio.Render;
                if (peer.Audio.Render is not { } now || before is null) continue;
                measured++;
                empty += now.EmptyEvents - before.EmptyEvents;
                safety += now.SafetyFallbacks - before.SafetyFallbacks;
            }
        }
        return new(empty, safety, streams, measured);
    }
}
