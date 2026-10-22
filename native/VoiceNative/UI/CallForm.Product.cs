namespace VoiceNative;

public sealed partial class CallForm
{
    private CallSession? callSession;
    private readonly ComboBox captureDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox renderDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox muteMicrophone = new() { Text = "마이크 음소거", AutoSize = false, Size = new Size(150, 36), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label callState = new() { Text = "대기", Dock = DockStyle.Fill, AutoSize = true, Font = new Font("맑은 고딕", 14, FontStyle.Bold) };
    private readonly Label callHint = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "같은 방 이름으로 참가하면 바로 연결됩니다. 최대 6명." };
    private readonly FlowLayoutPanel participants = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Dictionary<string, (Panel Panel, Label State)> participantRows = new();
    private readonly Dictionary<string, DateTimeOffset> failedSince = new();
    private readonly Dictionary<string, (int Volume, bool Muted)> participantPreferences = new();
    private readonly Button createRoom = new() { Text = "새 방 만들기", AutoSize = true };
    private readonly Button refreshAudio = new() { Text = "장치 새로고침", AutoSize = true };
    private bool refreshingDevices, deviceSwitching;
    private DateTimeOffset nextDeviceCheck, connectedSince;
    private string? lastDeviceSignature;
    private string? SelectedCaptureId => (captureDevice.SelectedItem as AudioDeviceChoice)?.Id;
    private string? SelectedRenderId => (renderDevice.SelectedItem as AudioDeviceChoice)?.Id;

