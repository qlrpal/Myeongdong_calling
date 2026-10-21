using System.Diagnostics;
using System.Text.Json;

namespace VoiceNative;

internal static class MeasurementClockProbe
{
    public static async Task Respond()
    {
        while (await Console.In.ReadLineAsync() is { } line && line != "stop")
        {
            var received = ProbeClock.Now;
            Console.WriteLine(JsonSerializer.Serialize(new { received, sent = ProbeClock.Now }));
            Console.Out.Flush();
        }
    }
    public static async Task Sync(string output)
    {
        var start = new ProcessStartInfo("C:/Program Files/PuTTY/plink.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException("Missing " + name);
        foreach (var argument in new[] { "-batch", "-hostkey", Required("VOICE_COMPARE_SSH_HOSTKEY"), "-pw",
            Required("VOICE_COMPARE_SSH_PASSWORD"), Required("VOICE_COMPARE_SSH_TARGET"),
            "powershell.exe -NoProfile -Command \"& ./voice-pipeline-passive-054/TransportProbe.exe --measurement-clock-respond\"" })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Clock control did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var points = new List<object>();
        (double Low, double High)? best = null;
        try
        {
            for (var i = 0; i < 40; i++)
            {
                var sent = ProbeClock.Now;
                await child.StandardInput.WriteLineAsync("ping"); await child.StandardInput.FlushAsync();
                var line = await child.StandardOutput.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException();
                var received = ProbeClock.Now;
                using var reply = JsonDocument.Parse(line);
                var bounds = ProbeClock.Bounds(sent, reply.RootElement.GetProperty("received").GetDouble(),
                    reply.RootElement.GetProperty("sent").GetDouble(), received);
                if (bounds is { } b)
                {
                    points.Add(new { at = sent, low = b.Low, high = b.High });
                    if (best is null || b.High - b.Low < best.Value.High - best.Value.Low) best = b;
                }
                await Task.Delay(5, timeout.Token);
            }
            if (best is null) throw new InvalidOperationException("No bounded clock exchanges.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            { low = best.Value.Low, high = best.Value.High, widthMs = (best.Value.High-best.Value.Low)*1000, points,
                notes = "Remote QPC minus local QPC bounds at exchange time. No symmetric-network assumption. Do not extrapolate across host restarts or ignore clock drift." }, DiagnosticJson.Readable));
            Console.WriteLine($"clock width={(best.Value.High-best.Value.Low)*1000:F3}ms");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(3000)) child.Kill();
        }
    }
}
