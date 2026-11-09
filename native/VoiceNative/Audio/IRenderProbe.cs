using NAudio.Wave;

namespace VoiceNative;

// Explicit device-test observer only. Normal playback never installs an observer.
internal interface IRenderProbe
{
    void Samples(ReadOnlySpan<byte> data, WaveFormat format);
    void Clock(long writtenFrames, double before, double after, ulong position, ulong qpc, ulong frequency);
    void Submitted(RenderSubmissionObservation observation) { }
}

// Sample range is [FirstFrame, FirstFrame + Frames). Release timestamps bracket
// the actual WASAPI API call; neither timestamp represents physical DAC output.
internal readonly record struct RenderSubmissionObservation(long FirstFrame, int Frames,
    double PcmReadyAt, double BeforeRelease, double AfterRelease);
