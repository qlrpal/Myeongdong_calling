namespace VoiceNative;

// A closed UDP tuple can still name a live TURN allocation. Keep active ports and
// recently closed ports out of subsequent peer construction; never used on the media path.
internal sealed class MediaPortHistory(double retentionSeconds = 600)
{
    internal readonly record struct Lease(int Port, long Generation);
    private readonly object sync = new();
    private readonly Dictionary<int, long> active = new();
    private readonly Dictionary<int, double> closed = new();
    private long generation;
    internal bool TryActivate(int port, double now, out Lease lease)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        lock (sync)
        {
            foreach (var expired in closed.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) closed.Remove(expired);
            lease = default;
            if (active.ContainsKey(port) || closed.ContainsKey(port)) return false;
            lease = new(port, ++generation); active.Add(port, lease.Generation); return true;
        }
    }
    internal void Retire(Lease lease, double now)
    {
        lock (sync)
        {
            if (active.TryGetValue(lease.Port, out var owner) && owner == lease.Generation)
            { active.Remove(lease.Port); closed[lease.Port] = now + retentionSeconds; }
        }
    }
}
