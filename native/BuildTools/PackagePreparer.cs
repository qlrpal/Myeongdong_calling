using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BuildTools;

internal static class PackagePreparer
{
    internal static async Task<PackageManifest> PrepareAsync(string directory, string nativeRoot, bool contained, CancellationToken token)
    {
        directory = WorkspaceFiles.Inside(Path.Combine(nativeRoot, ".local"), directory);
        if (!string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(Path.Combine(nativeRoot, ".local")), WorkspaceFiles.PathComparison)
            || !Path.GetFileName(directory).StartsWith("publish-", StringComparison.Ordinal)
            || File.Exists(Path.Combine(directory, PackageVerifier.ManifestName)) || Directory.Exists(Path.Combine(directory, "licenses")))
            throw new InvalidDataException("Only fresh workspace publish staging may be prepared");
        using var deps = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "VoiceNative.deps.json")));
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "VoiceNative.runtimeconfig.json")));
        var includesRuntime = config.RootElement.GetProperty("runtimeOptions").TryGetProperty("includedFrameworks", out var frameworks)
            && frameworks.ValueKind == JsonValueKind.Array;
        if (includesRuntime != contained) throw new InvalidDataException("Runtime inclusion does not match package kind");
        var libraries = deps.RootElement.GetProperty("libraries");
        var version = libraries.EnumerateObject().First(p => p.Name.StartsWith("VoiceNative/", StringComparison.Ordinal)).Name.Split('/')[1];
        if (!version.Contains("-beta.", StringComparison.Ordinal)) throw new InvalidDataException("Not a versioned beta assembly");
        WriteNotices(directory, nativeRoot, libraries);
        foreach (var name in new[] { "README.md", "시작하기.md", "베타안내.md" })
            File.Copy(Path.Combine(nativeRoot, "release", name), Path.Combine(directory, name), overwrite: true);
        var probe = WorkspaceFiles.Inside(nativeRoot, Path.Combine(nativeRoot, ".local", "beta-identity-" + Guid.NewGuid().ToString("N") + ".json"));
        var exe = Path.Combine(directory, "VoiceNative.exe");
        await ProcessRunner.RunAsync(exe, ["--sdk-build-info", probe], nativeRoot, token, 15);
        var sdk = ReadIdentity(probe, "ProductionPatched", "Published package did not load the patched SDK");
        await ProcessRunner.RunAsync(exe, ["--codec-build-info", probe], nativeRoot, token, 15,
            environment: new Dictionary<string, string> { ["VOICE_OPUS_BACKEND"] = "native" });
        var codec = ReadIdentity(probe, "NativeActive", "Published native Opus unavailable");
        var (head, dirty) = await GitIdentityAsync(nativeRoot, token);
        var names = WorkspaceFiles.List(directory); PackageVerifier.CheckPaths(names); PackageVerifier.CheckAssets(names, contained);
        token.ThrowIfCancellationRequested();
        var manifest = new PackageManifest { SchemaVersion = 1, Channel = "beta", Version = version, Rid = "win-x64", SelfContained = contained,
            CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), SourceHead = head, SourceDirty = dirty,
            Sdk = sdk, Codec = codec, Files = names.Select(n => PackageVerifier.Describe(directory, n)).ToArray() };
        PackageJson.Write(Path.Combine(directory, PackageVerifier.ManifestName), manifest);
        PackageVerifier.Verify(directory); return manifest;
    }
    private static JsonElement ReadIdentity(string path, string flag, string failure)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.GetProperty(flag).GetBoolean()) throw new InvalidDataException(failure);
        return document.RootElement.Clone();
    }
    private static async Task<(string? Head, bool? Dirty)> GitIdentityAsync(string root, CancellationToken token)
    {
        try
        {
            var head = await ProcessRunner.RunAsync("git", ["rev-parse", "HEAD"], root, token, 10, echo: false);
            var status = await ProcessRunner.RunAsync("git", ["status", "--porcelain"], root, token, 10, echo: false);
            return (head.Trim(), !string.IsNullOrWhiteSpace(status));
        }
        catch (Exception error) when (error is ProcessFailure or Win32Exception) { return (null, null); } // ZIP checkout or no Git.
    }
    private static string CatalogName(string name) => name.StartsWith("NAudio.", StringComparison.Ordinal) ? "NAudio"
        : name.StartsWith("Common.Logging", StringComparison.Ordinal) ? "Common.Logging"
        : name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal) ? "Microsoft" : name;
    private static void WriteNotices(string directory, string nativeRoot, JsonElement libraries)
    {
        using var sourceLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(nativeRoot, "VoiceNative", "packages.lock.json")));
        var pinned = sourceLock.RootElement.GetProperty("dependencies").EnumerateObject().First().Value;
        var licenseRoot = Path.Combine(directory, "licenses"); Directory.CreateDirectory(licenseRoot);
        var rows = new List<string> { "# Third-party notices", "", "Package versions are from the published dependency graph. Original license/notice texts are in `licenses/`.", "",
            "| Package | Version | Copyright from package metadata | Texts |", "|---|---|---|---|" };
        foreach (var entry in libraries.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (entry.Value.GetProperty("type").GetString() != "package") continue;
            var parts = entry.Name.Split('/');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+' or '_'))))
                throw new InvalidDataException("Invalid package identity: " + entry.Name);
            var name = parts[0]; var version = parts[1];
            if (pinned.TryGetProperty(name, out var pin) && pin.GetProperty("resolved").GetString() != version)
                throw new InvalidDataException("Publish changed pinned dependency: " + entry.Name);
            var source = WorkspaceFiles.Inside(nativeRoot, Path.Combine(nativeRoot, ".nuget", name.ToLowerInvariant(), version));
            var entries = new DirectoryInfo(source).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
            var nuspec = entries.FirstOrDefault(f => f.Extension == ".nuspec");
            var copyright = "See license text";
            if (nuspec is not null)
            {
                using var reader = XmlReader.Create(nuspec.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var metadata = XDocument.Load(reader);
                var text = metadata.Descendants().FirstOrDefault(e => e.Name.LocalName == "copyright")?.Value;
                if (text is not null) copyright = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("|", "\\|", StringComparison.Ordinal);
            }
            var documents = entries.Where(f => IsNotice(f.Name)).ToArray(); var copied = new List<string>();
            foreach (var document in documents)
            {
                var target = name + "-" + document.Name;
                File.Copy(document.FullName, Path.Combine(licenseRoot, target)); copied.Add(target);
            }
            if (!documents.Any(f => IsLicense(f.Name)))
            {
                var catalog = CatalogName(name) + ".txt";
                File.Copy(Path.Combine(nativeRoot, "release", "licenses", catalog), Path.Combine(licenseRoot, catalog), overwrite: true); copied.Add(catalog);
            }
            rows.Add($"| {name} | {version} | {copyright} | {string.Join(", ", copied.Select(f => $"[{f}](licenses/{f})"))} |");
        }
        File.Copy(Path.Combine(nativeRoot, "release", "licenses", "sources.json"), Path.Combine(licenseRoot, "sources.json"));
        rows.AddRange(["", "The patched SIPSorcery source license is also provided as `SIPSorcery.LICENSE.md`; native Opus terms are in `Opus.LICENSE.txt`.", "Microsoft runtime license/third-party notices, when bundled, are included with the runtime."]);
        File.WriteAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.md"), string.Join('\n', rows) + "\n");
    }
    private static bool IsLicense(string name) => HasStem(name, "license") || HasStem(name, "licence");
    private static bool IsNotice(string name) => IsLicense(name) || HasStem(name, "copying") || HasStem(name, "notice") || HasStem(name, "third-party-notices");
    private static bool HasStem(string name, string stem) => name.Equals(stem, StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase);
}
