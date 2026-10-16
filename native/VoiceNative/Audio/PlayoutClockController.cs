namespace VoiceNative;

// Queue feedback against the render sample clock; never uses wall-clock timers.
// This corrects consumption, not an estimate of the hardware oscillator's ppm.
internal sealed class PlayoutClockController(int reserveSamples, bool enabled, bool fixedReserveTarget = false)
{
    private double filteredQueue, learnedTarget, observedSamples;
    private bool initialized;
    private int targetReserveSamples = reserveSamples;
    public double Rate { get; private set; } = 1;
    public bool Enabled { get; } = enabled;
    public double TargetSamples => learnedTarget > 0 ? learnedTarget : targetReserveSamples;
    public double? FilteredQueueSamples => initialized ? filteredQueue : null;
    public void Observe(double queueSamples, int outputSamples)
    {
        if (!Enabled || outputSamples == 0) return;
        if (!initialized) { filteredQueue = queueSamples; initialized = true; }
        filteredQueue += (queueSamples - filteredQueue) * Math.Min(1, outputSamples / 24000.0);
        observedSamples += outputSamples;
        if (observedSamples <= 96000)
        {
            // Baseline learns at most one extra packet of phase/jitter.
            // The opt-in diagnostic comparison keeps the explicit reserve target.
            learnedTarget = fixedReserveTarget ? targetReserveSamples : Math.Clamp(filteredQueue, targetReserveSamples, targetReserveSamples + 480);
            return;
        }
        var error = filteredQueue - learnedTarget;
        var outsideBand = Math.Sign(error) * Math.Max(0, Math.Abs(error) - 240); // 5ms deadband.
        var correction = Math.Clamp(outsideBand / 48000 * 0.1, -0.002, 0.01);
        Rate += (1 + correction - Rate) * Math.Min(1, outputSamples / 12000.0);
    }
    public void SetReserve(int samples)
    {
        if (samples == targetReserveSamples) return;
        learnedTarget = fixedReserveTarget ? samples : Math.Clamp(learnedTarget + samples - targetReserveSamples, samples, samples + 480);
        targetReserveSamples = samples;
    }
    public void Reset()
    {
        Rate = 1; initialized = false; observedSamples = filteredQueue = learnedTarget = 0;
    }
}
