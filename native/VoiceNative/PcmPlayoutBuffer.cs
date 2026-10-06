using NAudio.Wave;
namespace VoiceNative;

// Keep a small startup reserve. A burst must not empty the whole playback queue.
internal sealed class PcmPlayoutBuffer : IWaveProvider
{
    private readonly object sync = new();
    private readonly Queue<byte> audio = new();
    private bool started;
    private long underruns, trims, decodedSamples, consumedBytes, requestedBytes;
    public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
    public void AddSamples(byte[] bytes)
    {
        lock (sync)
        {
            decodedSamples += bytes.Length / 2;
            foreach (var value in bytes) audio.Enqueue(value);
            // Only bound a substantial stall; preserve queued audio through ordinary bursts.
            if (audio.Count > 23040) // 240ms
            {
                while (audio.Count > 11520) audio.Dequeue(); // retain 120ms, never clear to zero
                trims++;
            }
        }
    }
    public int Read(Span<byte> destination)
    {
        lock (sync)
        {
            destination.Clear();
            requestedBytes += destination.Length;
            if (!started)
            {
                if (audio.Count < 3840) return destination.Length; // 40ms initial reserve
                started = true;
            }
            var available = Math.Min(destination.Length, audio.Count);
            available -= available % 2;
            for (var i = 0; i < available; i++) destination[i] = audio.Dequeue();
            consumedBytes += available;
            if (available < destination.Length) { underruns++; started = false; }
            return destination.Length;
        }
    }
    public (double QueueMs, long Trims, long Underruns, double DecodedMs, double ConsumedMs, double RequestedMs) Snapshot()
    {
        lock (sync) return (audio.Count / 96.0, trims, underruns, decodedSamples / 48.0, consumedBytes / 96.0, requestedBytes / 96.0);
    }
}
