using System.IO.Compression;

namespace BuildTools;

internal static class BuildRunner
{
    internal static async Task RunAsync(BuildOptions options, CancellationToken token)
    {
        var root = options.NativeRoot;
        using var lease = BuildLease.Acquire(root);
        string Output(string name) => WorkspaceFiles.Inside(root, Path.Combine(root, name));
        var staging = Output(".local/publish-" + Guid.NewGuid().ToString("N"));
        var outputName = options.Beta ? "dist-beta" : "dist";
        var destination = Output(outputName); var zip = Output(outputName + ".zip");
        var pending = Output(".local/package-" + Guid.NewGuid().ToString("N") + ".zip");
        var extracted = Output(".local/package-check-" + Guid.NewGuid().ToString("N"));
        var archive = Output(".local/archive/build-" + outputName + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var publish = new List<string> { "publish", Path.Combine(root, "VoiceNative"), "-c", "Release", "-o", staging };
        if (options.NoRestore) publish.Add("--no-restore");
        if (options.Beta || options.ReadyToRun)
        {
            var privateLock = Output(".local/publish-lock-" + Guid.NewGuid().ToString("N") + ".json");
            File.Copy(Path.Combine(root, "VoiceNative", "packages.lock.json"), privateLock);
            publish.AddRange(["-r", "win-x64", "--self-contained", options.Beta && !options.FrameworkDependent ? "true" : "false", "-p:NuGetLockFilePath=" + privateLock]);
        }
        if (options.Beta) publish.AddRange(["-p:BetaRelease=true", "-p:DebugType=none", "-p:DebugSymbols=false"]);
        if (options.ReadyToRun) publish.Add("-p:PublishReadyToRun=true");
        var environment = new Dictionary<string, string> { ["DOTNET_CLI_HOME"] = Path.Combine(root, ".dotnet"),
            ["NUGET_PACKAGES"] = Path.Combine(root, ".nuget"), ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        await ProcessRunner.RunAsync("dotnet", publish, root, token, environment: environment);
        if (options.Beta)
        {
            var manifest = await PackagePreparer.PrepareAsync(staging, root, !options.FrameworkDependent, token);
            Console.WriteLine($"PASS beta package: {manifest.Files.Length} files; patched SDK/native Opus loaded");
        }
        else PrepareDeveloperDocuments(staging, root);
        if (options.StageOnly) { Console.WriteLine("Prepared isolated output: " + staging); return; }
        token.ThrowIfCancellationRequested();
        if (options.Package)
        {
            ZipFile.CreateFromDirectory(staging, pending, CompressionLevel.Optimal, includeBaseDirectory: false, entryNameEncoding: System.Text.Encoding.UTF8);
            if (options.Beta)
            {
                ZipFile.ExtractToDirectory(pending, extracted, System.Text.Encoding.UTF8);
                PackageVerifier.Verify(extracted); Console.WriteLine("PASS beta ZIP inventory/hashes");
            }
        }
        token.ThrowIfCancellationRequested();
        OutputTransaction.Commit(root, staging, destination, pending, zip, archive, options.Package);
        Console.WriteLine("Previous output retained in " + archive); Console.WriteLine("Prepared package: " + destination);
        if (options.Package) Console.WriteLine("ZIP: " + zip);
    }
    private static void PrepareDeveloperDocuments(string staging, string root)
    {
        var readme = File.ReadAllText(Path.Combine(root, "README.md")).Replace("server/README.md", "docs/SERVER.md", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(staging, "README.md"), readme);
        File.Copy(Path.Combine(root, "docs", "USER-GUIDE.md"), Path.Combine(staging, "시작하기.md"), overwrite: true);
        WorkspaceFiles.CopyTree(Path.Combine(root, "docs"), Path.Combine(staging, "docs"));
        File.Copy(Path.Combine(root, "server", "README.md"), Path.Combine(staging, "docs", "SERVER.md"), overwrite: true);
        WorkspaceFiles.CopyTree(Path.Combine(root, "server", "deploy"), Path.Combine(staging, "docs", "deploy"));
    }
}
