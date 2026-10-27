using NAudio.Wave;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;

namespace VoiceNative;

// Prototype device adapter. Keep the negotiated Opus SDP separate from mono PCM.
// Capture and playback use shared, event-driven WASAPI.
internal sealed class WindowsAudioDevice
{
    private readonly IAudioEncoder encoder;
    private readonly SharedCaptureRecorder? input;
    private readonly NativeCaptureConverter? captureConverter;
    private readonly byte[] convertedCapture = new byte[1920];
    private string? inputFallbackReason;
    private SharedRenderPlayer? output;
    private int requestedOutputMs;
    private readonly bool preferLowLatency;
    private readonly bool boundedRender, rawRender;
    private readonly AudioStreamCategory renderCategory;
    private readonly CaptureFrameSizes captureFrames;
    private int frameSamples => captureFrames.Samples;
    internal double FrameDurationMs => frameSamples / 48.0;
    internal void RequestFrameDuration(double milliseconds)
    {
        if (experiment?.AdaptiveFrames != true || milliseconds is not (2.5 or 5 or 10))
            throw new InvalidOperationException("Dynamic capture frames are not enabled.");
        captureFrames.Request((int)(milliseconds * 48));
    }
    private readonly IPlayoutBuffer? buffer;
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
    public AcousticCaptureProbe Acoustic { get; } = new();
    internal Action<short[]>? DiagnosticCaptureTransform { get; init; }
    public AudioFrameTimeline Timeline { get; } = new();
    public string? LastInjectedHash { get; private set; }
    public string? EncodedTraceHash { get; private set; }
    private double? frameCaptureAt;
    private long captureDiscontinuities, captureTimestampErrors;
    private readonly PipelineTrace? pipeline;
    private readonly string? renderDeviceId;
    private readonly LatencyExperiment? experiment;
    private readonly WindowsAudioDevice? sharedOutput;
    private SharedPlaybackMixer.Source? mixedSource;
    private readonly SharedPlaybackMixer? sharedMixer;
    private TaskCompletionSource? closeCompletion;
    private PlaybackGain? playbackGain;
    private bool muted;
    public void SetMuted(bool value) => Volatile.Write(ref muted, value);
    public void SetVolume(float value) { if (playbackGain is not null) playbackGain.Gain = value; }
    private double frameFirstCallbackAt;
    public CaptureOrigin? LastCaptureOrigin { get; private set; }
    private readonly AudioFormat pcmFormat = new(AudioCodecsEnum.OPUS, 111, 48000, 1);
    public event Action<uint, byte[]>? OnAudioSourceEncodedSample;
    public event Action<string>? OnAudioSourceError;
    public event Action<string>? OnAudioSinkError;
    public event Action<string>? OnAudioSinkRecovered;

