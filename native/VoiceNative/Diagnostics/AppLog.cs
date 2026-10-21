using System.Text.RegularExpressions;

namespace VoiceNative;

internal static class AppLog
{
    private static readonly object Sync = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyeongdongCalling", "logs");
    public static void Write(string message)
    {
        // Logs must not take down the call if the disk is full or inaccessible.
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                var safe = Regex.Replace(message, @"(?i)(token|credential|password|secret)=([^\s&]+)", "$1=[redacted]");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {safe}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
