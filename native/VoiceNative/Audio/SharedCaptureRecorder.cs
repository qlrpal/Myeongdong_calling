using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceNative;

// Explicitly requests a supported ~5ms shared period rather than the driver's
// absolute minimum. The established NAudio shared recorder remains the fallback.
internal sealed class SharedCaptureRecorder : IDisposable
{
    private WasapiRecorder? fallback;
    private AudioClient? client;
    private MMDevice? device;
    private readonly AutoResetEvent ready = new(false);
    private readonly ManualResetEvent stop = new(false);
    private Thread? thread;
    private TaskCompletionSource? started;
    private byte[] silence = [];
    private bool disposed;
    private string? deviceId, deviceName;
    private WaveFormat format = new(48000, 16, 1);
    public event CaptureDataAvailableHandler? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public WaveFormat WaveFormat => fallback?.WaveFormat ?? format;
    public bool LowLatencyActive => fallback?.LowLatencyActive ?? client is not null;
    private string? fallbackReason;
    public string? LowLatencyUnavailableReason => fallbackReason ?? fallback?.LowLatencyUnavailableReason;
    public double EnginePeriodMs => fallback?.LatencyMilliseconds ?? periodMs;
    public string? DeviceId => fallback?.DeviceId ?? deviceId;
    public string? DeviceFriendlyName => fallback?.DeviceFriendlyName ?? deviceName;
    public TimeSpan AverageLatency => fallback?.AverageLatency ?? TimeSpan.FromMilliseconds(periodMs);
    public TimeSpan CurrentLatency => fallback?.CurrentLatency ?? (client is { } active
        ? TimeSpan.FromSeconds(active.CurrentPadding / (double)format.SampleRate) : AverageLatency);
    private double periodMs = 5;
    private readonly string? captureEndpointId;

    internal static uint? ChoosePeriod(AudioClientPeriodInfo range, int rate)
    {
        if (rate <= 0 || range.FundamentalPeriodInFrames == 0 || range.MinPeriodInFrames == 0
            || range.MaxPeriodInFrames < range.MinPeriodInFrames) return null;
        var unit = (ulong)range.FundamentalPeriodInFrames;
        var wanted = Math.Max((ulong)Math.Ceiling(rate * .005), range.MinPeriodInFrames);
        var rounded = (wanted + unit - 1) / unit * unit;
        // No benefit over the existing 10ms shared path; never round outside driver bounds.
        return rounded <= range.MaxPeriodInFrames && rounded < range.DefaultPeriodInFrames
            && rounded * 1000.0 / rate < 10 ? (uint)rounded : null;
    }

    public SharedCaptureRecorder(bool preferLowLatency, bool intermediatePeriod = true, string? captureEndpointId = null)
    {
        this.captureEndpointId = captureEndpointId;
        try
        {
            if (preferLowLatency && intermediatePeriod)
            {
                using var enumerator = new MMDeviceEnumerator();
                device = GetEndpoint(enumerator);
                deviceId = device.ID; deviceName = device.FriendlyName;
                client = device.CreateAudioClient(); format = client.MixFormat;
                _ = new NativeCaptureConverter(format); // Keep the existing no-resampling contract.
                var frames = ChoosePeriod(client.GetSharedModeEnginePeriod(format), format.SampleRate)
                    ?? throw new NotSupportedException("입력 장치에 10ms 미만의 안전한 공유 주기가 없습니다.");
                client.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, frames, format, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                periodMs = frames * 1000.0 / format.SampleRate;
                silence = new byte[checked(client.BufferSize * format.BlockAlign)];
                return;
            }
        }
        catch (Exception error) when (error is COMException or NotSupportedException or ArgumentException)
        {
            fallbackReason = "5ms 공유 입력 미사용: " + error.Message;
            ReleaseClient();
        }
        catch { ReleaseClient(); ready.Dispose(); stop.Dispose(); throw; }
        try { CreateFallback(preferLowLatency); }
        catch { ReleaseClient(); ready.Dispose(); stop.Dispose(); throw; }
    }

