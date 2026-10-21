using System.Runtime.InteropServices;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceNative;

// Shared/event render adapter. Retains the established player as the compatibility path.
internal sealed class SharedRenderPlayer : IDisposable
{
    private readonly int requestedMs;
    private readonly bool preferLow, bounded, preferRaw;
    private readonly AudioStreamCategory category;
    public bool RawActive { get; private set; }
    private WasapiPlayer? fallback;
    private MMDevice? device;
    private AudioClient? client;
    private IWaveProvider? provider;
    private readonly AutoResetEvent ready = new(false);
    private readonly ManualResetEvent stop = new(false);
    private Thread? thread;
    private TaskCompletionSource? started;
    private bool disposed;
    private int targetFrames, capacity, padding, desiredFrames;
    private long warmupStarted;
    private bool boundedFailed;
    private double periodMs;
    private long emptyEvents, safetyFallbacks;
    private string? reason, deviceId, deviceName;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public WaveFormat OutputWaveFormat { get; private set; } = new(48000, 16, 1);
    public string? DeviceId => fallback?.DeviceId ?? deviceId;
    public string? DeviceFriendlyName => fallback?.DeviceFriendlyName ?? deviceName;
    public bool LowLatencyActive => fallback?.LowLatencyActive ?? client is not null;
    public string? LowLatencyUnavailableReason => reason ?? fallback?.LowLatencyUnavailableReason;
    public int LatencyMilliseconds => fallback?.LatencyMilliseconds ?? (int)Math.Ceiling(periodMs);
    public double EnginePeriodMs => fallback?.LatencyMilliseconds ?? periodMs;
    public TimeSpan AverageLatency => fallback?.AverageLatency ?? TimeSpan.FromMilliseconds(periodMs);
    public TimeSpan CurrentLatency => fallback?.CurrentLatency ?? TimeSpan.FromSeconds(
        (client?.CurrentPadding ?? Volatile.Read(ref padding)) / (double)OutputWaveFormat.SampleRate);
    public long SafetyFallbacks => Interlocked.Read(ref safetyFallbacks);
    public long EmptyEvents => Interlocked.Read(ref emptyEvents);
    public bool BoundedActive => client is not null && Volatile.Read(ref targetFrames) < capacity;
    public double? TargetPaddingMs => client is null ? null : Volatile.Read(ref targetFrames) * 1000.0 / OutputWaveFormat.SampleRate;
    public double? CapacityMs => client is null ? null : capacity * 1000.0 / OutputWaveFormat.SampleRate;

    private readonly string? endpointId;
    private MMDevice GetEndpoint(MMDeviceEnumerator enumerator) => endpointId is null
        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(endpointId);
    public SharedRenderPlayer(int requestedMs, bool preferLow, bool bounded, bool preferRaw = false, AudioStreamCategory category = AudioStreamCategory.Other, string? endpointId = null)
    { this.endpointId = endpointId; this.category = category; this.requestedMs = requestedMs; this.preferLow = preferLow; this.bounded = bounded; this.preferRaw = preferRaw; }

    internal static int ChooseTarget(int capacity, uint period, int rate) =>
        Math.Min(capacity, checked((int)period + (int)Math.Ceiling(rate * .005)));

