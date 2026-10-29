using System.IO.Compression;
using System.Text.Json;

namespace BuildTools;

internal static class SelfChecks
{
    internal static async Task RunAsync(CancellationToken token)
    {
        var checks = 0;
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
        { try { await action(); } catch (T) { checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
        var root = Directory.CreateTempSubdirectory("calling-buildtools-").FullName;
        if (!Path.GetFileName(root).StartsWith("calling-buildtools-", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected fixture path");
        try
        {
            foreach (var path in new[] { "TransportProbe.exe", "BuildTools.dll", "settings.json", "voice-diagnostics-123.json", "app.log", ".env", "x.key", "VoiceNative.pdb",
                "docs/reports/R.md", "server/install.sh", "../escape", "C:\\private.txt", "/root/file", "a/../b", "./x", "a//b", "build.mjs", "VoiceNative.csproj" })
                Throws<InvalidDataException>(() => PackageVerifier.CheckPaths([path]));
            PackageVerifier.CheckPaths(["VoiceNative.dll", "licenses/NAudio.txt", "ko/System.Windows.Forms.resources.dll"]);
            var directory = Path.Combine(root, "package"); Directory.CreateDirectory(directory);
            foreach (var name in PackageVerifier.Required) File.WriteAllText(Path.Combine(directory, name), "fixture: " + name);
            using var empty = JsonDocument.Parse("{}");
            var manifest = new PackageManifest { SchemaVersion = 1, Channel = "beta", Version = "0.6.1-beta.1", Rid = "win-x64", SelfContained = false,
                CreatedUtc = "2026-01-01T00:00:00.000Z", Sdk = empty.RootElement.Clone(), Codec = empty.RootElement.Clone(),
                Files = WorkspaceFiles.List(directory).Select(n => PackageVerifier.Describe(directory, n)).ToArray() };
            var manifestPath = Path.Combine(directory, PackageVerifier.ManifestName); PackageJson.Write(manifestPath, manifest);
            Require(PackageVerifier.Verify(directory).Files.Length == manifest.Files.Length, "Valid inventory rejected");
            var asset = Path.Combine(directory, "VoiceNative.dll"); var saved = File.ReadAllBytes(asset);
            File.WriteAllText(asset, "changed"); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory)); File.WriteAllBytes(asset, saved);
            var extra = Path.Combine(directory, "extra.txt"); File.WriteAllText(extra, "extra");
            Throws<InvalidDataException>(() => PackageVerifier.Verify(directory)); File.Delete(extra);
            PackageJson.Write(manifestPath, manifest with { SelfContained = true }); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest with { Channel = "stable" }); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest with { Files = [.. manifest.Files, manifest.Files[0]] }); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest with { Files = [manifest.Files[0] with { Path = "../escape" }, .. manifest.Files.Skip(1)] });
            Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            File.WriteAllText(manifestPath, "{\"schemaVersion\":1}"); Throws<JsonException>(() => PackageVerifier.Verify(directory));
            var json = JsonSerializer.Serialize(manifest, PackageJson.Options);
            File.WriteAllText(manifestPath, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": \"1\"", StringComparison.Ordinal));
            Throws<JsonException>(() => PackageVerifier.Verify(directory));
            File.WriteAllText(manifestPath, json.Replace("\"selfContained\": false", "\"selfContained\": \"false\"", StringComparison.Ordinal));
            Throws<JsonException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest with { Files = [null!] }); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest with { Files = [manifest.Files[0] with { Sha256 = null! }] }); Throws<InvalidDataException>(() => PackageVerifier.Verify(directory));
            PackageJson.Write(manifestPath, manifest);
            var zip = Path.Combine(root, "fixture.zip"); var extracted = Path.Combine(root, "extracted");
            ZipFile.CreateFromDirectory(directory, zip, CompressionLevel.Optimal, false, System.Text.Encoding.UTF8);
            ZipFile.ExtractToDirectory(zip, extracted, System.Text.Encoding.UTF8);
            Require(PackageVerifier.Verify(extracted).Files.SequenceEqual(manifest.Files), "ZIP/Unicode roundtrip changed files");
            var badZip = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../zip-escape").Open()); writer.Write("bad"); }
            Throws<IOException>(() => ZipFile.ExtractToDirectory(badZip, Path.Combine(root, "bad-extraction")));
            Require(!File.Exists(Path.Combine(root, "zip-escape")), "ZIP path escaped extraction");
            Throws<InvalidDataException>(() => WorkspaceFiles.Inside(root, root + "-escape/file"));
            Throws<InvalidDataException>(() => WorkspaceFiles.Inside(root, Path.Combine(root, "..", "outside")));
            Require(WorkspaceFiles.Inside(root, Path.Combine(root, "nested", "file")) == Path.Combine(root, "nested", "file"), "Valid workspace output rejected");
            var native = Path.Combine(root, "native"); Directory.CreateDirectory(Path.Combine(native, "VoiceNative"));
            File.WriteAllText(Path.Combine(native, "VoiceNative", "VoiceNative.csproj"), "fixture");
            using (var lease = BuildLease.Acquire(native)) Throws<IOException>(() => { using var duplicate = BuildLease.Acquire(native); });
            using (var released = BuildLease.Acquire(native)) Require(released.CanWrite, "Completed build retained its lock");
            Require(BuildOptions.Parse(["--native-root", native, "--beta", "--package"]) is { Beta: true, Package: true, FrameworkDependent: false }, "Beta option semantics changed");
            Require(BuildOptions.Parse(["--native-root", native, "--stage-only", "--no-restore"]) is { StageOnly: true, NoRestore: true }, "Developer options changed");
            foreach (var flags in new[] { new[] { "--beta", "--no-restore" }, ["--stage-only", "--package"], ["--framework-dependent"], ["--ready-to-run", "--no-restore"], ["--unknown"], ["--beta", "--beta"] })
                Throws<ArgumentException>(() => BuildOptions.Parse(["--native-root", native, .. flags]));
            foreach (var phase in new[] { "archive-directory", "archive-zip", "install-directory", "install-zip", "success" })
            {
                var workspace = Path.Combine(root, phase); Directory.CreateDirectory(workspace);
                var staging = Path.Combine(workspace, "staging"); var destination = Path.Combine(workspace, "dist-beta");
                var pending = Path.Combine(workspace, "pending.zip"); var published = Path.Combine(workspace, "dist-beta.zip"); var backup = Path.Combine(workspace, "archive");
                Directory.CreateDirectory(staging); Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(staging, "marker"), "new"); File.WriteAllText(Path.Combine(destination, "marker"), "old");
                File.WriteAllText(pending, "new-zip"); File.WriteAllText(published, "old-zip");
                void Commit() => OutputTransaction.Commit(workspace, staging, destination, pending, published, backup, true,
                    at => { if (at == phase) throw new IOException("Injected replacement failure"); });
                if (phase == "success")
                {
                    Commit(); Require(File.ReadAllText(Path.Combine(destination, "marker")) == "new" && File.ReadAllText(published) == "new-zip"
                        && File.ReadAllText(Path.Combine(backup, "dist-beta", "marker")) == "old", "Release transaction lost old/new assets");
                }
                else
                {
                    Throws<IOException>(Commit);
                    Require(File.ReadAllText(Path.Combine(destination, "marker")) == "old" && File.ReadAllText(published) == "old-zip"
                        && File.ReadAllText(Path.Combine(staging, "marker")) == "new" && File.ReadAllText(pending) == "new-zip", "Rollback lost prior release/staging: " + phase);
                }
            }
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable");
            var prefix = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(SelfChecks).Assembly.Location } : Array.Empty<string>();
            await ThrowsAsync<ProcessFailure>(() => ProcessRunner.RunAsync(executable, [.. prefix, "test-child-fail"], root, token, 10, echo: false));
            await ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(executable, [.. prefix, "test-child-wait"], root, token, 1, echo: false));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); cancellation.CancelAfter(150);
            await ThrowsAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(executable, [.. prefix, "test-child-wait"], root, cancellation.Token, 10, echo: false));
            Console.WriteLine($"PASS BuildTools: {checks} assertions; policy/manifest/Unicode ZIP/options/rollback/process failure/timeout/cancellation");
        }
        finally
        {
            // Only this exact, absolute CreateTempSubdirectory result is recursively deleted.
            Directory.Delete(WorkspaceFiles.Inside(Path.GetTempPath(), root), recursive: true);
        }
    }
}
