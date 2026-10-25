namespace VoiceNative;

// Bounded regression of remote RTP progress against local requested audio
// samples. No wall-clock comparison between hosts and no payload retention.
internal sealed class RtpClockEstimator
{
    private const int Capacity = 320;
    private readonly (long Output, long Source)[] points = new (long, long)[Capacity];
    private int count, next;
    private long lastOutput, lastFit;
    public double Bias { get; private set; }
    public bool Ready { get; private set; }
    public void Reset() { count = next = 0; lastOutput = lastFit = 0; Bias = 0; Ready = false; }
    public void Observe(long output, long source)
    {
        if (output < lastOutput) Reset();
        if (output - lastOutput < 4800) return;
        points[next] = (output, source); next = (next + 1) % Capacity; count = Math.Min(count + 1, Capacity);
        lastOutput = output;
        if (output - lastFit < 48000 || count < 120) return;
        lastFit = output;
        var first = points[count == Capacity ? next : 0];
        if (output - first.Output < 12 * 48000) return;
        double sx = 0, sy = 0, xx = 0, xy = 0, yy = 0;
        for (var i = 0; i < count; i++)
        {
            var x = (double)(points[i].Output - first.Output);
            var y = (double)(points[i].Source - first.Source);
            sx += x; sy += y; xx += x * x; xy += x * y; yy += y * y;
        }
        var spread = xx - sx * sx / count;
        if (spread <= 0) return;
        var covariance = xy - sx * sy / count;
        var slope = covariance / spread;
        var residual = Math.Sqrt(Math.Max(0, (yy - sy * sy / count - slope * covariance) / count));
        // Congestion/pauses are not oscillator drift. Keep the previous bounded
        // estimate if the fit contains more than 10ms RMS of arrival-phase noise.
        if (!double.IsFinite(slope) || residual > 480 || Math.Abs(slope - 1) > .003) return;
        var target = Math.Clamp(slope - 1, -.001, .001);
        Bias = Ready ? Bias * .8 + target * .2 : target; Ready = true;
    }
}