    public void Init(IWaveProvider source)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (provider is not null || fallback is not null) throw new InvalidOperationException("Output already initialized.");
        if (bounded && preferLow)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                device = GetEndpoint(enumerator);
                deviceId = device.ID; deviceName = device.FriendlyName;
                client = device.CreateAudioClient();
                if (preferRaw || category != AudioStreamCategory.Other)
                {
                    if (!client.SupportsAudioClient2) throw new NotSupportedException("Shared output stream properties are unavailable.");
                    client.SetClientProperties(category, preferRaw ? AudioClientStreamOptions.Raw : AudioClientStreamOptions.None);
                }
                var mix = client.MixFormat;
                // Limit the custom path to formats we can adapt without opaque conversion.
                if (mix.BitsPerSample != 32 || mix.Channels is not (1 or 2)
                    || !(mix.Encoding == WaveFormatEncoding.IeeeFloat
                    || mix is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
                    throw new NotSupportedException("Shared render mix format requires compatibility output.");
                var period = client.GetSharedModeEnginePeriod(mix).ChooseLowestLatencyPeriod();
                client.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, period, mix, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                capacity = client.BufferSize;
                desiredFrames = ChooseTarget(capacity, period, mix.SampleRate);
                targetFrames = capacity;
                periodMs = period * 1000.0 / mix.SampleRate; OutputWaveFormat = mix;
                ISampleProvider samples = source.ToSampleProvider();
                if (samples.WaveFormat.SampleRate != mix.SampleRate) samples = new WdlResamplingSampleProvider(samples, mix.SampleRate);
                if (samples.WaveFormat.Channels == 1 && mix.Channels == 2) samples = new MonoToStereoSampleProvider(samples);
                else if (samples.WaveFormat.Channels != mix.Channels) throw new NotSupportedException("Unsupported shared render channel mapping.");
                provider = samples.ToWaveProvider(); RawActive = preferRaw; return;
            }
            catch (Exception error) when (error is COMException or NotSupportedException or ArgumentException)
            { reason = "공유 출력 호환 경로: " + error.Message; ReleaseClient(); }
        }
        CreateFallback(source);
    }
    private void CreateFallback(IWaveProvider source)
    {
        var builder = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(requestedMs);
        using var enumerator = new MMDeviceEnumerator();
        device = GetEndpoint(enumerator);
        deviceId = device.ID; deviceName = device.FriendlyName;
        var selected = device;
        builder.WithDevice(selected);
        if (preferLow) builder.WithLowLatency();
        builder.WithCategory(category);
        if (preferRaw) builder.WithRawMode();
        WasapiPlayer? player = null;
        try { player = builder.Build(); player.Init(source); RawActive = preferRaw; }
        catch (Exception error) when (preferRaw && error is COMException or NotSupportedException or InvalidOperationException)
        {
            player?.Dispose(); reason = "RAW 공유 출력 미지원 · 호환 출력: " + error.Message;
            var compatible = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(requestedMs).WithDevice(selected);
            if (preferLow) compatible.WithLowLatency();
            compatible.WithCategory(category);
            player = compatible.Build();
            try { player.Init(source); } catch { player.Dispose(); throw; }
            RawActive = false;
        }
        catch { player?.Dispose(); throw; }
        fallback = player; OutputWaveFormat = player.OutputWaveFormat;
        player.PlaybackStopped += (_, args) => PlaybackStopped?.Invoke(this, args);
    }
    public void Play()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (fallback is not null) { fallback.Play(); return; }
        if (provider is null || client is null) throw new InvalidOperationException("Output not initialized.");
        if (thread is { IsAlive: true }) return;
        if (thread is not null) throw new InvalidOperationException("Stopped output must be recreated.");
        started = new(TaskCreationOptions.RunContinuationsAsynchronously); stop.Reset();
        thread = new Thread(Render) { IsBackground = true, Name = "Voice bounded shared render" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        try { started.Task.GetAwaiter().GetResult(); }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            thread.Join(); thread = null;
            var source = provider; provider = null; ReleaseClient();
            reason = "공유 출력 시작 실패 · 호환 경로 복귀: " + error.Message;
            CreateFallback(source); fallback!.Play();
        }
    }
    private void Render()
    {
        IntPtr priority = IntPtr.Zero; Exception? failure = null; var active = client!;
        try
        {
            uint index = 0; priority = AvSetMmThreadCharacteristics("Audio", ref index);
            var render = active.AudioRenderClient;
            Fill(render, targetFrames); active.Start(); warmupStarted = Stopwatch.GetTimestamp(); started!.SetResult();
            WaitHandle[] signals = [stop, ready];
            while (WaitHandle.WaitAny(signals, Math.Max(30, (int)Math.Ceiling(periodMs * 3))) != 0)
            {
                var queued = active.CurrentPadding;
                if (queued == 0)
                {
                    RestoreFullPadding();
                }
                // Keep established headroom through cold decoder/JIT startup. Reduce only
                // after two seconds without a drain; never retry after a bounded deadline miss.
                if (!boundedFailed && Stopwatch.GetElapsedTime(warmupStarted).TotalSeconds >= 2)
                    Volatile.Write(ref targetFrames, desiredFrames);
                var count = Math.Min(capacity - queued, targetFrames - queued);
                if (count > 0) Fill(render, count, monitorDrain: queued > 0);
                Volatile.Write(ref padding, active.CurrentPadding);
            }
        }
        catch (Exception error) { if (!started!.TrySetException(error)) failure = error; }
        finally
        {
            try { active.Stop(); active.Reset(); } catch (COMException) { }
            Volatile.Write(ref padding, 0);
            if (priority != IntPtr.Zero) AvRevertMmThreadCharacteristics(priority);
        }
        if (failure is not null) PlaybackStopped?.Invoke(this, new StoppedEventArgs(failure));
    }
    private void RestoreFullPadding()
    {
        Interlocked.Increment(ref emptyEvents);
        if (targetFrames < capacity)
        {
            boundedFailed = true;
            Interlocked.Increment(ref safetyFallbacks);
        }
        else warmupStarted = Stopwatch.GetTimestamp();
        // Warm-up drains restart settling; a bounded miss latches full headroom.
        Volatile.Write(ref targetFrames, capacity);
    }
    private void Fill(AudioRenderClient render, int frames, bool monitorDrain = false)
    {
        using var lease = render.GetBufferLease(frames, OutputWaveFormat.BlockAlign);
        var count = provider!.Read(lease.Buffer);
        if (count < 0 || count > lease.Buffer.Length || count % OutputWaveFormat.BlockAlign != 0)
            throw new InvalidOperationException("Invalid shared render source length.");
        lease.Buffer[count..].Clear();
        // A slow provider can drain the already committed audio while its lease is held.
        // Observe before Release, otherwise newly committed frames would hide the miss.
        if (monitorDrain && client!.CurrentPadding == 0) RestoreFullPadding();
        lease.Release(frames);
    }
    public void Stop()
    {
        if (disposed) return;
        fallback?.Stop(); stop.Set();
        if (thread is not null && Thread.CurrentThread != thread) thread.Join();
    }
    private void ReleaseClient() { RawActive = false; client?.Dispose(); client = null; device?.Dispose(); device = null; }
    public void Dispose()
    {
        if (disposed) return;
        if (Thread.CurrentThread == thread) throw new InvalidOperationException("Dispose output outside render callback.");
        Stop(); fallback?.Dispose(); ReleaseClient(); ready.Dispose(); stop.Dispose(); disposed = true;
    }
    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string name, ref uint index);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
