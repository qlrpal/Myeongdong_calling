using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace VoiceNative;

// Prototype device adapter. Keep the negotiated Opus SDP separate from mono PCM.
// A later WASAPI adapter can replace this WaveIn/WaveOut implementation.
internal sealed class WindowsAudioDevice
{
    private readonly AudioEncoder encoder;
    private readonly WaveIn? input;
    private readonly WaveOut? output;
    private readonly BufferedWaveProvider? buffer;
    private readonly object audioLock = new();
    private bool closed;
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
                buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 1), TimeSpan.FromMilliseconds(120))
                { DiscardOnBufferOverflow = true, ReadFully = true };
                output = new WaveOut { DeviceNumber = -1, BufferMilliseconds = 20, NumberOfBuffers = 2 };
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
                    OnAudioSourceEncodedSample?.Invoke(960, encoder.EncodeAudio(pcm, pcmFormat));
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
                var pcm = encoder.DecodeAudio(frame.EncodedAudio, pcmFormat);
                var bytes = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                // Bound stale queued audio instead of accumulating latency after a stall.
                if (buffer.BufferedDuration.TotalMilliseconds > 80) buffer.ClearBuffer();
                buffer.AddSamples(bytes, 0, bytes.Length);
            }
            catch (Exception ex) { OnAudioSinkError?.Invoke(ex.Message); }
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
