namespace VoiceNative;

public sealed record ReceiverFrameFeedback(bool Supported, long ReceivedPackets, double DecodedMs,
    double ConcealedMs, long LatePackets, long SourceChanges, bool OutputHealthy,
    double? UnderfillMs = null, double? RecoveryDiscardedMs = null);
public sealed record FrameAdaptationDiagnostics(double CurrentMs, double RequestedMs, bool AllPeersReady,
    string Reason, long Changes);

// Experimental, receiver-observed control. Route type alone never selects a
// longer frame. Failed trials roll back; quiet periods recover lower latency.
internal sealed class AdaptiveFramePolicy
{
    private ReceiverFrameFeedback? baseline;
    private double windowAt, changedAt = double.NegativeInfinity, quietMs, retryAfter;
    private int level, badWindows, trialWindows;
    private Pressure? trialPressure;
    private Pressure trialSum;
    private readonly record struct Pressure(double Plc, double Late, double Underfill, double Discard)
    {
        public double Maximum => Math.Max(Math.Max(Plc, Late), Math.Max(Underfill, Discard));
        public bool WorseThan(Pressure previous) => Plc > previous.Plc + .0005 || Late > previous.Late + .0005
            || Underfill > previous.Underfill + .0005 || Discard > previous.Discard + .0005;
        public Pressure Add(Pressure other) => new(Plc + other.Plc, Late + other.Late, Underfill + other.Underfill, Discard + other.Discard);
        public Pressure Mean => new(Plc / 2, Late / 2, Underfill / 2, Discard / 2);
    }
    private static readonly double[] Frames = [2.5, 5, 10];
    public double TargetMs => Frames[level];
    public long Changes { get; private set; }
    public string Reason { get; private set; } = "waiting for receiver windows";

    public void Suspend() { baseline = null; badWindows = trialWindows = 0; quietMs = 0; trialPressure = null; }
    public void Observe(double now, ReceiverFrameFeedback feedback)
    {
        if (!feedback.Supported || !feedback.OutputHealthy || feedback.UnderfillMs is null || feedback.RecoveryDiscardedMs is null) { Suspend(); Reason = "receiver unavailable"; return; }
        if (baseline is null || now < windowAt || feedback.SourceChanges != baseline.SourceChanges
            || feedback.ReceivedPackets < baseline.ReceivedPackets || feedback.DecodedMs < baseline.DecodedMs
            || feedback.ConcealedMs < baseline.ConcealedMs || feedback.LatePackets < baseline.LatePackets
            || feedback.UnderfillMs < baseline.UnderfillMs || feedback.RecoveryDiscardedMs < baseline.RecoveryDiscardedMs)
        { baseline = feedback; windowAt = now; badWindows = trialWindows = 0; quietMs = 0; trialPressure = null; return; }
        if (now - windowAt < 3) return;
        var elapsed = now - windowAt;
        var total = feedback.DecodedMs - baseline.DecodedMs + feedback.ConcealedMs - baseline.ConcealedMs;
        var underfill = feedback.UnderfillMs.Value - baseline.UnderfillMs!.Value;
        var discarded = feedback.RecoveryDiscardedMs.Value - baseline.RecoveryDiscardedMs!.Value;
        var packets = feedback.ReceivedPackets - baseline.ReceivedPackets;
        var concealed = feedback.ConcealedMs - baseline.ConcealedMs;
        var late = feedback.LatePackets - baseline.LatePackets;
        baseline = feedback; windowAt = now;
        if (total < 1000 || packets < 100) { badWindows = 0; quietMs = 0; Reason = "insufficient receiver media"; return; }
        var pressure = new Pressure(concealed / total, late / (double)packets, underfill / (elapsed * 1000), discarded / (elapsed * 1000));
        var bad = pressure.Plc > .01 || pressure.Late > .005 || pressure.Underfill > .005 || pressure.Discard > .005;
        quietMs = pressure.Maximum <= .001 ? quietMs + elapsed * 1000 : 0;
        if (trialPressure is { } before)
        {
            if (pressure.WorseThan(before))
            {
                trialPressure = null; retryAfter = now + 60;
                Change(level - 1, now, "receiver component worsened; rollback and retry backoff"); return;
            }
            trialSum = trialSum.Add(pressure);
            if (++trialWindows < 2) return;
            var measured = trialSum.Mean;
            trialPressure = null; trialWindows = 0;
            if (measured.Maximum >= before.Maximum * .9 || measured.WorseThan(before))
            { retryAfter = now + 60; Change(level - 1, now, "longer-frame trial ineffective; rollback and retry backoff"); return; }
            Reason = "longer-frame trial improved receiver pressure";
        }
        badWindows = bad ? badWindows + 1 : 0;
        if (!bad && level == 0) Reason = "healthy receiver; keep shortest frames";
        if (now - changedAt < 15) return;
        if (badWindows >= 2 && level < Frames.Length - 1 && now >= retryAfter)
        {
            Change(level + 1, now, "two bad receiver windows; bounded trial");
            trialPressure = pressure; trialSum = default;
        }
        else if (quietMs >= 30000 && level > 0) Change(level - 1, now, "quiet receiver; recover shorter frames");
    }
    private void Change(int next, double now, string reason)
    {
        level = Math.Clamp(next, 0, Frames.Length - 1); changedAt = now;
        badWindows = trialWindows = 0; quietMs = 0; Changes++; Reason = reason;
    }
}
