using System.Runtime.ExceptionServices;
using System.Windows.Forms;

namespace VoiceNative;

internal static class MicrophoneHotkeyChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static nint Payload(MicrophoneHotkeyChoice choice) => (nint)(choice.Modifiers | choice.Key << 16);
    internal static void Run()
    {
        var api = new FakeApi(); var binding = new MicrophoneHotkey(api);
        var off = MicrophoneHotkeyChoice.Choices[0]; var first = MicrophoneHotkeyChoice.Choices[1]; var second = MicrophoneHotkeyChoice.Choices[2];
        Require(!binding.Apply(1, off, true) && !binding.Apply(1, first, false) && api.Calls == 0, "Idle/disabled registered a shortcut");
        Require(binding.Apply(1, first, true) && api.Calls == 1 && (api.Modifiers & 0x4000) != 0, "Active registration/no-repeat failed");
        var originalId = api.Id;
        Require(binding.Accept(0x312, originalId, Payload(first)), "Valid notification ignored");
        Require(!binding.Accept(0x312, originalId+1, Payload(first)) && !binding.Accept(0x100, originalId, Payload(first))
            && !binding.Accept(0x312, originalId, Payload(second)), "Unrelated notification accepted");
        Require(binding.Apply(1, first, true) && api.Calls == 1, "Every diagnostics tick re-registered shortcut");
        Require(!binding.Apply(1, first, false) && api.Removals == 1 && !binding.Accept(0x312, originalId, Payload(first)), "Leave retained shortcut");
        Require(binding.Apply(1, first, true) && api.Id != originalId && !binding.Accept(0x312, originalId, Payload(first)), "Previous-session queued notification accepted");
        Require(binding.Apply(1, second, true) && api.Removals == 2, "Selection change failed to release old shortcut");
        api.NextError = 1409;
        Require(!binding.Apply(1, first, true) && binding.Error == 1409 && !binding.Active, "Conflict concealed");
        var attempts = api.Calls;
        Require(!binding.Apply(1, first, true) && api.Calls == attempts, "Conflict retried on every tick");
        binding.Apply(1, off, false); api.NextError = 0;
        Require(binding.Apply(1, first, true), "Conflict recovery failed");
        api.CanRemove = false;
        Require(!binding.Apply(1, second, true) && !binding.Active && api.Calls == attempts+1, "Release failure leaked another registration");
        Require(!binding.Accept(0x312, api.Id, Payload(first)), "Release failure still toggles microphone");
        api.CanRemove = true; Require(binding.Release(), "Cleanup retry failed");
        Require(binding.Apply(2, first, true), "Recreated handle did not register");
        binding.WindowDestroyed(); Require(!binding.Active && !binding.Accept(0x312, api.Id, Payload(first)), "Destroyed window retained shortcut");
        Require(MicrophoneHotkeyChoice.Find("malformed").Key == 0, "Unknown setting selected a shortcut");
        Console.WriteLine("PASS: microphone hotkey opt-in, no-repeat, conflict, session IDs, selection changes, release failure and window lifetime.");
    }
    private sealed class FakeApi : IMicrophoneHotkeyApi
    {
        internal int Calls, Removals, Id, NextError;
        internal uint Modifiers;
        internal bool CanRemove = true;
        public int Register(nint window, int id, uint modifiers, uint key) { Calls++; Id = id; Modifiers = modifiers; return NextError; }
        public bool Unregister(nint window, int id) { Removals++; return CanRemove; }
    }
    internal static void RunNative()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Native(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Native hotkey checks deadline");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Native()
    {
        // Temporary F24 test combination; no user setting or actual keyboard input.
        var choice = new MicrophoneHotkeyChoice("fixture", "Ctrl+Alt+Shift+F24", 7, 0x87);
        var trackedApi = new TrackingNativeApi();
        var first = new MicrophoneHotkey(trackedApi);
        var second = new MicrophoneHotkey(new MicrophoneHotkeyApi());
        using var owner = new HotkeyWindow(first); using var contender = new Form();
        try
        {
            Require(first.Apply(owner.Handle, choice, true), "Native fixture combination unavailable: " + first.Error);
            Require(!second.Apply(contender.Handle, choice, true) && second.Error == 1409, "Native conflict not reported");
            CallNativeUi.Message(owner.Handle, 0x312, trackedApi.Id, Payload(choice));
            Require(owner.Toggles == 1, "Window message did not dispatch shortcut");
            Require(first.Release(), "Native unregister failed");
            CallNativeUi.Message(owner.Handle, 0x312, trackedApi.Id, Payload(choice));
            Require(owner.Toggles == 1, "Released shortcut still dispatches");
            second.Apply(contender.Handle, choice, false);
            Require(second.Apply(contender.Handle, choice, true), "Native released key not available to another window");
        }
        finally { first.Release(); second.Release(); }
        Console.WriteLine("PASS: native RegisterHotKey conflict/release/rebind and injected WM_HOTKEY dispatch. Not physical key input or game validation.");
    }
    private sealed class TrackingNativeApi : IMicrophoneHotkeyApi
    {
        private readonly MicrophoneHotkeyApi native = new();
        internal int Id;
        public int Register(nint window, int id, uint modifiers, uint key) { Id = id; return native.Register(window, id, modifiers, key); }
        public bool Unregister(nint window, int id) => native.Unregister(window, id);
    }
    private sealed class HotkeyWindow(MicrophoneHotkey binding) : Form
    {
        internal int Toggles;
        protected override void WndProc(ref Message message)
        {
            if (binding is not null && binding.Accept(message.Msg, message.WParam, message.LParam)) { Toggles++; message.Result = 0; return; }
            base.WndProc(ref message);
        }
    }
}