    public WindowsAudioDevice(IAudioEncoder encoder, bool disableSource = false, bool disableSink = false, int outputLatencyMs = 40, bool preferLowLatency = true, double frameMs = 20, bool useRtpPlayout = false, bool adaptiveReserve = true, PipelineTrace? pipeline = null, string tracePeer = "", bool fixedReserveClockTarget = false, bool transientEmptyQueueHold = false, bool freshRestart = false, bool intermediateCapture = true, bool boundedRender = false, bool rawRender = false, AudioStreamCategory renderCategory = AudioStreamCategory.Other, int rtpReserveMs = 20, string? captureDeviceId = null, bool responsiveReserve = false, bool quickGapRecovery = false, string? renderDeviceId = null, LatencyExperiment? experiment = null, SharedPlaybackMixer? sharedMixer = null, WindowsAudioDevice? sharedOutput = null, IPlayoutBuffer? suppliedBuffer = null)
    {
        if (outputLatencyMs is not (20 or 40)) throw new ArgumentOutOfRangeException(nameof(outputLatencyMs));
        if (rtpReserveMs is < 10 or > 40) throw new ArgumentOutOfRangeException(nameof(rtpReserveMs));
        requestedOutputMs = outputLatencyMs;
        this.preferLowLatency = preferLowLatency;
        this.boundedRender = boundedRender; this.rawRender = rawRender; this.renderCategory = renderCategory;
        experiment?.Validate(); this.experiment = experiment;
        if (experiment is null && frameMs is not (10 or 20)) throw new ArgumentOutOfRangeException(nameof(frameMs));
        captureFrames = new(experiment?.FrameSamples ?? checked((int)(frameMs * 48)), experiment?.AdaptiveFrames == true);
        if (experiment?.Pcm == true) pcmFormat = new(AudioCodecsEnum.L16, 112, 48000, 1);
        this.encoder = encoder;
        this.pipeline = pipeline;
        this.renderDeviceId = renderDeviceId;
        if ((sharedMixer is null) != (sharedOutput is null) || (sharedOutput is not null || suppliedBuffer is not null) && (!disableSource || disableSink)
            || suppliedBuffer is not null && sharedOutput is not null) throw new ArgumentException("Shared output requires a sink-only device.");
        this.sharedMixer = sharedMixer; this.sharedOutput = sharedOutput;
        try
        {
            if (!disableSource)
            {
                // Initialise/JIT the lazy Opus encoder before the real capture callback.
                encoder.EncodeAudio(new short[frameSamples], pcmFormat);
                input = new SharedCaptureRecorder(preferLowLatency, intermediateCapture, captureDeviceId, experiment?.MinimumCapture == true,
                    experiment?.StagedDeviceBuffers == true, experiment?.PeriodWatchdog == true, experiment?.ProAudioCapture == true, experiment?.CriticalAudioPriority == true);
                captureConverter = new NativeCaptureConverter(input.WaveFormat);
                inputFallbackReason = input.LowLatencyUnavailableReason;
                input.DataAvailable += (data, flags, devicePosition, qpcPosition) =>
                {
                    var callbackAt = pipeline?.Enabled == true ? ProbeClock.Now : 0;
                    var callbackSamples = data.Length / input.WaveFormat.BlockAlign;
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0)
                    {
                        Interlocked.Increment(ref captureDiscontinuities);
                        lock (audioLock) { captureFrameCount = 0; frameCaptureAt = null; }
                    }
                    if ((flags & AudioClientBufferFlags.TimestampError) != 0) Interlocked.Increment(ref captureTimestampErrors);
                    var capturedAt = qpcPosition / 10000000.0;
                    double? stamp = (flags & AudioClientBufferFlags.TimestampError) == 0 && ProbeClock.Now - capturedAt is >= 0 and < 1 ? capturedAt : null;
                    if (captureConverter is null) Capture(data, stamp, callbackAt, callbackSamples, (int)flags);
                    else
                    {
                        for (var offset = 0; offset < data.Length;)
                        {
                            var frames = Math.Min(960, (data.Length - offset) / captureConverter.BlockAlign);
                            if (frames == 0) break;
                            var count = frames * captureConverter.BlockAlign;
                            var bytes = captureConverter.Convert(data.Slice(offset, count), convertedCapture);
                            Capture(convertedCapture.AsSpan(0, bytes), stamp is { } at ? at + offset / (double)captureConverter.BlockAlign / 48000 : null, callbackAt, callbackSamples, (int)flags);
                            offset += count;
                        }
                    }
                };
                input.RecordingStopped += (_, args) => { if (args.Exception is not null) OnAudioSourceError?.Invoke(args.Exception.Message); };
            }
            if (!disableSink)
            {
                if (useRtpPlayout && experiment?.StagedDeviceBuffers == true) PlayoutWarmup.Ensure(experiment);
                buffer = suppliedBuffer ?? (useRtpPlayout ? new RtpOpusPlayoutBuffer(Timeline, targetReserveMs: rtpReserveMs, adaptiveReserve: adaptiveReserve, pipeline: pipeline, tracePeer: tracePeer, fixedReserveClockTarget: fixedReserveClockTarget, transientEmptyQueueHold: transientEmptyQueueHold, freshRestart: freshRestart, responsiveReserve: responsiveReserve, recoveryReserveMs: quickGapRecovery ? 10 : 0, repeatedGapRecovery: quickGapRecovery, experiment: experiment) : new PcmPlayoutBuffer());
                buffer.OnFrameSubmitted = (hash, at, discarded) => Timeline.Update(hash, old => old with { OutputSubmittedAt = discarded ? null : at, Discarded = discarded });
                playbackGain = new PlaybackGain(buffer);
                if (sharedOutput is null) output = CreateOutput();
                else
                {
                    mixedSource = sharedMixer!.Add(playbackGain, buffer);
                    sharedOutput.OnAudioSinkError += SharedSinkError;
                    sharedOutput.OnAudioSinkRecovered += SharedSinkRecovered;
                }
            }
        }
        catch (Exception failure)
        {
            var cleanup = new CallCleanup();
            cleanup.Run("입력 초기화 정리", () => input?.Dispose());
            cleanup.Run("출력 초기화 정리", () => output?.Dispose());
            cleanup.Run("공통 출력 등록 정리", () => mixedSource?.DisposeAsync().AsTask().GetAwaiter().GetResult());
            cleanup.Run("재생 버퍼 초기화 정리", () => (buffer as IDisposable)?.Dispose());
            try { cleanup.ThrowIfFailed(); }
            catch (Exception cleanupFailure) { throw new AggregateException("오디오 초기화 후 정리 오류", failure, cleanupFailure); }
            throw;
        }
    }
    private void SharedSinkError(string message) { lock (audioLock) { if (closed) return; } OnAudioSinkError?.Invoke(message); }
    private void SharedSinkRecovered(string message) { lock (audioLock) { if (closed) return; } OnAudioSinkRecovered?.Invoke(message); }
    private SharedRenderPlayer CreateOutput()
    {
        var priming = experiment?.StagedDeviceBuffers == true ? buffer as RtpOpusPlayoutBuffer : null;
        var mixPriming = experiment?.StagedDeviceBuffers == true ? buffer as SharedPlaybackMixer : null;
        Func<int, bool>? canPrime = priming is not null ? priming.CanPrimeOutput : mixPriming is not null ? mixPriming.CanPrimeOutput : null;
        var player = new SharedRenderPlayer(requestedOutputMs, preferLowLatency, boundedRender, rawRender, renderCategory, renderDeviceId, experiment?.MinimalOutput == true, experiment?.OutputHeadroomMs ?? 0,
            experiment?.StagedDeviceBuffers == true, experiment?.RenderPeriodWatchdog == true, experiment?.TraceRenderWork == true,
            canPrime, priming?.PrimingSignal ?? mixPriming?.PrimingSignal, experiment?.CriticalAudioPriority == true);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = renderDeviceId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(renderDeviceId);
            using var client = endpoint.CreateAudioClient();
            var rate = client.MixFormat.SampleRate;
            IWaveProvider source = playbackGain!;
            if (preferLowLatency && rate != 48000) source = new WdlResamplingSampleProvider(source.ToSampleProvider(), rate).ToWaveProvider();
            player.Init(source);
            buffer!.ConfigureForOutput(player.LowLatencyActive, player.LatencyMilliseconds);
        }
        catch (Exception failure)
        {
            try { player.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException("출력 초기화 후 정리 오류", failure, cleanupFailure); }
            throw;
        }
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
    public string OutputDeviceId => sharedOutput?.OutputDeviceId ?? output?.DeviceId ?? throw new InvalidOperationException("출력 장치가 없습니다.");
    internal bool OwnsOutputStream => Volatile.Read(ref output) is not null;
    public List<AudioFormat> GetAudioSinkFormats() => experiment is not null ? [OpusAudioEncoder.ExperimentFormat(experiment)] : encoder.SupportedFormats.Where(f => f.Codec == AudioCodecsEnum.OPUS).ToList();
    internal void InjectSyntheticCapture(ReadOnlySpan<byte> pcm)
    {
        if (input is not null || buffer is not null) throw new InvalidOperationException("Synthetic injection requires a capture-free diagnostic source.");
        if ((pcm.Length & 1) != 0) throw new ArgumentException("PCM16 requires whole samples.", nameof(pcm));
        Capture(pcm, null, ProbeClock.Now, pcm.Length / 2, 0);
    }
    private void Capture(ReadOnlySpan<byte> captured, double? packetCaptureAt, double callbackAt, int callbackSamples, int flags)
    {
        var callbackStarted = Stopwatch.GetTimestamp();
        var tracing = pipeline?.Enabled == true;
        var callbackFrames = 0;
        lock (audioLock)
        {
            var lockAt = pipeline?.Enabled == true ? ProbeClock.Now : 0;
            if (closed) return;
            var captureAt = Stopwatch.GetTimestamp();
            if (lastCaptureAt is { } previousCapture) captureGaps.Add(Stopwatch.GetElapsedTime(previousCapture, captureAt).TotalMilliseconds);
            lastCaptureAt = captureAt;
            try
            {
                var offset = 0;
                while (offset < captured.Length)
                {
                    if (captureFrameCount == 0)
                    {
                        captureFrames.ApplyAtBoundary();
                        frameCaptureAt = packetCaptureAt is { } packetAt ? packetAt + offset / 96000.0 : null;
                        frameFirstCallbackAt = callbackAt;
                    }
                    var take = Math.Min(captureFrameBytes.Length - captureFrameCount, captured.Length - offset);
                    captured.Slice(offset, take).CopyTo(captureFrameBytes.AsSpan(captureFrameCount));
                    captureFrameCount += take; offset += take;
                    if (captureFrameCount < captureFrameBytes.Length) continue;
                    var frameReadyAt = pipeline?.Enabled == true ? ProbeClock.Now : 0;
                    captureFrameCount = 0;
                    var pcm = capturePcm;
                    Buffer.BlockCopy(captureFrameBytes, 0, pcm, 0, captureFrameBytes.Length);
                    DiagnosticCaptureTransform?.Invoke(pcm);
                    var firstProbeFrame = Acoustic.IsFirstInjectionFrame;
                    var capturedAt = frameCaptureAt;
                    Acoustic.Process(pcm, frameCaptureAt);
                    if (Volatile.Read(ref muted)) Array.Clear(pcm);
                    if (frameCaptureAt is not null) frameCaptureAt += frameSamples / 48000.0;
                    level = Level(pcm);
                    var started = Stopwatch.GetTimestamp();
                    var encoded = encoder.EncodeAudio(pcm, pcmFormat);
                    var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    LastCaptureOrigin = pipeline?.Enabled == true && frameFirstCallbackAt > 0 ? new(capturedAt, frameFirstCallbackAt,
                        callbackAt, callbackStarted / (double)Stopwatch.Frequency, lockAt, frameReadyAt,
                        started / (double)Stopwatch.Frequency, ProbeClock.Now, callbackSamples, flags) : null;
                    var hash = firstProbeFrame ? AudioFrameTimeline.Hash(encoded) : null;
                    if (hash is not null) TraceEncodedFrame(hash, capturedAt, started);
                    EncodedTraceHash = hash;
                    if (firstProbeFrame) LastInjectedHash = hash;
                    totalProcessingMs += duration; processedFrames++; processing.Add(duration); lastFrameAt = Stopwatch.GetTimestamp();
                    callbackFrames++;
                    OnAudioSourceEncodedSample?.Invoke((uint)frameSamples, encoded);
                }
            }
            catch (Exception ex) { OnAudioSourceError?.Invoke(ex.Message); }
            finally
            {
                captureWork.Add(Stopwatch.GetElapsedTime(callbackStarted).TotalMilliseconds);
                if (tracing) pipeline!.Captured(new(callbackStarted / (double)Stopwatch.Frequency,
                    lockAt, ProbeClock.Now, captured.Length / 2, callbackFrames));
            }
        }
    }
    private byte[] captureFrameBytes => captureFrames.Bytes;
    private short[] capturePcm => captureFrames.Pcm;
    private int captureFrameCount;
    public void GotEncodedMediaFrame(EncodedAudioFrame frame)
    {
        lock (audioLock)
        {
            if (closed || buffer is not PcmPlayoutBuffer pcmBuffer) return;
            try
            {
                var started = Stopwatch.GetTimestamp();
                var hash = Timeline.Enabled ? AudioFrameTimeline.Hash(frame.EncodedAudio) : null;
                var pcm = encoder.DecodeAudio(frame.EncodedAudio, pcmFormat);
                if (hash is not null) Timeline.Update(hash, old => old with { ReceivedAt = old.ReceivedAt ?? started / (double)Stopwatch.Frequency, DecodeStart = started / (double)Stopwatch.Frequency, DecodeEnd = ProbeClock.Now });
                var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                totalProcessingMs += duration; processedFrames++; processing.Add(duration); lastFrameAt = Stopwatch.GetTimestamp();
                level = Level(pcm);
                var bytes = new byte[pcm.Length * 2]; Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                if (hash is not null) Timeline.Update(hash, old => old with { QueuedAt = ProbeClock.Now });
                pcmBuffer.AddSamples(bytes, hash);
            }
            catch (Exception ex) { OnAudioSinkError?.Invoke(ex.Message); }
        }
    }
    public void GotRtpAudioPacket(uint source, uint timestamp, byte[] payload)
    {
        if (buffer is RtpOpusPlayoutBuffer rtp) rtp.Add(source, timestamp, payload);
        sharedMixer?.NotifyMedia();
    }
    private void TraceEncodedFrame(string hash, double? capturedAt, long started) =>
        Timeline.Update(hash, old => old with { CaptureAt = capturedAt, EncodeStart = started / (double)Stopwatch.Frequency, EncodeEnd = ProbeClock.Now });
    private static double Level(short[] pcm)
    {
        double energy = 0;
        for (var i = 0; i < pcm.Length; i++) energy += (double)pcm[i] * pcm[i];
        var meanSquare = pcm.Length == 0 ? 0 : energy / pcm.Length;
        return Math.Max(-120, 10 * Math.Log10(Math.Max(meanSquare, 0.000001) / (32768.0 * 32768)));
    }
    public AudioDiagnostics Snapshot()
    {
        AudioDiagnostics snapshot;
        SharedRenderPlayer? currentOutput;
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
                output?.DeviceFriendlyName ?? input?.DeviceFriendlyName,
                output?.OutputWaveFormat.ToString() ?? input?.WaveFormat.ToString(),
                timing.P95, timing.Max, lastFrameAt is { } at ? Stopwatch.GetElapsedTime(at).TotalMilliseconds : null,
                closed ? "종료" : buffer is null ? "입력" : sinkFailed ? "장치 오류" : sinkStarted ? "재생" : "대기",
                sinkError, recoveries, captureGaps.Snapshot().P95, captureGaps.Snapshot().Max,
                work.P95, work.Max, Interlocked.Read(ref captureDiscontinuities), Interlocked.Read(ref captureTimestampErrors),
                playback?.UnderfillMs, playback?.MaxUnderfillMs);
        }
        if (buffer is RtpOpusPlayoutBuffer rtp)
        {
            var detail = rtp.Details();
            snapshot = snapshot with
            {
                DecodedFrames = detail.DecodedPackets,
                ProcessingMs = detail.DecodeMeanMs,
                ProcessingP95Ms = detail.DecodeP95Ms,
                ProcessingMaxMs = detail.DecodeMaxMs,
                LastFrameAgeMs = detail.LastPacketAgeMs,
                LevelDbFs = detail.LevelDbFs,
                RtpPlayout = detail
            };
        }
        if (sharedOutput is not null)
        {
            var shared = sharedOutput.Snapshot();
            return snapshot with
            {
                Device = shared.Device, DeviceFormat = shared.DeviceFormat,
                DeviceLatencyMs = shared.DeviceLatencyMs, DeviceAverageLatencyMs = shared.DeviceAverageLatencyMs,
                Render = shared.Render, RequestedOutputMs = shared.RequestedOutputMs,
                LowLatencyRequested = shared.LowLatencyRequested, LowLatencyActive = shared.LowLatencyActive,
                LowLatencyUnavailableReason = shared.LowLatencyUnavailableReason, EnginePeriodMs = shared.EnginePeriodMs,
                DeviceError = mixedSource?.Error ?? shared.DeviceError, DeviceRestarts = shared.DeviceRestarts,
                PlaybackState = closed ? "종료" : mixedSource?.Error is not null ? "상대 재생 오류" : sinkStarted ? shared.PlaybackState : "대기"
            };
        }
        // Core Audio calls can block during device reconfiguration. Never hold
        // the capture/decoder callback lock across these diagnostic-only queries.
        snapshot = snapshot with
        {
            Render = currentOutput is null ? null : new RenderDiagnostics(currentOutput.BoundedActive, currentOutput.TargetPaddingMs, currentOutput.CapacityMs, currentOutput.EmptyEvents, currentOutput.SafetyFallbacks, currentOutput.RawActive, currentOutput.Execution),
            CaptureExecution = input?.Execution,
            RequestedOutputMs = buffer is null ? null : requestedOutputMs,
            LowLatencyRequested = preferLowLatency,
            LowLatencyActive = currentOutput?.LowLatencyActive ?? input?.LowLatencyActive,
            LowLatencyUnavailableReason = currentOutput?.LowLatencyUnavailableReason ?? inputFallbackReason ?? input?.LowLatencyUnavailableReason,
            EnginePeriodMs = (currentOutput?.LowLatencyActive ?? input?.LowLatencyActive) == true ? currentOutput?.EnginePeriodMs ?? input?.EnginePeriodMs : null
        };
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
        try
        {
            lock (audioLock) { if (closed) return; sinkStarted = true; }
            if (sharedOutput is null) output?.Play();
            else { mixedSource!.Activate(); await sharedOutput.StartAudioSink(); }
        }
        finally { playbackGate.Release(); }
    }
    public async Task RestartOutputAsync(bool manual = true, int? outputLatencyMs = null)
    {
        if (outputLatencyMs is not (null or 20 or 40)) throw new ArgumentOutOfRangeException(nameof(outputLatencyMs));
        if (sharedOutput is not null) { await sharedOutput.RestartOutputAsync(manual, outputLatencyMs); return; }
        await playbackGate.WaitAsync();
        try
        {
            SharedRenderPlayer? old;
            lock (audioLock)
            {
                if (closed || buffer is null) return;
                if (outputLatencyMs is { } latency)
                {
                    requestedOutputMs = latency;
                    if (output?.LowLatencyActive == true && !sinkFailed) return; // Only change the fallback request; keep the live low-latency stream.
                }
                if (manual) automaticRecoveryUsed = false;
                old = output; output = null; sinkFailed = true; buffer.Suspend();
            }
            var cleanup = new CallCleanup();
            cleanup.Run("이전 출력 정지", () => old?.Stop());
            cleanup.Run("이전 출력 해제", () => old?.Dispose());
            cleanup.ThrowIfFailed();
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
    public Task Close()
    {
        TaskCompletionSource completion;
        lock (audioLock)
        {
            if (closeCompletion is not null) return closeCompletion.Task;
            closed = true;
            closeCompletion = completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _ = CloseCoreAsync(completion);
        return completion.Task;
    }
    private async Task CloseCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            var cleanup = new CallCleanup();
            // Stop/dispose never holds audioLock: capture may need it before
            // StopRecording can finish. Preserve failures from BOTH halves.
            cleanup.Run("재생 버퍼 중지", () => buffer?.Suspend());
            cleanup.Run("음향 측정 취소", () => Acoustic.Cancel());
            if (input is not null)
            {
                cleanup.Run("마이크 정지", input.StopRecording);
                cleanup.Run("마이크 해제", input.Dispose);
            }
            await playbackGate.WaitAsync();
            try
            {
                if (sharedOutput is not null)
                {
                    sharedOutput.OnAudioSinkError -= SharedSinkError;
                    sharedOutput.OnAudioSinkRecovered -= SharedSinkRecovered;
                    if (mixedSource is not null)
                        await cleanup.RunAsync("공통 출력 소스 분리", () => mixedSource.DisposeAsync().AsTask());
                }
                SharedRenderPlayer? old;
                lock (audioLock) { old = output; output = null; }
                cleanup.Run("출력 정지", () => old?.Stop());
                cleanup.Run("출력 해제", () => old?.Dispose());
                cleanup.Run("재생 버퍼 해제", () => (buffer as IDisposable)?.Dispose());
            }
            finally { playbackGate.Release(); }
            cleanup.ThrowIfFailed(); completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
