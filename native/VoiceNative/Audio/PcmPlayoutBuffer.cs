using NAudio.Wave;
namespace VoiceNative;

// Bounded PCM ring. Preserve a startup reserve, but don't retain stale speech after a burst.
internal sealed class PcmPlayoutBuffer : IWaveProvider
{
    private readonly object sync = new();
    private readonly byte[] audio = new byte[32768];
    private int head, count;
    private int startupBytes = 3840, maximumBytes = 7680, retainedBytes = 3840;
    private bool started, underflowActive, suspended;
    private readonly Queue<(long Position, string Hash)> markers = new();
    private long written, removed;
    private int fadeRemaining;
    private short lastOutputSample, fadeOrigin;
    public Action<string, double, bool>? OnFrameSubmitted { get; set; }
    private long underruns, trims, decodedSamples, consumedBytes, requestedBytes;
    private long underfillBytes, episodeBytes, maxEpisodeBytes;
    public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
    public void AddSamples(byte[] bytes, string? hash = null)
    {
        if (bytes.Length % 2 != 0 || bytes.Length > 11520) throw new ArgumentException("PCM block must contain complete mono samples, at most 120ms.");
        lock (sync)
        {
            decodedSamples += bytes.Length / 2;
            if (suspended) return;
            if (hash is not null) markers.Enqueue((written, hash));
            written += bytes.Length;
            var tail = (head + count) % audio.Length;
            var first = Math.Min(bytes.Length, audio.Length - tail);
            bytes.AsSpan(0, first).CopyTo(audio.AsSpan(tail));
            bytes.AsSpan(first).CopyTo(audio);
            count += bytes.Length;
            if (count > maximumBytes) // Drop whole 10ms periods when backlog exceeds the bounded reserve.
            {
                var skip = (count - retainedBytes) / 960 * 960;
                head = (head + skip) % audio.Length; count -= skip; removed += skip;
                DrainMarkers(removed, true); trims++;
                fadeOrigin = lastOutputSample; fadeRemaining = 240; // 5ms transition, no extra buffering.
            }
        }
    }
    public void ConfigureForOutput(bool lowLatency, int periodMs)
    {
        lock (sync)
        {
            var reserve = lowLatency ? Math.Clamp(periodMs * 4, 40, 80) : 40;
            startupBytes = reserve * 96;
            maximumBytes = Math.Max(80, reserve * 2) * 96;
            retainedBytes = reserve * 96;
        }
    }
    public int Read(Span<byte> destination)
    {
        if (destination.Length % 2 != 0) throw new ArgumentException("PCM output must contain complete samples.");
        lock (sync)
        {
            destination.Clear(); requestedBytes += destination.Length;
            if (suspended) return destination.Length;
            if (!started)
            {
                if (count < Math.Max(startupBytes, Math.Min(destination.Length, maximumBytes))) return destination.Length; // Keep the known stable 40ms startup reserve.
                started = true;
            }
            var available = Math.Min(destination.Length, count);
            var first = Math.Min(available, audio.Length - head);
            audio.AsSpan(head, first).CopyTo(destination);
            audio.AsSpan(0, available - first).CopyTo(destination.Slice(first));
            head = (head + available) % audio.Length; count -= available;
            removed += available; DrainMarkers(removed, false); consumedBytes += available;
            for (var offset = 0; offset < available && fadeRemaining > 0; offset += 2)
            {
                var sample = (short)(destination[offset] | destination[offset + 1] << 8);
                var blended = (short)((fadeOrigin * fadeRemaining + sample * (240 - fadeRemaining)) / 240);
                destination[offset] = (byte)blended; destination[offset + 1] = (byte)(blended >> 8); fadeRemaining--;
            }
            if (available >= 2) lastOutputSample = (short)(destination[available - 2] | destination[available - 1] << 8);
            if (available < destination.Length)
            {
                if (!underflowActive) underruns++;
                underflowActive = true;
                var missing = destination.Length - available;
                underfillBytes += missing; episodeBytes += missing;
                maxEpisodeBytes = Math.Max(maxEpisodeBytes, episodeBytes);
                lastOutputSample = 0;
            }
            else { underflowActive = false; episodeBytes = 0; }
            return destination.Length;
        }
    }
    private void DrainMarkers(long before, bool discarded)
    {
        var at = ProbeClock.Now;
        while (markers.TryPeek(out var marker) && marker.Position < before)
        { markers.Dequeue(); OnFrameSubmitted?.Invoke(marker.Hash, at, discarded); }
    }
    public void Suspend() { lock (sync) { suspended = true; Reset(); } }
    public void Resume() { lock (sync) { suspended = false; Reset(); } }
    private void Reset()
    {
        head = count = 0; removed = written; DrainMarkers(removed, true);
        started = underflowActive = false; episodeBytes = 0; fadeRemaining = 0; lastOutputSample = 0;
    }
    public (double QueueMs, long Trims, long Underruns, double DecodedMs, double ConsumedMs, double RequestedMs, double UnderfillMs, double MaxUnderfillMs) Snapshot()
    {
        lock (sync) return (count / 96.0, trims, underruns, decodedSamples / 48.0, consumedBytes / 96.0, requestedBytes / 96.0, underfillBytes / 96.0, maxEpisodeBytes / 96.0);
    }
}
