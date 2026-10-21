using System.Diagnostics;

namespace VoiceNative;

public sealed record AcousticLatencyResult(DateTimeOffset Timestamp, string Peer, double MinimumMs, double MaximumMs,
    double ClockUncertaintyMs, double DetectionResolutionMs, string Description, string MeasurementPath = "acoustic", AudioFrameStages? SourceStages = null, AudioFrameStages? ReceiverStages = null, AudioStageBreakdown? Stages = null);
internal sealed record ProbeMessage(string Kind, string Id, double A = 0, double B = 0, double C = 0, string? PayloadHash = null, AudioFrameStages? Stages = null);
internal static class ProbeClock
{
    public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public static (double Low, double High)? Bounds(double aSend, double bReceive, double bSend, double aReceive)
    {
        if (!new[] { aSend, bReceive, bSend, aReceive }.All(double.IsFinite) || aReceive < aSend || bSend < bReceive) return null;
        var low = bSend - aReceive; var high = bReceive - aSend;
        return !double.IsFinite(low) || !double.IsFinite(high) || !double.IsFinite(high - low) || high < low || high - low > 1 ? null : (low, high);
    }
}

// A voiced vowel-like harmonic signal and a bounded detector. No microphone audio is persisted.
internal sealed class AcousticCaptureProbe
{
    private static readonly short[] Signal = CreateSignal();
    internal static short[] DiagnosticSignal() => Signal.ToArray();
    private readonly object sync = new();
    private TaskCompletionSource<double>? injected;
    private int toneFrame;
    private readonly short[] detectionFrame = new short[960];
    private int detectionCount;
    private double detectionFrameAt;
    private string? detectionId;
    private Action<double>? detected;
    private double expires, onset;
    private int matches;
    public bool IsFirstInjectionFrame { get { lock (sync) return injected is not null && toneFrame == 0; } }
    public Task<double> Inject()
    {
        // Prepare the first-use waveform on the requesting thread, before enabling injection.
        // Lazy static initialization inside Process otherwise blocks the capture callback.
        if (Signal.Length != 19200) throw new InvalidOperationException("Invalid diagnostic waveform length.");
        lock (sync)
        {
            if (injected is not null) throw new InvalidOperationException("이미 테스트 신호를 생성 중입니다.");
            injected = new(TaskCreationOptions.RunContinuationsAsynchronously); toneFrame = 0;
            return injected.Task;
        }
    }
    public void Arm(string id, Action<double> callback)
    {
        lock (sync) { detectionId = id; detected = callback; expires = ProbeClock.Now + 8; matches = 0; detectionCount = 0; }
    }
    public void Cancel(string? id = null)
    {
        lock (sync)
        {
            if (id is null || id == detectionId) { detectionId = null; detected = null; matches = 0; }
            if (id is null) { injected?.TrySetCanceled(); injected = null; }
        }
    }
    public void Process(short[] pcm, double? capturedAt)
    {
        Action<double>? callback = null; double measured = 0;
        lock (sync)
        {
            if (detected is not null && ProbeClock.Now > expires) { detected = null; detectionId = null; }
            if (detected is not null && capturedAt is { } capture)
            {
                var offset = 0;
                while (offset < pcm.Length)
                {
                    if (detectionCount == 0) detectionFrameAt = capture + offset / 48000.0;
                    var copied = Math.Min(960 - detectionCount, pcm.Length - offset);
                    pcm.AsSpan(offset, copied).CopyTo(detectionFrame.AsSpan(detectionCount));
                    detectionCount += copied; offset += copied;
                    if (detectionCount < 960) continue;
                    detectionCount = 0;
                    if (IsTone(detectionFrame))
                    {
                        if (matches++ == 0) onset = detectionFrameAt;
                        if (matches >= 2) { callback = detected; measured = onset; detected = null; detectionId = null; break; }
                    }
                    else matches = 0;
                }
            }
            if (injected is not null)
            {
                if (capturedAt is null) { injected.TrySetException(new InvalidOperationException("유효한 WASAPI 캡처 시각이 없어 측정할 수 없습니다.")); injected = null; }
                else
                {
                    if (toneFrame == 0) injected.TrySetResult(capturedAt.Value);
                    for (var i = 0; i < pcm.Length; i++)
                    {
                        pcm[i] = (short)Math.Clamp(pcm[i] + Signal[toneFrame + i], short.MinValue, short.MaxValue);
                    }
                    if ((toneFrame += pcm.Length) >= Signal.Length) injected = null; // 400ms at either frame size.
                }
            }
        }
        if (callback is not null) _ = Task.Run(() => callback(measured));
    }
    private static short[] CreateSignal()
    {
        var samples = new short[19200];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var time = sample / 48000.0;
            var envelope = Math.Max(0, Math.Min(1, Math.Min(sample / 480.0, (19200 - sample) / 960.0)));
            var syllable = 0.7 + 0.3 * Math.Cos(2 * Math.PI * 5 * time);
            double tone = 0;
            for (var harmonic = 1; harmonic <= 16; harmonic++)
            {
                var frequency = harmonic * 200.0;
                var weight = 0.15 / harmonic + Math.Exp(-Math.Pow((frequency - 700) / 230, 2))
                    + 0.6 * Math.Exp(-Math.Pow((frequency - 1200) / 300, 2))
                    + 0.25 * Math.Exp(-Math.Pow((frequency - 2500) / 400, 2));
                tone += weight * Math.Sin(2 * Math.PI * frequency * time);
            }
            samples[sample] = (short)(tone * 0.045 * envelope * syllable * 32767);
        }
        return samples;
    }
    private static bool IsTone(ReadOnlySpan<short> samples)
    {
        double energy = 0;
        foreach (var sample in samples) energy += (double)sample * sample;
        if (energy / samples.Length < 4) return false;
        double harmonicPower = 0;
        for (var harmonic = 1; harmonic <= 16; harmonic++) harmonicPower += PowerAt(samples, harmonic * 200);
        return 2 * harmonicPower / (samples.Length * energy) > 0.55;
    }
    private static double PowerAt(ReadOnlySpan<short> samples, double frequency)
    {
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / 48000);
        double previous = 0, older = 0;
        foreach (var sample in samples) { var current = sample + coefficient * previous - older; older = previous; previous = current; }
        return previous * previous + older * older - coefficient * previous * older;
    }
}
