using NAudio.CoreAudioApi;

namespace VoiceNative;

public sealed record AudioDeviceChoice(string? Id, string Name, bool Available = true)
{
    public override string ToString() => Name;
    public static AudioDeviceChoice[] List(bool capture)
    {
        using var enumerator = new MMDeviceEnumerator();
        var choices = new List<AudioDeviceChoice> { new(null, "Windows 기본 장치") };
        foreach (var device in enumerator.EnumerateAudioEndPoints(capture ? DataFlow.Capture : DataFlow.Render, DeviceState.Active))
        {
            using (device) choices.Add(new(device.ID, device.FriendlyName));
        }
        if (choices.Count == 1) choices[0] = choices[0] with { Name = "Windows 기본 장치 · 사용 가능한 장치 없음", Available = false };
        return choices.ToArray();
    }
    internal static AudioDeviceChoice[] PreserveSelection(AudioDeviceChoice[] choices, string? savedId)
        => savedId is null || choices.Any(c => c.Id == savedId) ? choices
            : [.. choices, new(savedId, "저장한 장치 · 연결 안 됨", false)];
    internal static string DefaultId(bool capture)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(capture ? DataFlow.Capture : DataFlow.Render, Role.Multimedia);
        return device.ID;
    }
}
