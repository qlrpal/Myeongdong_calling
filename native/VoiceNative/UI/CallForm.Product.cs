namespace VoiceNative;

public sealed partial class CallForm
{
    private CallSession? callSession;
    private readonly ComboBox captureDevice = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox renderDevice = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox muteMicrophone = new CallToggle() { Text = "마이크 음소거", AutoSize = false, Size = new Size(150, 36), Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label callState = new() { Text = "대기", Dock = DockStyle.Fill, AutoSize = true, Font = new Font("맑은 고딕", 14, FontStyle.Bold) };
    private readonly Label callHint = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "같은 방 이름으로 참가하면 바로 연결됩니다. 최대 6명." };
    private readonly FlowLayoutPanel participants = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Dictionary<string, ParticipantCardControls> participantRows = new();
    private readonly Dictionary<string, DateTimeOffset> failedSince = new();
    private readonly Dictionary<string, (int Volume, bool Muted)> participantPreferences = new();
    private readonly Button createRoom = new() { Text = "새 방 만들기", AutoSize = true };
    private readonly Button refreshAudio = new() { Text = "장치 새로고침", AutoSize = true };
    private bool refreshingDevices, deviceSwitching;
    private DateTimeOffset nextDeviceCheck, connectedSince;
    private string? lastDeviceSignature;
    private string? SelectedCaptureId => (captureDevice.SelectedItem as AudioDeviceChoice)?.Id;
    private string? SelectedRenderId => (renderDevice.SelectedItem as AudioDeviceChoice)?.Id;

