using System.Diagnostics;

namespace BuildTools;

internal sealed class ProcessFailure(string message) : Exception(message);
internal static class ProcessRunner
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        CancellationToken token, int timeoutSeconds = 900, bool echo = true, IReadOnlyDictionary<string, string>? environment = null)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        process.Start();
        var output = PumpAsync(process.StandardOutput, echo ? Console.Out : null);
        var error = PumpAsync(process.StandardError, echo ? Console.Error : null);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10));
            token.ThrowIfCancellationRequested(); throw new TimeoutException(executable + " exceeded its deadline");
        }
        var text = await output; var errors = await error;
        if (process.ExitCode != 0) throw new ProcessFailure($"{Path.GetFileName(executable)} exited {process.ExitCode}: {errors}");
        return text;
    }
    private static async Task<string> PumpAsync(StreamReader source, TextWriter? destination)
    {
        var text = new System.Text.StringBuilder();
        while (await source.ReadLineAsync() is { } line)
        { text.AppendLine(line); if (destination is not null) await destination.WriteLineAsync(line); }
        return text.ToString();
    }
}
