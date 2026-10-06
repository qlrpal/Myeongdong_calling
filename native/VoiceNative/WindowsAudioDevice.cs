using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VoiceNative;

// Prototype device adapter. Keep the negotiated Opus SDP separate from mono PCM.
// Capture and playback use shared, event-driven WASAPI.
internal sealed class WindowsAudioDevice
{
    private readonly AudioEncoder encoder;
    private readonly WasapiRecorder? input;
    private WasapiPlayer? output;
    private readonly PcmPlayoutBuffer? buffer;
    private readonly object audioLock = new();
    private bool closed;
    private bool sinkStarted, sinkFailed, automaticRecoveryUsed;
    private string? sinkError;
    private long recoveries;
    private readonly SemaphoreSlim playbackGate = new(1);
    private long processedFrames;
    private double totalProcessingMs;
    private double? level;
    private readonly ProcessingWindow processing = new();
    private long? lastFrameAt;
    private long? lastCaptureAt;
    private readonly ProcessingWindow captureGaps = new();
    private readonly ProcessingWindow captureWork = new();
    private long captureDiscontinuities, captureTimestampErrors;
    private readonly AudioFormat pcmFormat = new(AudioCodecsEnum.OPUS, 111, 48000, 1);
    public event Action<uint, byte[]>? OnAudioSourceEncodedSample;
    public event Action<string>? OnAudioSourceError;
    public event Action<string>? OnAudioSinkError;
    public event Action<string>? OnAudioSinkRecovered;

