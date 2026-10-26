using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceNative;

internal static class WasapiLowLatencyCheck
{
    private sealed class Silence : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public int Read(Span<byte> buffer) { buffer.Clear(); return buffer.Length; }
    }

    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("WASAPI requires Windows.");
            return 2;
        }

        var passed = true;
        passed &= await CheckCaptureAsync();
        passed &= await CheckRenderAsync();
        Console.WriteLine(passed ? "PASS: WASAPI shared low latency works on both default devices." :
            "FAIL: At least one default device did not run in WASAPI shared low latency mode.");
        return passed ? 0 : 1;
    }

    private static async Task<bool> CheckCaptureAsync()
    {
        PrintPeriods(DataFlow.Capture);
        try
        {
            using var recorder = new SharedCaptureRecorder(preferLowLatency: true);
            long callbacks = 0, frames = 0, discontinuities = 0;
            string? error = null;
            recorder.DataAvailable += (data, flags, _, _) =>
            {
                Interlocked.Increment(ref callbacks);
                Interlocked.Add(ref frames, data.Length / recorder.WaveFormat.BlockAlign);
                if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) Interlocked.Increment(ref discontinuities);
            };
            recorder.RecordingStopped += (_, args) => { if (args.Exception is not null) error = args.Exception.Message; };
            recorder.StartRecording();
            try { await Task.Delay(3000); }
            finally { recorder.StopRecording(); }
            var ok = recorder.LowLatencyActive && recorder.EnginePeriodMs < 10 &&
                Interlocked.Read(ref callbacks) > 0 && Interlocked.Read(ref frames) >= recorder.WaveFormat.SampleRate && error is null;
            Console.WriteLine($"Input: {(ok ? "PASS" : "FAIL")} | {recorder.DeviceFriendlyName} | lowLatency={recorder.LowLatencyActive} | period={recorder.EnginePeriodMs:F2} ms | callbacks={callbacks} | frames={frames} | discontinuities={discontinuities}");
            if (recorder.LowLatencyUnavailableReason is { } reason) Console.WriteLine("  fallback: " + reason);
            if (error is not null) Console.WriteLine("  error: " + error);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Input: FAIL | " + ex.Message);
            return false;
        }
    }

    private static async Task<bool> CheckRenderAsync()
    {
        PrintPeriods(DataFlow.Render);
        try
        {
            using var player = new SharedRenderPlayer(requestedMs: 20, preferLow: true, bounded: true);
            string? error = null;
            player.PlaybackStopped += (_, args) => { if (args.Exception is not null) error = args.Exception.Message; };
            player.Init(new Silence());
            player.Play();
            try { await Task.Delay(3000); }
            finally { player.Stop(); }
            var ok = player.LowLatencyActive && player.EnginePeriodMs < 10 && error is null;
            Console.WriteLine($"Output: {(ok ? "PASS" : "FAIL")} | {player.DeviceFriendlyName} | lowLatency={player.LowLatencyActive} | period={player.EnginePeriodMs:F2} ms | bounded={player.BoundedActive} | emptyEvents={player.EmptyEvents}");
            if (player.LowLatencyUnavailableReason is { } reason) Console.WriteLine("  fallback: " + reason);
            if (error is not null) Console.WriteLine("  error: " + error);
            return ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Output: FAIL | " + ex.Message);
            return false;
        }
    }

    private static void PrintPeriods(DataFlow flow)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            using var client = endpoint.CreateAudioClient();
            var format = client.MixFormat;
            var periods = client.GetSharedModeEnginePeriod(format);
            double Ms(uint frames) => frames * 1000.0 / format.SampleRate;
            Console.WriteLine($"{flow} capabilities: {endpoint.FriendlyName} | mix={format.SampleRate} Hz, {format.Channels} ch | min={Ms(periods.MinPeriodInFrames):F3} ms | default={Ms(periods.DefaultPeriodInFrames):F3} ms | fundamental={Ms(periods.FundamentalPeriodInFrames):F3} ms | max={Ms(periods.MaxPeriodInFrames):F3} ms");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{flow} capabilities unavailable: {ex.Message}");
        }
    }
}
