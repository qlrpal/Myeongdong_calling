using BuildTools;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    switch (args)
    {
        case ["build", .. var options]: await BuildRunner.RunAsync(BuildOptions.Parse(options), cancellation.Token); break;
        case ["prepare", var directory, var nativeRoot, var flag] when bool.TryParse(flag, out var contained):
            var manifest = await PackagePreparer.PrepareAsync(Path.GetFullPath(directory), Path.GetFullPath(nativeRoot), contained, cancellation.Token);
            Console.WriteLine($"PASS beta package: {manifest.Files.Length} files; patched SDK/native Opus loaded"); break;
        case ["verify", var directory]:
            Console.WriteLine($"PASS beta inventory/hashes: {PackageVerifier.Verify(Path.GetFullPath(directory)).Files.Length}"); break;
        case ["self-test"]: await SelfChecks.RunAsync(cancellation.Token); break;
        case ["test-child-fail"]: Console.Error.WriteLine("fixture failure"); return 7;
        case ["test-child-wait"]: await Task.Delay(Timeout.Infinite, cancellation.Token); break;
        default: throw new ArgumentException("Use build --native-root PATH [flags], prepare DIRECTORY NATIVE_ROOT true|false, verify DIRECTORY, or self-test");
    }
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Build cancelled; unpublished staging and prior output retained."); return 130; }
catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
