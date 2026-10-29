using System.Security.Cryptography;

namespace BuildTools;

internal static class PackageVerifier
{
    internal const string ManifestName = "release-manifest.json";
    internal static readonly string[] Required = ["VoiceNative.exe", "VoiceNative.dll", "VoiceNative.deps.json", "VoiceNative.runtimeconfig.json",
        "SIPSorcery.dll", "SIPSorcery.LICENSE.md", "Opus.LICENSE.txt", "opus.dll", "README.md", "시작하기.md", "베타안내.md", "THIRD-PARTY-NOTICES.md"];
    private static readonly HashSet<string> ForbiddenDirectories = new(["server", "deploy", "reports", ".local", "bin", "obj", "node_modules"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ForbiddenExtensions = new([".pdb", ".etl", ".pcap", ".pcapng", ".dmp", ".key", ".pem", ".pfx", ".wav", ".zip", ".cs", ".tsx", ".ps1", ".sh", ".csproj", ".props", ".targets", ".slnx", ".mjs", ".js"], StringComparer.OrdinalIgnoreCase);
    internal static void CheckPaths(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var p = file.Replace('\\', '/'); var parts = p.Split('/');
            if (string.IsNullOrEmpty(p) || p.StartsWith('/') || p.Contains(':') || parts.Any(s => s is "" or "." or "..")
                || parts.Any(ForbiddenDirectories.Contains) || ForbiddenExtensions.Contains(Path.GetExtension(p))
                || parts.Any(s => s.StartsWith("TransportProbe", StringComparison.OrdinalIgnoreCase) || s.StartsWith("BuildTools", StringComparison.OrdinalIgnoreCase)
                    || s.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || s.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                    || s.StartsWith("app.log", StringComparison.OrdinalIgnoreCase) || s.StartsWith("voice-diagnostics-", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Development/private or unsafe beta path: " + file);
        }
    }
    internal static void CheckAssets(string[] names, bool contained)
    {
        foreach (var name in Required.Concat(contained ? ["coreclr.dll", "System.Private.CoreLib.dll", "System.Windows.Forms.dll"] : Array.Empty<string>()))
            if (!names.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Missing beta asset: " + name);
        foreach (var name in new[] { "NAudio.dll", "NAudio.Asio.dll", "NAudio.Dmo.dll", "NAudio.WinForms.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll" })
            if (names.Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Unused module returned to beta: " + name);
    }
    internal static PackageFile Describe(string directory, string name)
    {
        using var stream = File.OpenRead(Path.Combine(directory, name));
        return new(name, stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
    internal static PackageManifest Verify(string directory)
    {
        var manifest = PackageJson.Read<PackageManifest>(Path.Combine(directory, ManifestName));
        if (manifest.SchemaVersion != 1 || manifest.Channel != "beta" || manifest.Rid != "win-x64"
            || manifest.Version is null || !manifest.Version.Contains("-beta.", StringComparison.Ordinal) || manifest.Files is null)
            throw new InvalidDataException("Invalid beta manifest");
        if (manifest.Files.Any(f => f is null || f.Path is null || f.Bytes < 0 || f.Sha256 is null))
            throw new InvalidDataException("Invalid beta file record");
        var names = WorkspaceFiles.List(directory).Where(f => f != ManifestName).ToArray();
        CheckPaths(names); CheckAssets(names, manifest.SelfContained);
        // Compare the entire inventory before reading any manifest-provided file path.
        if (!names.SequenceEqual(manifest.Files.Select(f => f.Path), StringComparer.Ordinal)) throw new InvalidDataException("Package inventory mismatch");
        foreach (var file in manifest.Files)
            if (Describe(directory, file.Path) != file) throw new InvalidDataException("Package hash mismatch: " + file.Path);
        return manifest;
    }
}