    private void CreateFallback(bool preferLowLatency, bool preserveFormat = false)
    {
        var low = preferLowLatency;
        if (low)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var endpoint = GetEndpoint(enumerator);
                using var probe = endpoint.CreateAudioClient();
                var mix = probe.MixFormat;
                if (probe.GetSharedModeEnginePeriod(mix).MinPeriodInFrames * 1000.0 / mix.SampleRate < 5) low = false;
            }
            catch (Exception) { low = false; }
        }
        var builder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(10);
        if (captureEndpointId is not null)
        {
            using var enumerator = new MMDeviceEnumerator();
            device = GetEndpoint(enumerator);
            builder.WithDevice(device);
        }
        if (low) builder.WithLowLatency();
        if (preserveFormat) builder.WithFormat(format);
        else if (!preferLowLatency) builder.WithFormat(new WaveFormat(48000, 16, 1));
        var recorder = builder.Build();
        try { _ = new NativeCaptureConverter(recorder.WaveFormat); }
        catch (NotSupportedException)
        {
            recorder.Dispose();
            var compatible = new WasapiRecorderBuilder().WithSharedMode().WithEventSync().WithBufferLength(10)
                .WithFormat(new WaveFormat(48000, 16, 1));
            if (device is not null) compatible.WithDevice(device);
            recorder = compatible.Build();
        }
        fallback = recorder;
        recorder.DataAvailable += (data, flags, position, qpc) => DataAvailable?.Invoke(data, flags, position, qpc);
        recorder.RecordingStopped += (_, args) => RecordingStopped?.Invoke(this, args);
        fallbackReason ??= low ? null : "표준 10ms 공유 입력 사용";
    }

    private MMDevice GetEndpoint(MMDeviceEnumerator enumerator) => captureEndpointId is null
        ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
        : enumerator.GetDevice(captureEndpointId);

    public void StartRecording()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (fallback is not null) { fallback.StartRecording(); return; }
        if (thread is not null) throw new InvalidOperationException("입력이 이미 시작되었습니다.");
        stop.Reset();
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Voice shared capture 5ms" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        try { started.Task.GetAwaiter().GetResult(); }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            thread.Join(); thread = null; ReleaseClient();
            fallbackReason = "5ms 공유 입력 시작 실패 · 표준 공유 입력 복귀: " + error.Message;
            CreateFallback(false, preserveFormat: true); fallback!.StartRecording();
        }
    }

    private void CaptureLoop()
    {
        IntPtr priority = IntPtr.Zero;
        Exception? failure = null;
        var active = client!;
        try
        {
            uint index = 0;
            priority = AvSetMmThreadCharacteristics("Audio", ref index);
            var capture = active.AudioCaptureClient;
            active.Start(); started!.SetResult();
            WaitHandle[] signals = [stop, ready];
            while (WaitHandle.WaitAny(signals, 30) != 0)
            {
                while (!stop.WaitOne(0) && capture.GetNextPacketSize() > 0)
                {
                    using var lease = capture.GetBufferLease(format.BlockAlign);
                    var bytes = checked(lease.FrameCount * format.BlockAlign);
                    if (bytes == 0) continue;
                    ReadOnlySpan<byte> data;
                    if ((lease.Flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        if (bytes > silence.Length) throw new InvalidOperationException("입력 무음 버퍼 범위를 초과했습니다.");
                        data = silence.AsSpan(0, bytes);
                    }
                    else data = lease.Buffer;
                    DataAvailable?.Invoke(data, lease.Flags, lease.DevicePosition, lease.QPCPosition);
                }
            }
        }
        catch (Exception error)
        {
            if (!started!.TrySetException(error)) failure = error;
        }
        finally
        {
            try { active.Stop(); active.Reset(); } catch (COMException) { }
            if (priority != IntPtr.Zero) AvRevertMmThreadCharacteristics(priority);
        }
        if (failure is not null) RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
    }

    public void StopRecording()
    {
        fallback?.StopRecording(); stop.Set();
        if (thread is not null && Thread.CurrentThread != thread) thread.Join();
    }
    private void ReleaseClient() { client?.Dispose(); client = null; device?.Dispose(); device = null; }
    public void Dispose()
    {
        if (disposed) return;
        if (Thread.CurrentThread == thread) throw new InvalidOperationException("입력 콜백 밖에서 녹음기를 해제하세요.");
        StopRecording(); fallback?.Dispose(); ReleaseClient();
        ready.Dispose(); stop.Dispose(); disposed = true;
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "AvSetMmThreadCharacteristicsW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);
    [DllImport("avrt.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr handle);
}
