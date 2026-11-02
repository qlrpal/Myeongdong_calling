using NAudio.Wave;

namespace VoiceNative;

// Explicit device-test observer only. Normal playback never installs an observer.
internal interface IRenderProbe
{
    void Samples(ReadOnlySpan<byte> data, WaveFormat format);
    void Clock(long writtenFrames, double before, double after, ulong position, ulong qpc, ulong frequency);
}
