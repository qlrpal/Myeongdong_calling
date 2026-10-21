namespace VoiceNative;

// Bounded feedback for arrival variation and missed deadlines. Packet-loss gaps
// are subtracted through the RTP clock; they are not themselves measured jitter.
internal sealed class AdaptivePlayoutReserve(int minimumSamples, bool enabled, bool responsive = false)
{
    private readonly int floorSamples = minimumSamples;
    private readonly int maximumSamples = responsive ? Math.Max(minimumSamples, 1440) : Math.Min(1920, minimumSamples + 960);
    private readonly Queue<double> excess = new();
    private long? lastTimestamp;
    private double lastArrival, quietSamples, lateWindowSamples;
    private int latePressure, updates;
    public bool Enabled { get; } = enabled;
    public int TargetSamples { get; private set; } = responsive ? Math.Max(minimumSamples, 960) : minimumSamples;
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
        if (++latePressure >= (responsive ? 2 : 3) && JitterP95Ms >= (responsive ? 3 : 5)) { Raise(); latePressure = 0; }
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
        // Legacy waits thirty quiet seconds; the responsive candidate waits five.
        // Both return one millisecond per second, without discarding audio.
        if (quietSamples >= (responsive ? 6 : 31) * 48000 && TargetSamples > floorSamples)
        { TargetSamples = Math.Max(floorSamples, TargetSamples - 48); Decreases++; quietSamples -= 48000; }
    }
    public void Rebuffer()
    {
        // A long outage exceeds bounded PLC. Legacy returns to its floor;
        // responsive restarts at 20ms and can relax again after quiet playback.
        TargetSamples = responsive ? Math.Max(floorSamples, 960) : floorSamples;
        quietSamples = lateWindowSamples = 0; latePressure = 0;
    }
    public void Reset()
    {
        Rebuffer(); excess.Clear(); lastTimestamp = null; lastArrival = 0;
        updates = 0; JitterP95Ms = 0;
    }
}
