using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;

namespace VoiceNative;
internal static class RenderDeviceChecks
{
    private sealed class Silence(bool stall) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool stalled;
        public int Reads { get; private set; }
        public int Read(Span<byte> data)
        {
            Reads++;
            if (stall && !stalled && clock.Elapsed.TotalSeconds > 5) { stalled = true; Thread.Sleep(50); }
            data.Clear(); return data.Length;
        }
    }
    public static async Task<int> LoadSoakAsync(string server, string room, string output, string role)
    {
        // Independent silent shared stream plus one partially busy CPU worker.
        // This exercises coexistence without changing any game/device settings.
        using var other = new SharedRenderPlayer(40, true, false);
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var probe = endpoint.CreateAudioClient();
        IWaveProvider feed = new Silence(false);
        if (probe.MixFormat.SampleRate != feed.WaveFormat.SampleRate)
            feed = new WdlResamplingSampleProvider(feed.ToSampleProvider(), probe.MixFormat.SampleRate).ToWaveProvider();
        other.Init(feed); other.Play();
        using var cancel = new CancellationTokenSource();
        var load = Task.Run(() =>
        {
            while (!cancel.IsCancellationRequested)
            {
                var began = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 4) Thread.SpinWait(100);
                Thread.Sleep(6);
            }
        });
        try { return await RtpSoakProbe.RunAsync(server, room, output, role, 300, PlayoutPolicy.LowLatency, measure: false); }
        finally { cancel.Cancel(); await load; other.Stop(); }
    }
    public static async Task RestartAsync(string output)
    {
        using var encoder = new OpusAudioEncoder(OpusProfile.Baseline);
        var sink = new WindowsAudioDevice(encoder, disableSource: true, frameMs: 10,
            useRtpPlayout: true, adaptiveReserve: false, boundedRender: true, rawRender: true, renderCategory: AudioStreamCategory.GameChat);
        try
        {
            await sink.StartAudioSink(); await Task.Delay(2500);
            await sink.RestartOutputAsync(manual: true);
            await sink.StartAudioSink(); await Task.Delay(2500);
            var result = sink.Snapshot();
            if (result.DeviceRestarts != 1 || result.DeviceError is not null || result.PlaybackState != "재생")
                throw new InvalidOperationException("Shared output restart failed.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(result, DiagnosticJson.Readable));
        }
        finally { await sink.Close(); await sink.Close(); }
        Console.WriteLine("PASS: shared sink start, manual restart, repeated start and repeated close.");
    }
    public static void Targets()
    {
        if (SharedRenderPlayer.ChooseTarget(984,448,44100) != 669
            || SharedRenderPlayer.ChooseTarget(240,240,48000) != 240)
            throw new InvalidOperationException("Render target bounds failed.");
        using var player = new SharedRenderPlayer(40, true, true);
        Expect<InvalidOperationException>(player.Play);
        player.Stop(); player.Dispose(); player.Dispose();
        Expect<ObjectDisposedException>(player.Play);
        Expect<ObjectDisposedException>(() => player.Init(new Silence(false)));
        Console.WriteLine("PASS: render target capacity bounds and uninitialized/disposed lifecycle guards.");
        static void Expect<T>(Action action) where T : Exception
        {
            try { action(); } catch (T) { return; }
            throw new InvalidOperationException($"Expected {typeof(T).Name}.");
        }
    }
    public static async Task RunAsync(string output)
    {
        Targets();
        var results = new List<object>();
        foreach (var mode in new[] { "standard", "bounded", "bounded-stall" })
        {
            using var player = new SharedRenderPlayer(40, true, mode != "standard");
            var source = new Silence(mode == "bounded-stall"); string? error = null;
            player.PlaybackStopped += (_, args) => { if (args.Exception is not null) error = args.Exception.ToString(); };
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var probe = endpoint.CreateAudioClient();
            var rate = probe.MixFormat.SampleRate;
            IWaveProvider feed = source;
            if (rate != source.WaveFormat.SampleRate)
                feed = new WdlResamplingSampleProvider(source.ToSampleProvider(), rate).ToWaveProvider();
            player.Init(feed); player.Play(); player.Play(); // Repeated connection notification is harmless.
            var samples = new List<object>();
            for (var i=0;i<150;i++)
            {
                await Task.Delay(100);
                samples.Add(new { at = i * .1, paddingMs = player.CurrentLatency.TotalMilliseconds,
                    targetMs = player.TargetPaddingMs, player.BoundedActive, player.EmptyEvents, player.SafetyFallbacks });
            }
            player.Stop();
            if (error is not null || source.Reads < 100) throw new InvalidOperationException(error ?? "Output made no progress.");
            if (mode == "bounded-stall" && player.CapacityMs is not null && (player.SafetyFallbacks == 0 || player.BoundedActive))
                throw new InvalidOperationException("A missed deadline did not restore compatibility padding.");
            results.Add(new { mode, device = player.DeviceFriendlyName, format = player.OutputWaveFormat.ToString(),
                periodMs = player.EnginePeriodMs, player.CapacityMs, player.TargetPaddingMs,
                player.BoundedActive, player.EmptyEvents, player.SafetyFallbacks, source.Reads, error, samples });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new { results, notes = "Silent shared output only; artificial50ms source stall verifies full-padding safety recovery. No microphone/audio recording." },DiagnosticJson.Readable));
        Console.WriteLine("PASS: shared output progress, target bounds and forced-deadline fallback.");
    }
}
