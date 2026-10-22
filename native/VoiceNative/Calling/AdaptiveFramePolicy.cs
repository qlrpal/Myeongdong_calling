namespace VoiceNative;

public sealed record ReceiverFrameFeedback(bool Supported, long ReceivedPackets, double DecodedMs,
    double ConcealedMs, long LatePackets, long SourceChanges, bool OutputHealthy);
public sealed record FrameAdaptationDiagnostics(double CurrentMs, double RequestedMs, bool AllPeersReady,
    string Reason, long Changes);

// Experimental, receiver-observed control. Route type alone never selects a
// longer frame. Failed trials roll back; quiet periods recover lower latency.
internal sealed class AdaptiveFramePolicy
{
    private ReceiverFrameFeedback? baseline;
    private double windowAt, changedAt = double.NegativeInfinity, quietMs;
    private int level, badWindows, trialWindows;
    private double? trialPressure;
    private static readonly double[] Frames = [2.5, 5, 10];
    public double TargetMs => Frames[level];
    public long Changes { get; private set; }
    public string Reason { get; private set; } = "waiting for receiver windows";

    public void Suspend() { baseline = null; badWindows = trialWindows = 0; quietMs = 0; trialPressure = null; }
    public void Observe(double now, ReceiverFrameFeedback feedback)
    {
        if (!feedback.Supported || !feedback.OutputHealthy) { Suspend(); Reason = "receiver unavailable"; return; }
        if (baseline is null || now < windowAt || feedback.SourceChanges != baseline.SourceChanges
            || feedback.ReceivedPackets < baseline.ReceivedPackets || feedback.DecodedMs < baseline.DecodedMs
            || feedback.ConcealedMs < baseline.ConcealedMs || feedback.LatePackets < baseline.LatePackets)
        { baseline = feedback; windowAt = now; badWindows = trialWindows = 0; quietMs = 0; trialPressure = null; return; }
        if (now - windowAt < 3) return;
        var elapsed = now - windowAt;
        var total = feedback.DecodedMs - baseline.DecodedMs + feedback.ConcealedMs - baseline.ConcealedMs;
        var packets = feedback.ReceivedPackets - baseline.ReceivedPackets;
        var concealed = feedback.ConcealedMs - baseline.ConcealedMs;
        var late = feedback.LatePackets - baseline.LatePackets;
        baseline = feedback; windowAt = now;
        if (total < 1000 || packets < 100) { badWindows = 0; quietMs = 0; Reason = "insufficient receiver media"; return; }
        var pressure = Math.Max(concealed / total, late / (double)packets);
        var bad = concealed / total > .01 || late / (double)packets > .005;
        if (trialPressure is { } before && ++trialWindows >= 2)
        {
            trialPressure = null; trialWindows = 0;
            if (pressure >= before * .9)
            { Change(level - 1, now, "longer-frame trial ineffective; rollback"); return; }
            Reason = "longer-frame trial improved receiver pressure";
        }
        quietMs = pressure <= .001 ? quietMs + elapsed * 1000 : 0;
        badWindows = bad ? badWindows + 1 : 0;
        if (!bad && level == 0) Reason = "healthy receiver; keep shortest frames";
        if (now - changedAt < 15) return;
        if (badWindows >= 2 && level < Frames.Length - 1)
        {
            Change(level + 1, now, "two bad receiver windows; bounded trial");
            trialPressure = pressure;
        }
        else if (quietMs >= 30000 && level > 0) Change(level - 1, now, "quiet receiver; recover shorter frames");
    }
    private void Change(int next, double now, string reason)
    {
        level = Math.Clamp(next, 0, Frames.Length - 1); changedAt = now;
        badWindows = trialWindows = 0; quietMs = 0; Changes++; Reason = reason;
    }
}
