namespace VoiceNative;

public interface ICallEngine : IAsyncDisposable
{
    event Action<string>? Status;
    Task JoinAsync(Uri address, string room, string name);
    Task LeaveAsync();
    void SetAudioDevices(string? captureId, string? renderId);
    void SetMicrophoneMuted(bool muted);
    void SetPeerVolume(string peerId, float volume);
    void SetLatencyExperiment(LatencyExperiment? experiment);
    CallDiagnostics GetDiagnostics();
    Task RestartOutputAsync();
    Task SetOutputLatencyAsync(int milliseconds);
    void SetLowLatencyPreference(bool enabled);
    void SetOpusProfile(OpusProfile profile);
    void SetRtpPlayoutPreference(bool enabled);
    void SetAdaptivePlayoutPreference(bool enabled);
    void SetPlayoutPolicy(PlayoutPolicy policy);
    void SetShortCapturePreference(bool enabled);
    void SetRawOutputPreference(bool enabled);
    void SetMediaInterface(string? address);
    Task MeasureAcousticLatencyAsync();
    Task MeasureLoopbackLatencyAsync();
}
