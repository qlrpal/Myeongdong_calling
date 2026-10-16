namespace VoiceNative;

// Bounded feedback for arrival variation and missed deadlines. Packet-loss gaps
// are subtracted through the RTP clock; they are not themselves measured jitter.
internal sealed class AdaptivePlayoutReserve(int minimumSamples, bool enabled)
{
    private readonly int floorSamples = minimumSamples;
    private readonly int maximumSamples = Math.Min(1920, minimumSamples + 960);
    private readonly Queue<double> excess = new();
    private long? lastTimestamp;
    private double lastArrival, quietSamples, lateWindowSamples;
    private int latePressure, updates;
    public bool Enabled { get; } = enabled;
    public int TargetSamples { get; private set; } = minimumSamples;
    public double JitterP95Ms { get; private set; }
    public long Increases { get; private set; }
    public long Decreases { get; private set; }

    public void Arrival(long timestamp, double arrivalMs)
    {
        if (!Enabled || !double.IsFinite(arrivalMs)) return;
        if (lastTimestamp is { } previous && timestamp > previous && arrivalMs >= lastArrival)
        {
            excess.Enqueue(Math.Clamp(arrivalMs - lastArrival - (timestamp - previous) / 48.0, 0, 100));
            if (excess.Count > 256) excess.Dequeue();
            if (++updates % 32 == 0)
            {
                var sorted = excess.Order().ToArray();
                JitterP95Ms = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
            }
        }
        if (lastTimestamp is null || timestamp > lastTimestamp)
        { lastTimestamp = timestamp; lastArrival = arrivalMs; }
    }
    public void Late()
    {
        if (!Enabled) return;
        quietSamples = 0;
        if (++latePressure >= 3 && JitterP95Ms >= 5) { Raise(); latePressure = 0; }
    }
    public bool TryHold()
    {
        if (!Enabled || TargetSamples + 480 > maximumSamples) return false;
        Raise(); return true;
    }
    private void Raise()
    {
        var next = Math.Min(maximumSamples, TargetSamples + 480);
        if (next > TargetSamples) { TargetSamples = next; Increases++; }
        quietSamples = 0;
    }
    public void Advance(int outputSamples, bool concealed)
    {
        if (!Enabled) return;
        lateWindowSamples += outputSamples;
        if (lateWindowSamples >= 48000) { lateWindowSamples = 0; latePressure = 0; }
        if (concealed) quietSamples = 0;
        else quietSamples += outputSamples;
        // Thirty quiet seconds first, then one millisecond per second back down.
        if (quietSamples >= 31 * 48000 && TargetSamples > floorSamples)
        { TargetSamples = Math.Max(floorSamples, TargetSamples - 48); Decreases++; quietSamples -= 48000; }
    }
    public void Rebuffer()
    {
        // A long outage exceeds bounded PLC. Restart from fresh packets at the
        // low-latency floor, rather than charging another 20ms for the outage.
        TargetSamples = floorSamples; quietSamples = lateWindowSamples = 0; latePressure = 0;
    }
    public void Reset()
    {
        Rebuffer(); excess.Clear(); lastTimestamp = null; lastArrival = 0;
        updates = 0; JitterP95Ms = 0;
    }
}
