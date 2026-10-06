namespace VoiceNative;

public interface ICallEngine : IAsyncDisposable
{
    event Action<string>? Status;
    Task JoinAsync(Uri server, string room, string name);
    Task LeaveAsync();
}
