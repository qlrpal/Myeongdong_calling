using System.Collections.Concurrent;

namespace VoiceNative;

// Monotonic local deadlines + repeated acknowledgements: no host clock subtraction.
internal sealed class PairBarrier(string run, string role, string configuration)
{
    private readonly ConcurrentDictionary<string, byte> seen = new(), acknowledged = new();
    private string remotePhase = "none";
    private string? error;
    internal static readonly string[] Phases = ["ready", "baseline", "muted", "active", "a-leave", "a-rejoined", "b-leave", "b-rejoined", "finish"];
    internal string? Error => Volatile.Read(ref error);
    internal void Receive(string message)
    {
        if (message.Length > 512) return;
        var fields = message.Split('|');
        if (fields.Length != 6 || fields[0] != run || fields[1] != (role == "a" ? "b" : "a") || fields[5] != "1"
            || !Phases.Contains(fields[3]) || fields[4] != "none" && !Phases.Contains(fields[4])) return;
        if (fields[2] != configuration) { Volatile.Write(ref error, "양쪽 설정 또는 빌드가 다릅니다."); return; }
        seen.TryAdd(fields[3], 0); acknowledged.TryAdd(fields[4], 0);
        Volatile.Write(ref remotePhase, fields[3]);
    }
    internal string Message(string phase) => string.Join('|', run, role, configuration, phase, Volatile.Read(ref remotePhase), "1");
    internal bool Ready(string phase) => Error is null && seen.ContainsKey(phase) && acknowledged.ContainsKey(phase);
}