    private void BuildProductView(TableLayoutPanel diagnosticsLayout)
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
        BuildDesignedView(diagnosticsLayout);
        var settings = UserSettings.Load(UserSettings.DefaultPath);
        server.Text = settings.Server; room.Text = settings.Room; nickname.Text = settings.Nickname;
        RefreshDeviceChoices(settings.CaptureId, settings.RenderId);
        muteMicrophone.Checked = settings.Muted; engine.SetMicrophoneMuted(settings.Muted);
        InitializeMicrophoneHotkey(settings);
        InitializeSetupFeedback();
        muteMicrophone.CheckedChanged += (_, _) => { engine.SetMicrophoneMuted(muteMicrophone.Checked); if (!closeRequested) SaveUserSettings(); };
        captureDevice.SelectedIndexChanged += async (_, _) => await AudioSelectionChangedAsync();
        renderDevice.SelectedIndexChanged += async (_, _) => await AudioSelectionChangedAsync();
    }

    private bool RefreshDeviceChoices(string? captureId, string? renderId)
    {
        refreshingDevices = true;
        try
        {
            // Enumerate both before changing either list; a failure must not partially switch devices.
            var inputs = AudioDeviceChoice.PreserveSelection(AudioDeviceChoice.List(true), captureId);
            var outputs = AudioDeviceChoice.PreserveSelection(AudioDeviceChoice.List(false), renderId);
            foreach (var (combo, choices, wanted) in new[] { (captureDevice, inputs, captureId), (renderDevice, outputs, renderId) })
            {
                combo.Items.Clear(); combo.Items.AddRange(choices.Cast<object>().ToArray());
                combo.SelectedItem = choices.FirstOrDefault(c => c.Id == wanted) ?? choices[0];
            }
            UpdateDeviceHint(); return true;
        }
        catch (Exception ex) { AppendLog("장치 목록 오류: " + ex.Message); ShowJoinFeedback("장치 목록을 불러오지 못했습니다. 설정에서 장치 새로고침을 눌러 주세요.", true); deviceHint.Text = callHint.Text; deviceHint.ForeColor = CallTheme.Warning; return false; }
        finally { refreshingDevices = false; }
    }
    private void SaveUserSettings()
    {
        try { new UserSettings(server.Text.Trim(), room.Text.Trim(), nickname.Text.Trim(), SelectedCaptureId, SelectedRenderId, muteMicrophone.Checked, SelectedHotkey).Save(UserSettings.DefaultPath); }
        catch (Exception ex) { AppendLog("설정 저장 실패: " + ex.Message); }
    }
    private async Task ConnectFromUiAsync()
    {
        if (closeRequested || callSession is null || callSession.IsBusy || callSession.WantsCall || localMeasurement is { IsCompleted: false }) return;
        join.Enabled = false;
        try
        {
            var request = CallRequest.Create(server.Text, room.Text, nickname.Text, SelectedCaptureId, SelectedRenderId);
            if (!RefreshDeviceChoices(request.CaptureId, request.RenderId)) { ToggleDesignedSettings(true); return; }
            RequireAvailableDevice(captureDevice, CallInputField.Capture); RequireAvailableDevice(renderDevice, CallInputField.Render);
            ShowJoinFeedback("방에 연결 중입니다…", false);
            engine.SetOpusProfile((OpusProfile)opusProfile.SelectedIndex);
            engine.SetMediaInterface((mediaInterface.SelectedItem as MediaInterfaceChoice)?.Address);
            engine.SetRawOutputPreference(rawOutput.Checked);
            engine.SetMicrophoneMuted(muteMicrophone.Checked); SaveUserSettings();
            lastDeviceSignature = null; failedSince.Clear();
            await callSession.ConnectAsync(request);
            if (!closeRequested && callSession.WantsCall) ShowJoinFeedback("참가 완료 · 상대를 기다리거나 대화를 시작하세요.", false);
        }
        catch (CallInputException ex) { if (!closeRequested) { ShowJoinFeedback(ex.Message, true); FocusSetupError(ex.Field); } }
        catch (Exception ex) { AppendLog("참가 실패: " + ex.Message); if (!closeRequested) ShowJoinFeedback("참가 실패 · " + CallFailureMessage.Describe(ex), true); }
        finally { if (!closeRequested) { join.Enabled = !callSession.WantsCall; leave.Enabled = callSession.WantsCall; RefreshMicrophoneHotkey(); } }
    }
    private async Task<bool> DisconnectFromUiAsync()
    {
        if (callSession is null) return true;
        microphoneHotkey.Release();
        leave.Enabled = false;
        try { await callSession.DisconnectAsync(); if (!closeRequested) ShowJoinFeedback("통화를 종료했어요.", false); return true; }
        catch (Exception ex)
        {
            AppendLog("종료 오류: " + ex.Message);
            if (!closeRequested) ShowJoinFeedback("정리 오류 · 진단을 저장하고 앱을 다시 시작하세요.", true);
            return false;
        }
        finally { if (!closeRequested) join.Enabled = !callSession.IsBusy; }
    }
    private async Task AudioSelectionChangedAsync()
    {
        if (refreshingDevices || deviceSwitching || callSession is null || callSession.IsBusy || closeRequested) return;
        ClearJoinFeedback(); UpdateDeviceHint();
        if (captureDevice.SelectedItem is AudioDeviceChoice { Available: false } || renderDevice.SelectedItem is AudioDeviceChoice { Available: false }) return;
        SaveUserSettings();
        if (!callSession.WantsCall) return;
        deviceSwitching = true;
        try { if (await DisconnectFromUiAsync() && !closeRequested) await ConnectFromUiAsync(); }
        finally { deviceSwitching = false; }
    }
    private void UpdateProductView(CallDiagnostics snapshot)
    {
        if (callSession is null || closeRequested) return;
        callSession.Observe(snapshot);
        ObserveSetupFeedback();
        RefreshMicrophoneHotkey();
        var locked = callSession.WantsCall || callSession.IsBusy;
        join.Enabled = !locked && localMeasurement is not { IsCompleted: false };
        createRoom.Enabled = join.Enabled;
        refreshAudio.Enabled = !callSession.IsBusy && !deviceSwitching;
        leave.Enabled = locked;
        server.ReadOnly = room.ReadOnly = nickname.ReadOnly = locked;
        captureDevice.Enabled = renderDevice.Enabled = !callSession.IsBusy && !deviceSwitching;
        callState.Text = callSession.State + (muteMicrophone.Checked ? " · 마이크 꺼짐" : "") + (latencyExperiment.SelectedIndex > 0 ? " · 극저지연 실험" : "");
        UpdateDesignedView(snapshot, locked);
        if (!locked && !snapshot.InCall) participantPreferences.Clear();
        foreach (var id in participantRows.Keys.Except(snapshot.Peers.Select(p => p.Id)).ToArray())
        { participants.Controls.Remove(participantRows[id].Panel); participantRows[id].Panel.Dispose(); participantRows.Remove(id); failedSince.Remove(id); }
        foreach (var peer in snapshot.Peers)
        {
            if (!participantRows.TryGetValue(peer.Id, out var row))
            {
                var card = CreateParticipantCard(peer.Name);
                var panel = card.Panel; var volume = card.Volume; var silent = card.Muted;
                if (participantPreferences.TryGetValue(peer.Id, out var remembered)) { volume.Value = remembered.Volume; silent.Checked = remembered.Muted; }
                void ApplyVolume() { participantPreferences[peer.Id] = (volume.Value, silent.Checked); engine.SetPeerVolume(peer.Id, silent.Checked ? 0 : volume.Value / 100f); card.Amount.Text = volume.Value + "%"; UpdateParticipantAudioStatus(card, peer.Id, null); }
                volume.ValueChanged += (_, _) => ApplyVolume(); silent.CheckedChanged += (_, _) => ApplyVolume();
                participants.Controls.Add(panel); participantRows[peer.Id] = row = card;
                ApplyVolume();
            }
            if (participantPreferences.TryGetValue(peer.Id, out var preference)) engine.SetPeerVolume(peer.Id, preference.Muted ? 0 : preference.Volume / 100f);
            row.State.Text = peer.State switch { "connected" => "연결됨 · " + peer.Route, "failed" or "disconnected" or "closed" => "연결 끊김", _ => "연결 중" };
            row.State.ForeColor = peer.State == "connected" ? CallTheme.Accent : CallTheme.Warning;
            UpdateParticipantAudioStatus(row, peer.Id, peer);

            if (peer.State is "failed" or "disconnected" || peer.IceState is "failed" or "disconnected")
            {
                failedSince.TryAdd(peer.Id, DateTimeOffset.UtcNow);
                if (DateTimeOffset.UtcNow - failedSince[peer.Id] > TimeSpan.FromSeconds(8)) callSession.RequestRecovery("통화 연결 복구");
            }
            else failedSince.Remove(peer.Id);
            if (peer.State == "connected" && peer.Precision?.LastPacketAgeSeconds > 8
                && DateTimeOffset.UtcNow - connectedSince > TimeSpan.FromSeconds(10)) callSession.RequestRecovery("음성 수신 연결 복구");
        }
        ArrangeParticipantCards();
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

    private void UpdateParticipantAudioStatus(ParticipantCardControls card, string id, PeerDiagnostics? peer)
    {
        if (peer is not null) card.RemoteMuted = ParticipantAudioStatus.RemoteMuted(peer.State, peer.Latency);
        var localMute = participantPreferences.TryGetValue(id, out var preference) && (preference.Muted || preference.Volume == 0);
        card.AudioState.Text = ParticipantAudioStatus.Describe(card.RemoteMuted, localMute);
        card.AudioState.ForeColor = card.RemoteMuted == true || localMute ? CallTheme.Warning : CallTheme.Secondary;
    }
}
