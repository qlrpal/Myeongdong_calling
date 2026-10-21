using NAudio.CoreAudioApi;

namespace VoiceNative;

public sealed record AudioDeviceChoice(string? Id, string Name)
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
        return choices.ToArray();
    }
    internal static string DefaultId(bool capture)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(capture ? DataFlow.Capture : DataFlow.Render, Role.Multimedia);
        return device.ID;
    }
}
