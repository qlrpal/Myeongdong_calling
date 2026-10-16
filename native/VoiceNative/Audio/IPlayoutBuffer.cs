using NAudio.Wave;

namespace VoiceNative;

internal interface IPlayoutBuffer : IWaveProvider
{
    Action<string, double, bool>? OnFrameSubmitted { get; set; }
    void ConfigureForOutput(bool lowLatency, int periodMs);
    void Suspend();
    void Resume();
    (double QueueMs, long Trims, long Underruns, double DecodedMs, double ConsumedMs,
        double RequestedMs, double UnderfillMs, double MaxUnderfillMs) Snapshot();
}
