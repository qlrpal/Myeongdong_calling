using System.Text.Json;
using NAudio.CoreAudioApi;

namespace VoiceNative;

// Negotiates shared streams only; never starts playback or changes endpoint settings.
internal static class RenderBudgetChecks
{
    public static void Run(string output)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var trials = new List<object>();
        foreach (var lowLatency in new[] { false, true })
        {
            using var ready = new AutoResetEvent(false);
            using var client = device.CreateAudioClient();
            var format = client.MixFormat;
            var range = client.GetSharedModeEnginePeriod(format);
            uint? chosen = null;
            if (lowLatency)
            {
                chosen = range.ChooseLowestLatencyPeriod();
                client.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, chosen.Value, format, Guid.Empty);
            }
            else client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback,
                400000, 0, format, Guid.Empty);
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            trials.Add(new
            {
                mode = lowLatency ? "IAudioClient3-minimum-shared" : "standard-shared-request40ms",
                format = format.ToString(), sampleRate = format.SampleRate,
                minimumFrames = range.MinPeriodInFrames, defaultFrames = range.DefaultPeriodInFrames,
                fundamentalFrames = range.FundamentalPeriodInFrames, maximumFrames = range.MaxPeriodInFrames,
                chosenFrames = chosen, chosenMs = chosen * 1000.0 / format.SampleRate,
                bufferFrames = client.BufferSize, bufferMs = client.BufferSize * 1000.0 / format.SampleRate,
                streamLatencyMs = client.StreamLatency / 10000.0,
                paddingBeforeStartFrames = client.CurrentPadding
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now, device = device.FriendlyName, trials,
            notes = "Shared stream initialization only. No Start, no audio samples, no endpoint changes. Buffer capacity and stream latency are not physical mouth-to-ear latency."
        }, DiagnosticJson.Readable));
        Console.WriteLine("PASS: shared render period/buffer metadata saved; playback was not started.");
    }
}
