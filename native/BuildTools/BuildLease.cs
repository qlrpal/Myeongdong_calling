namespace BuildTools;

internal static class BuildLease
{
    internal static FileStream Acquire(string root)
    {
        var path = WorkspaceFiles.Inside(root, Path.Combine(root, ".local", "build.lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("Another build may be active, or the build lock cannot be opened. Prior output was not changed.", error); }
    }
}
