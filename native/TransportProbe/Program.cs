using VoiceNative;

try
{
    if (args is ["--opus-ab-test", var server, var prefix, var report, var role])
        Environment.ExitCode = await OpusAbProbe.RunAsync(server, prefix, report, role);
    else if (args is ["--automated-call", var address, var room, var name, var path, var side])
        Environment.ExitCode = await AutomatedCallProbe.RunAsync(address, room, name, path, side == "receiver");
    else if (!await SelfChecks.TryRunAsync(args))
        await WebRtcProbe.RunAsync(args);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    Environment.ExitCode = 1;
}
