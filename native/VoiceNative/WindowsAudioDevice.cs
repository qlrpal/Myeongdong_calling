using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VoiceNative;

// Prototype device adapter. Keep the negotiated Opus SDP separate from mono PCM.
// Capture uses WaveIn; playback uses shared, event-driven WASAPI.
internal sealed class WindowsAudioDevice
{
    private readonly AudioEncoder encoder;
    private readonly WaveIn? input;
    private readonly WasapiPlayer? output;
    private readonly PcmPlayoutBuffer? buffer;
    private readonly object audioLock = new();
    private bool closed;
    private long processedFrames;
    private double totalProcessingMs;
    private double? level;
    private readonly AudioFormat pcmFormat = new(AudioCodecsEnum.OPUS, 111, 48000, 1);
    public event Action<uint, byte[]>? OnAudioSourceEncodedSample;
    public event Action<string>? OnAudioSourceError;
    public event Action<string>? OnAudioSinkError;

    public WindowsAudioDevice(AudioEncoder encoder, bool disableSource = false, bool disableSink = false)
    {
        this.encoder = encoder;
        try
        {
            if (!disableSource)
            {
                input = new WaveIn { DeviceNumber = -1, WaveFormat = new WaveFormat(48000, 16, 1), BufferMilliseconds = 20, NumberOfBuffers = 3 };
                input.DataAvailable += Capture;
                input.RecordingStopped += (_, args) => { if (args.Exception is not null) OnAudioSourceError?.Invoke(args.Exception.Message); };
            }
            if (!disableSink)
            {
                buffer = new PcmPlayoutBuffer();
                output = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
                output.Init(buffer);
                output.PlaybackStopped += (_, args) => { if (args.Exception is not null) OnAudioSinkError?.Invoke(args.Exception.Message); };
            }
        }
        catch { input?.Dispose(); output?.Dispose(); throw; }
    }
    public List<AudioFormat> GetAudioSinkFormats() => encoder.SupportedFormats.Where(f => f.Codec == AudioCodecsEnum.OPUS).ToList();
    private void Capture(object? sender, WaveInEventArgs args)
    {
        lock (audioLock)
        {
            if (closed) return;
            try
            {
                // WaveIn may return partial blocks; only send complete 20ms frames.
                captureBytes.AddRange(args.Buffer.AsSpan(0, args.BytesRecorded).ToArray());
                while (captureBytes.Count >= 1920)
                {
                    var bytes = captureBytes.GetRange(0, 1920).ToArray(); captureBytes.RemoveRange(0, 1920);
                    var pcm = new short[960]; Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                    level = Level(pcm);
                    var started = Stopwatch.GetTimestamp();
                    var encoded = encoder.EncodeAudio(pcm, pcmFormat);
                    totalProcessingMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; processedFrames++;
                    OnAudioSourceEncodedSample?.Invoke(960, encoded);
                }
            }
            catch (Exception ex) { OnAudioSourceError?.Invoke(ex.Message); }
        }
    }
    private readonly List<byte> captureBytes = [];
    public void GotEncodedMediaFrame(EncodedAudioFrame frame)
    {
        lock (audioLock)
        {
            if (closed || buffer is null) return;
            try
            {
                var started = Stopwatch.GetTimestamp();
                var pcm = encoder.DecodeAudio(frame.EncodedAudio, pcmFormat);
                totalProcessingMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; processedFrames++;
                level = Level(pcm);
                var bytes = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                buffer.AddSamples(bytes);
            }
            catch (Exception ex) { OnAudioSinkError?.Invoke(ex.Message); }
        }
    }
    private static double Level(short[] pcm)
    {
        var meanSquare = pcm.Length == 0 ? 0 : pcm.Sum(x => (double)x * x) / pcm.Length;
        return Math.Max(-120, 10 * Math.Log10(Math.Max(meanSquare, 0.000001) / (32768.0 * 32768)));
    }
    public AudioDiagnostics Snapshot()
    {
        lock (audioLock)
        {
            var playback = buffer?.Snapshot();
            return new AudioDiagnostics(closed ? null : playback?.QueueMs,
                playback?.Trims ?? 0, level, processedFrames > 0 ? totalProcessingMs / processedFrames : null,
                playback?.Underruns, playback?.DecodedMs, buffer is null ? null : processedFrames,
                playback?.ConsumedMs, playback?.RequestedMs);
        }
    }
    public Task StartAudio() { input?.StartRecording(); return Task.CompletedTask; }
    public Task StartAudioSink() { lock (audioLock) { if (!closed) output?.Play(); } return Task.CompletedTask; }
    public Task Close()
    {
        lock (audioLock) { if (closed) return Task.CompletedTask; closed = true; }
        // Dispose outside the callback lock: stopping capture can wait for callbacks.
        if (input is not null) { input.DataAvailable -= Capture; input.StopRecording(); input.Dispose(); }
        output?.Stop(); output?.Dispose();
        return Task.CompletedTask;
    }
}
