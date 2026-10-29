namespace BuildTools;

internal sealed record BuildOptions(string NativeRoot, bool NoRestore, bool Package, bool ReadyToRun, bool StageOnly, bool Beta, bool FrameworkDependent)
{
    internal static BuildOptions Parse(string[] args)
    {
        string? root = null; var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--native-root" && root is null && i+1 < args.Length) { root = args[++i]; continue; }
            if (args[i] is not ("--no-restore" or "--package" or "--ready-to-run" or "--stage-only" or "--beta" or "--framework-dependent") || !flags.Add(args[i]))
                throw new ArgumentException("Unknown, duplicate or incomplete option: " + args[i]);
        }
        var value = new BuildOptions(Path.GetFullPath(root ?? throw new ArgumentException("--native-root is required")), flags.Contains("--no-restore"),
            flags.Contains("--package"), flags.Contains("--ready-to-run"), flags.Contains("--stage-only"), flags.Contains("--beta"), flags.Contains("--framework-dependent"));
        if (value.NoRestore && (value.ReadyToRun || value.Beta)) throw new ArgumentException("RID-specific builds require restore; omit -NoRestore");
        if (value.StageOnly && value.Package) throw new ArgumentException("StageOnly does not create/replace a release ZIP");
        if (value.FrameworkDependent && !value.Beta) throw new ArgumentException("FrameworkDependent is a beta package option");
        if (!File.Exists(Path.Combine(value.NativeRoot, "VoiceNative", "VoiceNative.csproj"))) throw new ArgumentException("Native source root not found");
        return value;
    }
}
