using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceNative;

internal static class CapturePeriodChecks
{
    public static void Periods()
    {
        Check(new(480, 48, 48, 480), 48000, 240);
        Check(new(480, 128, 128, 480), 48000, 256);
        Check(new(448, 4, 48, 448), 44100, 224);
        Check(new(480, 48, 480, 480), 48000, null);
        Check(new(240, 48, 48, 192), 48000, null);
        Check(new(480, 0, 48, 480), 48000, null);
        Check(new(192, 48, 48, 480), 48000, null);
        Console.WriteLine("PASS: shared capture periods honor fundamental/min/max bounds, 5ms floor and 10ms fallback.");
        static void Check(AudioClientPeriodInfo info, int rate, uint? expected)
        {
            if (SharedCaptureRecorder.ChoosePeriod(info, rate) != expected) throw new InvalidOperationException("Invalid shared capture period selection.");
        }
    }

    private sealed class Silence : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(Span<byte> destination) { destination.Clear(); return destination.Length; }
    }

    public static async Task DeviceAsync(string output)
    {
        var results = new List<object>();
        foreach (var trial in new[] { (Name: "standard", Intermediate: false, Concurrent: false),
            (Name: "five", Intermediate: true, Concurrent: false), (Name: "five-shared-output-load", Intermediate: true, Concurrent: true) })
        {
            using var recorder = new SharedCaptureRecorder(true, trial.Intermediate);
            using var other = trial.Concurrent ? new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build() : null;
            other?.Init(new Silence()); other?.Play();
            using var loadCancel = new CancellationTokenSource();
            var load = trial.Concurrent ? Task.Run(() =>
            {
                while (!loadCancel.IsCancellationRequested) { var began = Stopwatch.GetTimestamp(); while (Stopwatch.GetElapsedTime(began).TotalMilliseconds < 5) Thread.SpinWait(100); Thread.Sleep(5); }
            }) : Task.CompletedTask;
            var samples = new List<double>(); var chunks = new List<double>();
            var errors = new ConcurrentQueue<string>(); var sync = new object();
            var clock = Stopwatch.StartNew(); long frames = 0, discontinuities = 0, timestampErrors = 0;
            recorder.RecordingStopped += (_, args) => { if (args.Exception is not null) errors.Enqueue(args.Exception.Message); };
            recorder.DataAvailable += (data, flags, _, qpc) =>
            {
                if (clock.Elapsed.TotalSeconds < 2) return;
                var age = ProbeClock.Now - qpc / 10000000.0;
                lock (sync)
                {
                    frames += data.Length / recorder.WaveFormat.BlockAlign;
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) discontinuities++;
                    if ((flags & AudioClientBufferFlags.TimestampError) != 0) timestampErrors++;
                    if (samples.Count < 6000 && age is >= 0 and < 1 && (flags & AudioClientBufferFlags.TimestampError) == 0)
                    { samples.Add(age * 1000); chunks.Add(data.Length / (double)recorder.WaveFormat.AverageBytesPerSecond * 1000); }
                }
            };
            try { recorder.StartRecording(); await Task.Delay(12000); }
            finally { recorder.StopRecording(); loadCancel.Cancel(); await load; other?.Stop(); }
            double? Quantile(List<double> values, double q) => values.Count == 0 ? null : values.Order().ElementAt((int)Math.Ceiling(values.Count * q) - 1);
            if (!errors.IsEmpty || frames < 48000 * 8 || samples.Count == 0)
                throw new InvalidOperationException("Shared capture made insufficient progress: " + string.Join("; ", errors));
            results.Add(new { trial.Name, trial.Intermediate, trial.Concurrent, frames, discontinuities, timestampErrors,
                recorder.LowLatencyActive, recorder.LowLatencyUnavailableReason, recorder.EnginePeriodMs,
                recorder.DeviceFriendlyName, format = recorder.WaveFormat.ToString(),
                callbackAgeP50Ms = Quantile(samples, .5), callbackAgeP95Ms = Quantile(samples, .95),
                callbackChunkP50Ms = Quantile(chunks, .5), callbackChunkP95Ms = Quantile(chunks, .95) });
            Console.WriteLine($"{trial.Name}: active={recorder.LowLatencyActive} period={recorder.EnginePeriodMs:F3}ms age={Quantile(samples, .5):F2}/{Quantile(samples, .95):F2}ms discontinuities={discontinuities}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { notes = "Metadata only; native timestamp to callback, not mouth-to-ear. Two-second warmup excluded. Concurrent case uses silent shared playback and one partial CPU worker, not a real game.", results }, DiagnosticJson.Readable));
    }
}
