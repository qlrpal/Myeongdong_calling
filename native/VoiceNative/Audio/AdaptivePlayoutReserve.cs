namespace VoiceNative;

// Bounded feedback for arrival variation and missed deadlines. Packet-loss gaps
// are subtracted through the RTP clock; they are not themselves measured jitter.
internal sealed class AdaptivePlayoutReserve(int minimumSamples, bool enabled, bool responsive = false, bool shortFrames = false, int maximumShortSamples = 720, int shortQuietSeconds = 6, bool initialLearning = false)
{
    private readonly int floorSamples = minimumSamples;
    private readonly int maximumSamples = shortFrames ? Math.Max(minimumSamples, maximumShortSamples) : responsive ? Math.Max(minimumSamples, 1440) : Math.Min(1920, minimumSamples + 960);
    private readonly int stepSamples = shortFrames ? 120 : 480;
    private readonly Queue<double> excess = new(256);
    private readonly double[] sortedExcess = new double[256];
    private long? lastTimestamp;
    private double lastArrival, quietSamples, lateWindowSamples;
    private int latePressure, updates;
    private double learningSamples, healthSamples, concealedHealthSamples;
    private bool safetyFallback;
    public bool Frozen { get; private set; }
    public long Relearns { get; private set; }
    public string LearningState => !Enabled ? "Disabled" : !initialLearning ? "Continuous" : safetyFallback ? "ContinuousFallback" : Frozen ? "Holding" : "Learning";
    private void Relearn(bool safety = false)
    {
        if (Frozen) Relearns++;
        Frozen = false; learningSamples = healthSamples = concealedHealthSamples = 0;
        quietSamples = lateWindowSamples = 0; latePressure = 0;
        safetyFallback |= safety;
    }
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
            if (excess.Count == 256) excess.Dequeue();
            excess.Enqueue(Math.Clamp(arrivalMs - lastArrival - (timestamp - previous) / 48.0, 0, 100));
            if (++updates % 32 == 0)
            {
                excess.CopyTo(sortedExcess, 0);
                Array.Sort(sortedExcess, 0, excess.Count);
                JitterP95Ms = sortedExcess[(int)Math.Ceiling(excess.Count * 0.95) - 1];
            }
        }
        if (lastTimestamp is null || timestamp > lastTimestamp)
        { lastTimestamp = timestamp; lastArrival = arrivalMs; }
    }
    public void Late()
    {
        if (!Enabled) return;
        quietSamples = 0;
        if (++latePressure >= (responsive || shortFrames ? 2 : 3) && JitterP95Ms >= (shortFrames ? 2 : responsive ? 3 : 5)) { if (Frozen) Relearn(true); Raise(); latePressure = 0; }
    }
    public bool TryHold(int packetSamples = 0)
    {
        var amount = shortFrames && packetSamples > 0 ? packetSamples : stepSamples;
        if (!Enabled || TargetSamples + amount > maximumSamples) return false;
        if (Frozen) Relearn(true);
        Raise(amount); return true;
    }
    private void Raise(int amount = 0)
    {
        var next = Math.Min(maximumSamples, TargetSamples + (amount > 0 ? amount : stepSamples));
        if (next > TargetSamples) { TargetSamples = next; Increases++; }
        quietSamples = 0;
    }
    public void Advance(int outputSamples, bool concealed)
    {
        if (!Enabled) return;
        // Deadline pressure ages with output time even when the target is held.
        // Otherwise isolated late events accumulate indefinitely in Holding.
        lateWindowSamples += outputSamples;
        if (lateWindowSamples >= 48000) { lateWindowSamples = 0; latePressure = 0; }
        if (initialLearning && !safetyFallback)
        {
            healthSamples += outputSamples;
            if (concealed) concealedHealthSamples += outputSamples;
            // Persistent concealment or a required hold/late-target increase returns
            // to continuous control for this source. Freezing cannot disable safety.
            if (Frozen && healthSamples >= 3 * 48000)
            {
                if (concealedHealthSamples >= 9600) Relearn(true);
                healthSamples = concealedHealthSamples = 0;
            }
            if (!Frozen && (learningSamples += outputSamples) >= 30 * 48000 && TargetSamples == floorSamples)
            { Frozen = true; healthSamples = concealedHealthSamples = 0; }
            if (Frozen) return;
        }
        if (concealed) quietSamples = 0;
        else quietSamples += outputSamples;
        // Short-frame experiments can shorten the quiet interval independently.
        // All variants return one millisecond per second; late/PLC resets the wait.
        if (quietSamples >= (shortFrames ? shortQuietSeconds : responsive ? 6 : 31) * 48000 && TargetSamples > floorSamples)
        { TargetSamples = Math.Max(floorSamples, TargetSamples - 48); Decreases++; quietSamples -= 48000; }
    }
    public void Rebuffer()
    {
        if (initialLearning) Relearn();
        // Long gaps recover at the configured short-frame ceiling. Legacy and
        // responsive behavior remain separate from that bounded experiment.
        TargetSamples = shortFrames ? (Enabled ? maximumSamples : floorSamples)
            : responsive ? Math.Max(floorSamples, 960) : floorSamples;
        quietSamples = lateWindowSamples = 0; latePressure = 0;
    }
    public void Reset()
    {
        safetyFallback = false; Rebuffer(); excess.Clear(); lastTimestamp = null; lastArrival = 0;
        if (shortFrames) TargetSamples = floorSamples;
        updates = 0; JitterP95Ms = 0;
    }
}
