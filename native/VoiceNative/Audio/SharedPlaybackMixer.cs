using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceNative;

// No PCM FIFO: one device pull reads each independent peer timeline immediately.
// Membership changes publish a new array. Detach drains only that source's read
// lease before its owner may dispose the decoder/buffer.
internal sealed class SharedPlaybackMixer : IPlayoutBuffer, IDisposable
{
    private readonly object membership = new();
    private Source[] sources = [];
    // Preserve a single peer Read for ordinary fallback requests as well as
    // short device periods. Scratch capacity is not queued audio or added delay.
    private readonly byte[] scratch = new byte[9600];
    private readonly int[] sum = new int[4800];
    private readonly AutoResetEvent primingSignal = new(false);
    private bool disposed, configured, lowLatency;
    private int periodMs, priming = 1;
    private long clippedSamples, requestedSamples;
    public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
    public Action<string, double, bool>? OnFrameSubmitted { get; set; }
    internal WaitHandle PrimingSignal => primingSignal;
    internal int SourceCount => Volatile.Read(ref sources).Length;
    internal int ActiveSources => Volatile.Read(ref sources).Count(s => s.Active);
    internal long ClippedSamples => Interlocked.Read(ref clippedSamples);

    internal sealed class Source(SharedPlaybackMixer owner, IWaveProvider pcm, IPlayoutBuffer control) : IAsyncDisposable
    {
        internal IWaveProvider Pcm { get; } = pcm;
        internal IPlayoutBuffer Control { get; } = control;
        private readonly AudioSendLifetime lifetime = new();
        private int active;
        private string? error;
        internal string? Error => Volatile.Read(ref error);
        internal bool Active => Volatile.Read(ref active) != 0 && Error is null;
        internal bool Enter() => lifetime.TryEnter();
        internal void Exit() => lifetime.Exit();
        internal Task Drain() => lifetime.StopAsync();
        internal void Activate() { ClearError(); Volatile.Write(ref active, 1); owner.NotifyMedia(); }
        internal void ClearError() => Volatile.Write(ref error, null);
        internal void Fail(Exception failure) => Volatile.Write(ref error, failure.Message);
        public ValueTask DisposeAsync() => new(owner.RemoveAsync(this));
    }

    internal Source Add(IWaveProvider pcm, IPlayoutBuffer control)
    {
        if (pcm.WaveFormat.Encoding != WaveFormatEncoding.Pcm || pcm.WaveFormat.SampleRate != 48000
            || pcm.WaveFormat.BitsPerSample != 16 || pcm.WaveFormat.Channels != 1)
            throw new ArgumentException("Mixer sources must be 48kHz mono PCM16.", nameof(pcm));
        lock (membership)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sources.Length >= 6) throw new InvalidOperationException("Too many output sources.");
            if (configured) control.ConfigureForOutput(lowLatency, periodMs);
            var entry = new Source(this, pcm, control);
            Volatile.Write(ref sources, [.. sources, entry]);
            return entry;
        }
    }
    private Task RemoveAsync(Source entry)
    {
        lock (membership)
            Volatile.Write(ref sources, sources.Where(s => !ReferenceEquals(s, entry)).ToArray());
        return entry.Drain();
    }
    internal void NotifyMedia()
    {
        // Only wake startup/restart priming, never issue an event syscall per
        // packet during steady playback. Disposal follows source detach/drain.
        if (Volatile.Read(ref priming) != 0)
        {
            try { primingSignal.Set(); }
            catch (ObjectDisposedException) { } // A final in-flight receive may race owner shutdown.
        }
    }
    internal bool CanPrimeOutput(int requiredSamples)
    {
        foreach (var source in Volatile.Read(ref sources))
        {
            if (!source.Active || !source.Enter()) continue;
            try
            {
                if (source.Control is not RtpOpusPlayoutBuffer rtp || rtp.CanPrimeOutput(requiredSamples)) return true;
            }
            finally { source.Exit(); }
        }
        return false;
    }
    public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public int Read(Span<byte> destination)
    {
        if ((destination.Length & 1) != 0) throw new ArgumentException("PCM16 requires whole samples.", nameof(destination));
        Volatile.Write(ref priming, 0);
        var current = Volatile.Read(ref sources);
        long clipped = 0;
        for (var offset = 0; offset < destination.Length;)
        {
            var count = Math.Min(scratch.Length, destination.Length - offset);
            var samples = count / 2;
            sum.AsSpan(0, samples).Clear();
            foreach (var source in current)
            {
                if (!source.Active || !source.Enter()) continue;
                try
                {
                    var data = scratch.AsSpan(0, count);
                    data.Clear();
                    var read = source.Pcm.Read(data);
                    if (read < 0 || read > count || (read & 1) != 0) throw new InvalidOperationException("Invalid peer PCM length.");
                    var pcm = MemoryMarshal.Cast<byte, short>(data[..read]);
                    for (var i = 0; i < pcm.Length; i++) sum[i] += pcm[i];
                }
                catch (Exception error) { source.Fail(error); }
                finally { source.Exit(); }
            }
            var output = MemoryMarshal.Cast<byte, short>(destination.Slice(offset, count));
            for (var i = 0; i < samples; i++)
            {
                var value = sum[i];
                if (value is < short.MinValue or > short.MaxValue) clipped++;
                output[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            }
            offset += count;
        }
        Interlocked.Add(ref clippedSamples, clipped);
        Interlocked.Add(ref requestedSamples, destination.Length / 2);
        return destination.Length;
    }
    private void Each(Action<IPlayoutBuffer> action)
    {
        foreach (var source in Volatile.Read(ref sources))
        {
            if (!source.Enter()) continue;
            try { action(source.Control); }
            finally { source.Exit(); }
        }
    }
    public void ConfigureForOutput(bool lowLatency, int periodMs)
    {
        lock (membership)
        {
            this.lowLatency = lowLatency; this.periodMs = periodMs; configured = true;
            Each(buffer => buffer.ConfigureForOutput(lowLatency, periodMs));
        }
    }
    public void Suspend() => Each(buffer => buffer.Suspend());
    public void Resume()
    {
        Volatile.Write(ref priming, 1);
        foreach (var source in Volatile.Read(ref sources))
        {
            if (!source.Enter()) continue;
            try { source.Control.Resume(); source.ClearError(); }
            finally { source.Exit(); }
        }
    }
    public (double QueueMs, long Trims, long Underruns, double DecodedMs, double ConsumedMs,
        double RequestedMs, double UnderfillMs, double MaxUnderfillMs) Snapshot() =>
        (0, 0, 0, 0, 0, Interlocked.Read(ref requestedSamples) / 48.0, 0, 0);
    public void Dispose()
    {
        Source[] closing;
        lock (membership)
        {
            if (disposed) return;
            disposed = true; Volatile.Write(ref priming, 0); closing = sources; Volatile.Write(ref sources, []);
        }
        foreach (var source in closing) source.Drain().GetAwaiter().GetResult();
        primingSignal.Dispose();
    }
}
