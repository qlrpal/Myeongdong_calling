using System.Runtime.InteropServices;

namespace VoiceNative;

internal sealed record MicrophoneHotkeyChoice(string Id, string Name, uint Modifiers, uint Key)
{
    internal static readonly MicrophoneHotkeyChoice[] Choices =
    [new("none", "사용 안 함", 0, 0), new("ctrl-alt-m", "Ctrl + Alt + M", 3, 0x4D),
        new("ctrl-shift-m", "Ctrl + Shift + M", 6, 0x4D), new("alt-shift-m", "Alt + Shift + M", 5, 0x4D)];
    internal static MicrophoneHotkeyChoice Find(string? id) => Choices.FirstOrDefault(x => x.Id == id) ?? Choices[0];
    public override string ToString() => Name;
}

internal interface IMicrophoneHotkeyApi
{
    int Register(nint window, int id, uint modifiers, uint key);
    bool Unregister(nint window, int id);
}

internal sealed class MicrophoneHotkeyApi : IMicrophoneHotkeyApi
{
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    public int Register(nint window, int id, uint modifiers, uint key)
        => RegisterHotKey(window, id, modifiers, key) ? 0 : Marshal.GetLastWin32Error();
    public bool Unregister(nint window, int id) => UnregisterHotKey(window, id);
}

// Window-thread only. No hook, keyboard polling, worker, or audio-path work.
internal sealed class MicrophoneHotkey(IMicrophoneHotkeyApi api)
{
    private nint window;
    private int id, generation;
    private MicrophoneHotkeyChoice? choice;
    private (nint Window, string Choice, bool InCall)? requested;
    private bool listening;
    internal int Error { get; private set; }
    internal bool Active => listening;
    internal bool Apply(nint target, MicrophoneHotkeyChoice wanted, bool inCall)
    {
        var request = (target, wanted.Id, inCall);
        if (requested == request) return listening;
        if (!Release()) { Error = -1; return false; }
        requested = request; Error = 0;
        if (!inCall || target == 0 || wanted.Key == 0) return false;
        // New ID rejects queued notifications from the previous selection/session.
        id = 0x6000 + (++generation % 0x2000);
        Error = api.Register(target, id, wanted.Modifiers | 0x4000, wanted.Key); // MOD_NOREPEAT
        if (Error != 0) return false;
        window = target; choice = wanted; listening = true; return true;
    }
    internal bool Accept(int message, nint messageId, nint payload)
        => listening && message == 0x312 && messageId == id && choice is not null
            && ((ulong)(nuint)payload & 0xF) == choice.Modifiers
            && (((ulong)(nuint)payload >> 16) & 0xFFFF) == choice.Key;
    internal bool Release()
    {
        listening = false; requested = null;
        if (window != 0 && !api.Unregister(window, id)) return false;
        window = 0; choice = null; Error = 0; return true;
    }
    internal void WindowDestroyed()
    {
        // Windows removes registrations owned by a destroyed window.
        window = 0; choice = null; requested = null; listening = false; Error = 0;
    }
}
