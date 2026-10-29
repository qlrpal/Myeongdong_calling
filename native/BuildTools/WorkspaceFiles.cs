namespace BuildTools;

internal static class WorkspaceFiles
{
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static string Inside(string root, string path)
    {
        root = Path.GetFullPath(root); path = Path.GetFullPath(path);
        if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("Output escaped workspace: " + path);
        RejectLink(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        { current = Path.Combine(current, part); RejectLink(current); }
        return path;
    }
    private static void RejectLink(string path)
    {
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package paths must not contain links: " + path);
    }
    internal static string[] List(string directory)
    {
        var result = new List<string>(); Walk(new DirectoryInfo(directory), "");
        return result.Order(StringComparer.Ordinal).ToArray();
        void Walk(DirectoryInfo parent, string prefix)
        {
            RejectLink(parent.FullName);
            foreach (var item in parent.EnumerateFileSystemInfos())
            {
                RejectLink(item.FullName);
                var name = prefix + item.Name;
                if (item is DirectoryInfo child) Walk(child, name + "/"); else result.Add(name);
            }
        }
    }
    internal static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var name in List(source))
        {
            var target = Inside(destination, Path.Combine(destination, name));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(Path.Combine(source, name), target, overwrite: true);
        }
    }
}
