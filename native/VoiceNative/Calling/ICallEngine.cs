namespace VoiceNative;

public interface ICallEngine : IAsyncDisposable
{
    event Action<string>? Status;
    Task JoinAsync(Uri address, string room, string name);
    Task LeaveAsync();
    CallDiagnostics GetDiagnostics();
    Task RestartOutputAsync();
    Task SetOutputLatencyAsync(int milliseconds);
    void SetLowLatencyPreference(bool enabled);
    void SetOpusProfile(OpusProfile profile);
    Task MeasureAcousticLatencyAsync();
    Task MeasureLoopbackLatencyAsync();
}