    private void BuildProductView(TableLayoutPanel diagnosticsLayout, TableLayoutPanel fields)
    {
        callSession = new(engine);
        callSession.Changed += state =>
        {
            AppendLog(state);
            if (state.StartsWith("통화 중", StringComparison.Ordinal) && !closeRequested && IsHandleCreated)
            {
                if (InvokeRequired) { try { BeginInvoke(() => connectedSince = DateTimeOffset.UtcNow); } catch (InvalidOperationException) { } }
                else connectedSince = DateTimeOffset.UtcNow;
            }
        };
        engine.Status += message =>
        {
            if (message.StartsWith("마이크 오류:", StringComparison.Ordinal)) callSession.RequestRecovery("마이크 연결 복구");
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var home = new TabPage("통화") { BackColor = Color.WhiteSmoke, Padding = new Padding(16) };
        var diagnosticsTab = new TabPage("상세 진단·고급 설정");
        Controls.Remove(diagnosticsLayout); diagnosticsTab.Controls.Add(diagnosticsLayout);
        diagnosticsLayout.RowStyles[0].Height = 0;
        tabs.TabPages.AddRange([home, diagnosticsTab]); Controls.Add(tabs);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 48, 112, 52, 106, 38 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); body.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        body.Controls.Add(callState, 0, 0); body.Controls.Add(fields, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        join.Text = "방 참가"; leave.Text = "나가기";
        var create = createRoom;
        var invite = new Button { Text = "방 이름 복사", AutoSize = true };
        create.Click += async (_, _) =>
        {
            if (callSession.WantsCall || callSession.IsBusy) return;
            room.Text = "voice-" + Guid.NewGuid().ToString("N")[..8];
            joinOperation = ConnectFromUiAsync(); await joinOperation;
        };
        invite.Click += (_, _) => { try { Clipboard.SetText(room.Text.Trim()); callHint.Text = "방 이름을 복사했어요. 같은 서버에서 이 방 이름으로 참가하세요."; } catch (Exception ex) { AppendLog("복사 실패: " + ex.Message); } };
        actions.Controls.AddRange([join, create, invite, leave, muteMicrophone]); body.Controls.Add(actions, 0, 2);
        var devices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3 };
        devices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); devices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); devices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        devices.Controls.Add(new Label { Text = "마이크", AutoSize = true }, 0, 0); devices.Controls.Add(captureDevice, 1, 0);
        devices.Controls.Add(new Label { Text = "출력", AutoSize = true }, 0, 1); devices.Controls.Add(renderDevice, 1, 1);
        var refresh = refreshAudio;
        refresh.Click += async (_, _) =>
        {
            var before = (SelectedCaptureId, SelectedRenderId);
            RefreshDeviceChoices(before.Item1, before.Item2);
            if (before != (SelectedCaptureId, SelectedRenderId)) await AudioSelectionChangedAsync();
        };
        devices.Controls.Add(refresh, 2, 0);
        var deviceHint = new Label { Text = "통화 중 장치를 바꾸면 같은 방에 다시 연결합니다.", AutoSize = true };
        devices.Controls.Add(deviceHint, 1, 2); body.Controls.Add(devices, 0, 3);
        body.Controls.Add(new Label { Text = "참가자 · 상대별 볼륨 0~200%", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 4);
        body.Controls.Add(participants, 0, 5); body.Controls.Add(callHint, 0, 6); home.Controls.Add(body);
        var settings = UserSettings.Load(UserSettings.DefaultPath);
        server.Text = settings.Server; room.Text = settings.Room; nickname.Text = settings.Nickname;
        RefreshDeviceChoices(settings.CaptureId, settings.RenderId);
        muteMicrophone.Checked = settings.Muted; engine.SetMicrophoneMuted(settings.Muted);
        muteMicrophone.CheckedChanged += (_, _) => { engine.SetMicrophoneMuted(muteMicrophone.Checked); if (!closeRequested) SaveUserSettings(); };
        captureDevice.SelectedIndexChanged += async (_, _) => await AudioSelectionChangedAsync();
        renderDevice.SelectedIndexChanged += async (_, _) => await AudioSelectionChangedAsync();
    }

    private void RefreshDeviceChoices(string? captureId, string? renderId)
    {
        refreshingDevices = true;
        try
        {
            foreach (var (combo, capture, wanted) in new[] { (captureDevice, true, captureId), (renderDevice, false, renderId) })
            {
                var choices = AudioDeviceChoice.List(capture);
                combo.Items.Clear(); combo.Items.AddRange(choices.Cast<object>().ToArray());
                combo.SelectedItem = choices.FirstOrDefault(c => c.Id == wanted) ?? choices[0];
            }
        }
        catch (Exception ex) { AppendLog("장치 목록 오류: " + ex.Message); }
        finally { refreshingDevices = false; }
    }
    private void SaveUserSettings()
    {
        try { new UserSettings(server.Text.Trim(), room.Text.Trim(), nickname.Text.Trim(), SelectedCaptureId, SelectedRenderId, muteMicrophone.Checked).Save(UserSettings.DefaultPath); }
        catch (Exception ex) { AppendLog("설정 저장 실패: " + ex.Message); }
    }
    private async Task ConnectFromUiAsync()
    {
        if (closeRequested || callSession is null || callSession.IsBusy || callSession.WantsCall || localMeasurement is { IsCompleted: false }) return;
        join.Enabled = false;
        try
        {
            var request = CallRequest.Create(server.Text, room.Text, nickname.Text, SelectedCaptureId, SelectedRenderId);
            engine.SetOpusProfile((OpusProfile)opusProfile.SelectedIndex);
            engine.SetMediaInterface((mediaInterface.SelectedItem as MediaInterfaceChoice)?.Address);
            engine.SetRawOutputPreference(rawOutput.Checked);
            engine.SetMicrophoneMuted(muteMicrophone.Checked); SaveUserSettings();
            lastDeviceSignature = null; failedSince.Clear();
            await callSession.ConnectAsync(request);
            if (!closeRequested) callHint.Text = "참가 완료 · 상대를 기다리거나 대화를 시작하세요.";
        }
        catch (Exception ex) { AppendLog("참가 실패: " + ex.Message); if (!closeRequested) callHint.Text = ex.Message; }
        finally { if (!closeRequested) { join.Enabled = !callSession.WantsCall; leave.Enabled = callSession.WantsCall; } }
    }
    private async Task DisconnectFromUiAsync()
    {
        if (callSession is null) return;
        leave.Enabled = false;
        try { await callSession.DisconnectAsync(); }
        catch (Exception ex) { AppendLog("종료 오류: " + ex.Message); }
        finally { if (!closeRequested) { join.Enabled = true; callHint.Text = "통화를 종료했어요."; } }
    }
    private async Task AudioSelectionChangedAsync()
    {
        if (refreshingDevices || deviceSwitching || callSession is null || callSession.IsBusy || closeRequested) return;
        SaveUserSettings();
        if (!callSession.WantsCall) return;
        deviceSwitching = true;
        try { await DisconnectFromUiAsync(); if (!closeRequested) await ConnectFromUiAsync(); }
        finally { deviceSwitching = false; }
    }
    private void UpdateProductView(CallDiagnostics snapshot)
    {
        if (callSession is null || closeRequested) return;
        callSession.Observe(snapshot);
        var locked = callSession.WantsCall || callSession.IsBusy;
        join.Enabled = !locked && localMeasurement is not { IsCompleted: false };
        createRoom.Enabled = join.Enabled;
        refreshAudio.Enabled = !callSession.IsBusy && !deviceSwitching;
        leave.Enabled = locked;
        server.ReadOnly = room.ReadOnly = nickname.ReadOnly = locked;
        captureDevice.Enabled = renderDevice.Enabled = !callSession.IsBusy && !deviceSwitching;
        callState.Text = callSession.State + (muteMicrophone.Checked ? " · 마이크 꺼짐" : "") + (latencyExperiment.SelectedIndex > 0 ? " · 극저지연 실험" : "");
        if (!locked && !snapshot.InCall) participantPreferences.Clear();
        foreach (var id in participantRows.Keys.Except(snapshot.Peers.Select(p => p.Id)).ToArray())
        { participants.Controls.Remove(participantRows[id].Panel); participantRows[id].Panel.Dispose(); participantRows.Remove(id); failedSince.Remove(id); }
        foreach (var peer in snapshot.Peers)
        {
            if (!participantRows.TryGetValue(peer.Id, out var row))
            {
                var panel = new Panel { Width = 640, Height = 75, BackColor = Color.White, Margin = new Padding(0, 0, 0, 8) };
                var label = new Label { Left = 10, Top = 8, Width = 600, Height = 24 };
                var volume = new TrackBar { Left = 5, Top = 33, Width = 390, Height = 35, Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 25 };
                var amount = new Label { Left = 405, Top = 39, Width = 75, Text = "100%" };
                var silent = new CheckBox { Left = 495, Top = 37, Width = 120, Text = "상대 음소거" };
                if (participantPreferences.TryGetValue(peer.Id, out var remembered)) { volume.Value = remembered.Volume; silent.Checked = remembered.Muted; }
                void ApplyVolume() { participantPreferences[peer.Id] = (volume.Value, silent.Checked); engine.SetPeerVolume(peer.Id, silent.Checked ? 0 : volume.Value / 100f); amount.Text = volume.Value + "%"; }
                volume.ValueChanged += (_, _) => ApplyVolume(); silent.CheckedChanged += (_, _) => ApplyVolume();
                panel.Controls.AddRange([label, volume, amount, silent]); participants.Controls.Add(panel);
                participantRows[peer.Id] = row = (panel, label);
                ApplyVolume();
            }
            if (participantPreferences.TryGetValue(peer.Id, out var preference)) engine.SetPeerVolume(peer.Id, preference.Muted ? 0 : preference.Volume / 100f);
            row.State.Text = peer.Name + " · " + (peer.State == "connected" ? peer.Route : "연결 중 · " + peer.State);
            if (peer.State is "failed" or "disconnected" || peer.IceState is "failed" or "disconnected")
            {
                failedSince.TryAdd(peer.Id, DateTimeOffset.UtcNow);
                if (DateTimeOffset.UtcNow - failedSince[peer.Id] > TimeSpan.FromSeconds(8)) callSession.RequestRecovery("통화 연결 복구");
            }
            else failedSince.Remove(peer.Id);
            if (peer.State == "connected" && peer.Precision?.LastPacketAgeSeconds > 8
                && DateTimeOffset.UtcNow - connectedSince > TimeSpan.FromSeconds(10)) callSession.RequestRecovery("음성 수신 연결 복구");
        }
        if (snapshot.InCall && DateTimeOffset.UtcNow - connectedSince > TimeSpan.FromSeconds(5)
            && snapshot.Microphone?.LastFrameAgeMs > 3000) callSession.RequestRecovery("마이크 입력 복구");
        if (snapshot.InCall && !callSession.IsBusy && DateTimeOffset.UtcNow >= nextDeviceCheck)
        {
            nextDeviceCheck = DateTimeOffset.UtcNow.AddSeconds(5);
            try
            {
                var signature = (SelectedCaptureId ?? AudioDeviceChoice.DefaultId(true)) + "|" + (SelectedRenderId ?? AudioDeviceChoice.DefaultId(false));
                if (lastDeviceSignature is not null && lastDeviceSignature != signature) callSession.RequestRecovery("Windows 기본 장치 변경");
                lastDeviceSignature = signature;
            }
            catch (Exception ex) { AppendLog("기본 장치 확인: " + ex.Message); }
        }
    }
}
