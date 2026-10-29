namespace BuildTools;

internal static class OutputTransaction
{
    internal static void Commit(string root, string staging, string destination, string pendingZip, string zip, string archive, bool package,
        Action<string>? beforeMove = null)
    {
        foreach (var p in new[] { staging, destination, pendingZip, zip, archive }) WorkspaceFiles.Inside(root, p);
        var oldDirectory = Path.Combine(archive, Path.GetFileName(destination)); var oldZip = Path.Combine(archive, Path.GetFileName(zip));
        WorkspaceFiles.Inside(root, oldDirectory); WorkspaceFiles.Inside(root, oldZip);
        var archivedDirectory = false; var archivedZip = false; var installedDirectory = false; var installedZip = false;
        Directory.CreateDirectory(archive);
        try
        {
            if (Directory.Exists(destination)) { beforeMove?.Invoke("archive-directory"); Directory.Move(destination, oldDirectory); archivedDirectory = true; }
            if (File.Exists(zip)) { beforeMove?.Invoke("archive-zip"); File.Move(zip, oldZip); archivedZip = true; }
            beforeMove?.Invoke("install-directory"); Directory.Move(staging, destination); installedDirectory = true;
            if (package) { beforeMove?.Invoke("install-zip"); File.Move(pendingZip, zip); installedZip = true; }
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            Rollback(() => { if (installedZip) File.Move(zip, pendingZip); });
            Rollback(() => { if (installedDirectory) Directory.Move(destination, staging); });
            Rollback(() => { if (archivedZip) File.Move(oldZip, zip); });
            Rollback(() => { if (archivedDirectory) Directory.Move(oldDirectory, destination); });
            if (failures.Count > 1) throw new AggregateException("Release replacement/rollback failed; retained files remain in staging/archive", failures);
            throw;
            void Rollback(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
        }
    }
}