    public WindowsAudioDevice(AudioEncoder encoder, bool disableSource = false, bool disableSink = false)
    {
        this.encoder = encoder;
        try
        {
            if (!disableSource)
            {
                // Initialise/JIT the lazy Opus encoder before the real capture callback.
                encoder.EncodeAudio(new short[960], pcmFormat);
                input = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(20)
                    .WithFormat(new WaveFormat(48000, 16, 1)).Build();
                input.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
                {
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Interlocked.Increment(ref captureDiscontinuities);
                    if ((flags & AudioClientBufferFlags.TimestampError) != 0) Interlocked.Increment(ref captureTimestampErrors);
                    Capture(data);
                };
                input.RecordingStopped += (_, args) => { if (args.Exception is not null) OnAudioSourceError?.Invoke(args.Exception.Message); };
            }
            if (!disableSink)
            {
                buffer = new PcmPlayoutBuffer();
                output = CreateOutput();
            }
        }
        catch { input?.Dispose(); output?.Dispose(); throw; }
    }
    private WasapiPlayer CreateOutput()
    {
        var player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
        try { player.Init(buffer!); }
        catch { player.Dispose(); throw; }
        player.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is null) return;
            lock (audioLock)
            {
                if (closed || !ReferenceEquals(output, player) || sinkFailed) return;
                sinkFailed = true; sinkError = args.Exception.Message; buffer?.Suspend();
                if (automaticRecoveryUsed)
                {
                    OnAudioSinkError?.Invoke(args.Exception.Message + " · 출력 재시작이 필요합니다.");
                    return;
                }
                automaticRecoveryUsed = true;
            }
            OnAudioSinkError?.Invoke(args.Exception.Message + " · 출력을 한 번 자동 복구합니다.");
            _ = Task.Run(async () =>
            {
                try
                {
                    await RestartOutputAsync(manual: false);
                    lock (audioLock) { if (closed || sinkFailed) return; }
                    OnAudioSinkRecovered?.Invoke("출력 자동 복구 완료");
                }
                catch (Exception ex) { OnAudioSinkError?.Invoke("자동 복구 실패: " + ex.Message + " · 장치를 연결한 뒤 출력 재시작을 누르세요."); }
            });
        };
        return player;
    }
    public List<AudioFormat> GetAudioSinkFormats() => encoder.SupportedFormats.Where(f => f.Codec == AudioCodecsEnum.OPUS).ToList();
    private void Capture(ReadOnlySpan<byte> captured)
    {
        var callbackStarted = Stopwatch.GetTimestamp();
        lock (audioLock)
        {
            if (closed) return;
            var captureAt = Stopwatch.GetTimestamp();
            if (lastCaptureAt is { } previousCapture) captureGaps.Add(Stopwatch.GetElapsedTime(previousCapture, captureAt).TotalMilliseconds);
            lastCaptureAt = captureAt;
            try
            {
                // Capture callbacks may return partial blocks; send complete 20ms frames.
                captureBytes.AddRange(captured.ToArray());
                while (captureBytes.Count >= 1920)
                {
                    var bytes = captureBytes.GetRange(0, 1920).ToArray(); captureBytes.RemoveRange(0, 1920);
                    var pcm = new short[960]; Buffer.BlockCopy(bytes, 0, pcm, 0, bytes.Length);
                    level = Level(pcm);
                    var started = Stopwatch.GetTimestamp();
                    var encoded = encoder.EncodeAudio(pcm, pcmFormat);
                    var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    totalProcessingMs += duration; processedFrames++; processing.Add(duration); lastFrameAt = Stopwatch.GetTimestamp();
                    OnAudioSourceEncodedSample?.Invoke(960, encoded);
                }
            }
            catch (Exception ex) { OnAudioSourceError?.Invoke(ex.Message); }
            finally { captureWork.Add(Stopwatch.GetElapsedTime(callbackStarted).TotalMilliseconds); }
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
                var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                totalProcessingMs += duration; processedFrames++; processing.Add(duration); lastFrameAt = Stopwatch.GetTimestamp();
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
        AudioDiagnostics snapshot;
        WasapiPlayer? currentOutput;
        bool queryDevice;
        lock (audioLock)
        {
            var playback = buffer?.Snapshot();
            var timing = processing.Snapshot();
            currentOutput = output; queryDevice = !closed && !sinkFailed;
            var work = captureWork.Snapshot();
            snapshot = new AudioDiagnostics(closed ? null : playback?.QueueMs,
                playback?.Trims ?? 0, level, processedFrames > 0 ? totalProcessingMs / processedFrames : null,
                playback?.Underruns, playback?.DecodedMs, buffer is null ? null : processedFrames,
                playback?.ConsumedMs, playback?.RequestedMs,
                null, null,
                output?.DeviceFriendlyName ?? (input is null ? null : "WASAPI 기본 입력"),
                output?.OutputWaveFormat.ToString() ?? input?.WaveFormat.ToString(),
                timing.P95, timing.Max, lastFrameAt is { } at ? Stopwatch.GetElapsedTime(at).TotalMilliseconds : null,
                closed ? "종료" : buffer is null ? "입력" : sinkFailed ? "장치 오류" : sinkStarted ? "재생" : "대기",
                sinkError, recoveries, captureGaps.Snapshot().P95, captureGaps.Snapshot().Max,
                work.P95, work.Max, Interlocked.Read(ref captureDiscontinuities), Interlocked.Read(ref captureTimestampErrors),
                playback?.UnderfillMs, playback?.MaxUnderfillMs);
        }
        // Core Audio calls can block during device reconfiguration. Never hold
        // the capture/decoder callback lock across these diagnostic-only queries.
        if (!queryDevice) return snapshot;
        try
        {
            return snapshot with
            {
                DeviceLatencyMs = currentOutput?.CurrentLatency.TotalMilliseconds ?? input?.CurrentLatency.TotalMilliseconds,
                DeviceAverageLatencyMs = currentOutput?.AverageLatency.TotalMilliseconds ?? input?.AverageLatency.TotalMilliseconds
            };
        }
        catch (Exception) { return snapshot; } // A concurrent close/restart makes this observation unavailable.
    }
    public Task StartAudio() { input?.StartRecording(); return Task.CompletedTask; }
    public async Task StartAudioSink()
    {
        await playbackGate.WaitAsync();
        try { lock (audioLock) { if (closed) return; sinkStarted = true; } output?.Play(); }
        finally { playbackGate.Release(); }
    }
    public async Task RestartOutputAsync(bool manual = true)
    {
        await playbackGate.WaitAsync();
        try
        {
            WasapiPlayer? old;
            lock (audioLock)
            {
                if (closed || buffer is null) return;
                if (manual) automaticRecoveryUsed = false;
                old = output; output = null; sinkFailed = true; buffer.Suspend();
            }
            old?.Stop(); old?.Dispose();
            var replacement = CreateOutput();
            bool play, dispose;
            lock (audioLock)
            {
                dispose = closed; play = sinkStarted;
                if (!dispose) { output = replacement; buffer.Resume(); sinkFailed = false; sinkError = null; recoveries++; }
            }
            if (dispose) { replacement.Dispose(); return; }
            if (play) replacement.Play();
        }
        catch (Exception ex) { lock (audioLock) { sinkFailed = true; sinkError = ex.Message; buffer?.Suspend(); } throw; }
        finally { playbackGate.Release(); }
    }
    public async Task Close()
    {
        lock (audioLock) { if (closed) return; closed = true; buffer?.Suspend(); }
        // Dispose outside the callback lock: stopping capture can wait for callbacks.
        if (input is not null) { input.StopRecording(); input.Dispose(); }
        await playbackGate.WaitAsync();
        try
        {
            WasapiPlayer? old;
            lock (audioLock) { old = output; output = null; }
            old?.Stop(); old?.Dispose();
        }
        finally { playbackGate.Release(); }
    }
}
