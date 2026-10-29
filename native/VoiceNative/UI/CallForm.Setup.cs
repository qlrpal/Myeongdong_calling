namespace VoiceNative;

public sealed partial class CallForm
{
    private readonly Label deviceHint = DesignLabel("통화 중 장치를 바꾸면 같은 방으로 다시 연결합니다.", 9, color: CallTheme.Secondary);
    private bool joinFeedbackError;
    private string? lastFeedbackState;

    private void InitializeSetupFeedback()
    {
        foreach (var input in new[] { server, room, nickname }) input.TextChanged += (_, _) => ClearJoinFeedback();
    }
    private void ShowJoinFeedback(string message, bool error)
    {
        joinFeedbackError = error;
        callHint.Text = message; callHint.ForeColor = error ? CallTheme.Warning : CallTheme.Secondary;
        if (joinHint is not null) { joinHint.Text = message; joinHint.ForeColor = callHint.ForeColor; }
    }
    private void ClearJoinFeedback()
    {
        if (!joinFeedbackError) return;
        ShowJoinFeedback("같은 서버와 방 이름으로 참가하세요. 최대 6명.", false);
        UpdateDeviceHint();
    }
    private void FocusSetupError(CallInputField field)
    {
        Control target = field switch
        {
            CallInputField.Server => server, CallInputField.Room => room, CallInputField.Nickname => nickname,
            CallInputField.Capture => captureDevice, _ => renderDevice
        };
        var inSettings = field is CallInputField.Server or CallInputField.Capture or CallInputField.Render;
        ToggleDesignedSettings(inSettings);
        if (inSettings)
        {
            deviceHint.Text = callHint.Text; deviceHint.ForeColor = CallTheme.Warning;
            designedSettings?.ScrollControlIntoView(target);
        }
        target.Focus(); if (target is TextBox text) text.SelectAll();
    }
    private void UpdateDeviceHint()
    {
        var missing = new List<string>();
        if (captureDevice.SelectedItem is AudioDeviceChoice { Available: false }) missing.Add("마이크");
        if (renderDevice.SelectedItem is AudioDeviceChoice { Available: false }) missing.Add("출력");
        deviceHint.Text = missing.Count > 0 ? string.Join("·", missing) + " 장치를 연결하거나 목록에서 다른 장치를 선택하세요."
            : "통화 중 장치를 바꾸면 같은 방으로 다시 연결합니다.";
        deviceHint.ForeColor = missing.Count > 0 ? CallTheme.Warning : CallTheme.Secondary;
    }
    private void ObserveSetupFeedback()
    {
        var state = callSession?.State;
        if (state is null || state == lastFeedbackState) return;
        lastFeedbackState = state;
        if (callSession?.WantsCall == false && CallSetupFeedback.IsTerminalError(state)) ShowJoinFeedback(state, true);
    }
    private static void RequireAvailableDevice(ComboBox combo, CallInputField field)
    {
        if (combo.SelectedItem is not AudioDeviceChoice { Available: true })
            throw new CallInputException(field == CallInputField.Capture ? "사용할 마이크를 연결하거나 다른 입력 장치를 선택하세요."
                : "사용할 출력 장치를 연결하거나 다른 출력 장치를 선택하세요.", field);
    }
}

internal static class CallSetupFeedback
{
    internal static bool IsTerminalError(string state) => state.StartsWith("참가 실패", StringComparison.Ordinal)
        || state.StartsWith("재연결 실패", StringComparison.Ordinal) || state.StartsWith("재연결 중단", StringComparison.Ordinal)
        || state.StartsWith("자동 복구 중단", StringComparison.Ordinal) || state.StartsWith("통화 정리 오류", StringComparison.Ordinal);
}
