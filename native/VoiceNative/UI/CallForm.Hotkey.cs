namespace VoiceNative;

public sealed partial class CallForm
{
    private readonly MicrophoneHotkey microphoneHotkey = new(new MicrophoneHotkeyApi());
    private readonly ComboBox hotkeyChoice = new CallComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label hotkeyHint = DesignLabel("통화 중에만 작동합니다.", 9, color: CallTheme.Secondary);
    private bool loadingHotkey;
    private string SelectedHotkey => (hotkeyChoice.SelectedItem as MicrophoneHotkeyChoice)?.Id ?? "none";

    private void InitializeMicrophoneHotkey(UserSettings settings)
    {
        loadingHotkey = true;
        hotkeyChoice.Items.AddRange(MicrophoneHotkeyChoice.Choices);
        hotkeyChoice.SelectedItem = MicrophoneHotkeyChoice.Find(settings.MicrophoneHotkey);
        loadingHotkey = false;
        hotkeyChoice.SelectedIndexChanged += (_, _) =>
        { if (!loadingHotkey && !closeRequested) { RefreshMicrophoneHotkey(); SaveUserSettings(); } };
        RefreshMicrophoneHotkey();
    }
    private void RefreshMicrophoneHotkey()
    {
        var selected = MicrophoneHotkeyChoice.Find(SelectedHotkey);
        var inCall = !closeRequested && callSession?.WantsCall == true;
        microphoneHotkey.Apply(IsHandleCreated ? Handle : 0, selected, inCall);
        hotkeyHint.Text = microphoneHotkey.Error != 0
            ? $"단축키 등록 실패 ({microphoneHotkey.Error}). 다른 조합을 선택하세요."
            : selected.Key == 0 ? "다른 앱이 켜져 있어도 마이크를 전환할 수 있습니다."
            : microphoneHotkey.Active ? selected.Name + " · 통화 중 활성"
            : "통화를 시작하면 활성화됩니다.";
        hotkeyHint.ForeColor = microphoneHotkey.Error != 0 ? CallTheme.Warning : CallTheme.Secondary;
    }
    protected override void WndProc(ref Message m)
    {
        if (microphoneHotkey is not null && microphoneHotkey.Accept(m.Msg, m.WParam, m.LParam))
        {
            if (!closeRequested && callSession?.WantsCall == true)
            {
                muteMicrophone.Checked = !muteMicrophone.Checked;
                AppendLog(muteMicrophone.Checked ? "단축키 · 마이크 꺼짐" : "단축키 · 마이크 켜짐");
            }
            m.Result = 0; return;
        }
        base.WndProc(ref m);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (microphoneHotkey is not null) { microphoneHotkey.Release(); microphoneHotkey.WindowDestroyed(); }
        base.OnHandleDestroyed(e);
    }
}
