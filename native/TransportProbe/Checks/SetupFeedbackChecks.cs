namespace VoiceNative;

internal static class SetupFeedbackChecks
{
    internal static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var devices = new[] { new AudioDeviceChoice(null, "default"), new("usb-mic", "USB microphone") };
        var saved = AudioDeviceChoice.PreserveSelection(devices, "unplugged");
        Require(saved.Length == 3 && saved.Single(d => d.Id == "unplugged") is { Available: false }, "Missing saved endpoint silently replaced");
        Require(devices.Length == 2 && devices.All(d => d.Available), "Resolving missing endpoint changed enumerated devices");
        Require(ReferenceEquals(AudioDeviceChoice.PreserveSelection(devices, null), devices), "Default selection became fixed endpoint");
        Require(ReferenceEquals(AudioDeviceChoice.PreserveSelection(devices, "usb-mic"), devices), "Available saved endpoint duplicated");
        var plugged = AudioDeviceChoice.PreserveSelection([.. devices, new("unplugged", "Reconnected device")], "unplugged");
        Require(plugged.Count(d => d.Id == "unplugged") == 1 && plugged.Single(d => d.Id == "unplugged").Available, "Reconnected endpoint remained unavailable");
        foreach (var (server, room, name, field) in new[] {
            ("", "room", "name", CallInputField.Server), ("https://example.com", " ", "name", CallInputField.Room),
            ("https://example.com", "room", " ", CallInputField.Nickname), ("https://user:secret@example.com", "room", "name", CallInputField.Server),
            ("https://example.com?x=1", "room", "name", CallInputField.Server), ("https://example.com", "room space", "name", CallInputField.Room),
            ("https://example.com", "room", "a\nb", CallInputField.Nickname), ("https://example.com", "room", new string('x',25), CallInputField.Nickname) })
        {
            try { _ = CallRequest.Create(server, room, name, null, null); throw new InvalidOperationException("Invalid join input accepted"); }
            catch (CallInputException error) { Require(error.Field == field && !error.Message.Contains("secret", StringComparison.Ordinal), "Wrong input focus or credential exposure"); }
        }
        var request = CallRequest.Create(" https://example.com ", " 한글_방 ", " 사용자 ", "missing-id", null);
        Require(request.Room == "한글_방" && request.Nickname == "사용자" && request.CaptureId == "missing-id", "Valid trim or device identity changed");
        foreach (var state in new[] { "참가 실패 · server", "참가 실패 후 정리 오류", "재연결 실패 · 설정", "재연결 중단 · 오류", "자동 복구 중단 · 오류", "통화 정리 오류 · 오류" })
            Require(CallSetupFeedback.IsTerminalError(state), "Terminal error hidden: " + state);
        foreach (var state in new[] { "대기", "통화 중", "통화 중 · 재연결 완료", "재연결 중 · 1/5", "재연결 시도 실패 · 서버" })
            Require(!CallSetupFeedback.IsTerminalError(state), "Transient status presented as terminal error");
        Console.WriteLine("PASS: saved endpoint identity/missing/reconnected selection, field-specific join validation and terminal error presentation.");
    }
}
