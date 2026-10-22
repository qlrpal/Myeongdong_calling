namespace VoiceNative;

// Explicit opt-in. Null options preserve every production default.
public sealed record LatencyExperiment(double FrameMs, double ReserveMs, bool Pcm = false,
    bool MinimumCapture = false, bool MinimalOutput = true, bool LatestSend = true,
    int BacklogMs = 20, int PlcLimitMs = 10, int Complexity = 3, bool InlineSend = true,
    double EmptyHoldMs = 0, int OutputHeadroomMs = 0, bool AdaptiveShortReserve = false, int MaxAdaptiveReserveMs = 15,
    int ShortReserveQuietSeconds = 6, bool StagedDeviceBuffers = true, bool PeriodWatchdog = true, bool RenderPeriodWatchdog = true, bool TraceRenderWork = false,
    bool DedicatedSend = false, bool AdaptiveFrames = false, bool ProAudioCapture = false, bool CriticalAudioPriority = false)
{
    public int FrameSamples => checked((int)(FrameMs * 48));
    public int ReserveSamples => checked((int)(ReserveMs * 48));
    public void Validate()
    {
        if (DedicatedSend && InlineSend) throw new ArgumentException("Choose either dedicated or inline sending.");
        if (AdaptiveFrames && (Pcm || FrameMs != 2.5 || !AdaptiveShortReserve))
            throw new ArgumentException("Adaptive frames require the 2.5ms adaptive Opus profile.");
        if (FrameMs is not (2.5 or 5 or 10) && !(Pcm && FrameMs == 1)) throw new ArgumentException("FrameMs must be 2.5/5/10, or 1 for PCM.");
        if (!double.IsFinite(ReserveMs) || ReserveMs is < 0 or > 20 || ReserveMs * 48 != ReserveSamples) throw new ArgumentException("Invalid sample-aligned reserve.");
        if (BacklogMs is < 10 or > 120 || BacklogMs < ReserveMs || PlcLimitMs is < 0 or > 60 || Complexity is < 0 or > 10) throw new ArgumentException("Invalid experiment bounds.");
        if (!double.IsFinite(EmptyHoldMs) || EmptyHoldMs < 0 || EmptyHoldMs > PlcLimitMs
            || EmptyHoldMs * 48 % FrameSamples != 0) throw new ArgumentException("Empty hold must be packet-aligned and within the PLC limit.");
        if (OutputHeadroomMs is < 0 or > 5) throw new ArgumentException("Output headroom must be 0–5ms.");
        if (TraceRenderWork && !StagedDeviceBuffers) throw new ArgumentException("Render work tracing requires staged device buffers.");
        if (ShortReserveQuietSeconds is < 2 or > 10) throw new ArgumentException("Short reserve quiet interval must be 2–10s.");
        if (AdaptiveShortReserve && (Pcm || ReserveMs < 5 || ReserveMs > MaxAdaptiveReserveMs
            || MaxAdaptiveReserveMs is < 5 or > 20)) throw new ArgumentException("Short adaptive reserve requires Opus and a 5–20ms bounded range.");
    }
    public static string[] Names => ["기본 모드", "opus-adaptive", "opus-auto-frames", "opus-adaptive-5", "opus-5", "opus-2.5", "opus-2.5-r10", "opus-2.5-r10-h1", "opus-2.5-r5-hold", "opus-zero", "pcm-1", "pcm-2.5-r10"];
    public static LatencyExperiment? FromName(string name) => name switch
    {
        "기본 모드" or "baseline" => null,
        "opus-adaptive" => new(2.5, 5, PlcLimitMs: 30, EmptyHoldMs: 2.5, OutputHeadroomMs: 1, AdaptiveShortReserve: true, MaxAdaptiveReserveMs: 20),
        "opus-auto-frames" => new(2.5, 5, PlcLimitMs: 30, EmptyHoldMs: 2.5, OutputHeadroomMs: 1, AdaptiveShortReserve: true, MaxAdaptiveReserveMs: 20, AdaptiveFrames: true),
        "opus-adaptive-5" => new(5, 5, PlcLimitMs: 30, OutputHeadroomMs: 1, AdaptiveShortReserve: true, MaxAdaptiveReserveMs: 20),
        "opus-5" => new(5, 5),
        "opus-2.5" => new(2.5, 2.5),
        "opus-2.5-r10" => new(2.5, 10),
        "opus-2.5-r10-h1" => new(2.5, 10, EmptyHoldMs: 2.5, OutputHeadroomMs: 1),
        "opus-2.5-r5-hold" => new(2.5, 5, EmptyHoldMs: 2.5),
        "opus-zero" => new(2.5, 0),
        "pcm-1" => new(1, 0, Pcm: true),
        "pcm-2.5-r10" => new(2.5, 10, Pcm: true),
        _ => throw new ArgumentException("Unknown latency experiment: " + name)
    };
}
