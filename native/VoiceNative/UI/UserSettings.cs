using System.Text.Json;

namespace VoiceNative;

internal sealed record UserSettings(string Server, string Room, string Nickname, string? CaptureId = null, string? RenderId = null, bool Muted = false, string MicrophoneHotkey = "none")
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyeongdongCalling", "settings.json");
    public static UserSettings Default => new("https://qlrpal.ddns.net/", "test", Environment.UserName[..Math.Min(24, Environment.UserName.Length)]);
    public static UserSettings Load(string path)
    {
        if (!File.Exists(path)) return Default;
        try
        {
            if (new FileInfo(path).Length > 32768) return Default;
            var value = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? Default;
            _ = CallRequest.Create(value.Server, value.Room, value.Nickname, value.CaptureId, value.RenderId);
            return value with { MicrophoneHotkey = MicrophoneHotkeyChoice.Find(value.MicrophoneHotkey).Id };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return Default; }
    }
    public void Save(string path)
    {
        _ = CallRequest.Create(Server, Room, Nickname, CaptureId, RenderId);
        if (MicrophoneHotkeyChoice.Find(MicrophoneHotkey).Id != MicrophoneHotkey) throw new ArgumentException("지원하지 않는 마이크 단축키입니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this, DiagnosticJson.Readable)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
