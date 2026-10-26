using System.Diagnostics;
using Microsoft.Extensions.Logging;

// Probe-only structured metadata; never formats SDK messages containing credentials/SDP.
internal sealed class TurnSignalingLog : ILoggerFactory
{
    private sealed record Entry(double At, string Phase, string Category, string Level,
        string Template, Dictionary<string, string?> Values);
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    { "ErrorCode", "ErrorResponseCount", "RequestsSent", "SocketErrorCode", "SendResult", "Type", "MessageType", "CandidateCount", "Method", "RTPLocalEndPoint", "LocalEndPoint",
      "RemoteEndPoint", "LocalCandidate", "RemoteCandidate", "OldCandidate", "NewCandidate", "Duration" };
    private readonly object sync = new();
    private readonly Queue<Entry> entries = new();
    private string phase = "initial";
    private int evicted;
    internal string Phase { set => Volatile.Write(ref phase, value); }
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
    internal object Snapshot()
    {
        lock (sync) return new { evicted, entries = entries.ToArray(),
            limits = "Probe-only selected ICE/UDP SDK templates and whitelisted metadata. No formatted message, SDP, username, credential, nonce, transaction ID or packet bytes. At is host-local Stopwatch seconds." };
    }
    private sealed class Sink(TurnSignalingLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug &&
            (category.EndsWith("IceServer", StringComparison.Ordinal) ||
             category.EndsWith("IceServerResolver", StringComparison.Ordinal) ||
             category.EndsWith("RtpIceChannel", StringComparison.Ordinal) ||
             category.EndsWith("UdpReceiver", StringComparison.Ordinal));
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            string? template = null; var values = new Dictionary<string, string?>();
            foreach (var field in fields)
            {
                if (field.Key == "{OriginalFormat}") template = field.Value as string;
                else if (Allowed.Contains(field.Key)) values[field.Key] = field.Value?.ToString();
            }
            if (template is null) return;
            lock (owner.sync)
            {
                if (owner.entries.Count == 2048) { owner.entries.Dequeue(); owner.evicted++; }
                owner.entries.Enqueue(new(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                    Volatile.Read(ref owner.phase), category, logLevel.ToString(), template, values));
            }
        }
    }
}
