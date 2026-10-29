using System.Drawing.Drawing2D;

namespace VoiceNative;

internal static class CallTheme
{
    internal static readonly Color Base = ColorTranslator.FromHtml("#111316");
    internal static readonly Color Window = ColorTranslator.FromHtml("#171a1f");
    internal static readonly Color Surface = ColorTranslator.FromHtml("#1d2127");
    internal static readonly Color Raised = ColorTranslator.FromHtml("#242930");
    internal static readonly Color Border = ColorTranslator.FromHtml("#343b45");
    internal static readonly Color Text = ColorTranslator.FromHtml("#f3f5f7");
    internal static readonly Color Secondary = ColorTranslator.FromHtml("#a8b0bb");
    internal static readonly Color Accent = ColorTranslator.FromHtml("#70d6a2");
    internal static readonly Color Warning = ColorTranslator.FromHtml("#f4be62");
    internal static readonly Color Danger = ColorTranslator.FromHtml("#ff7c7c");

    internal static void Apply(Control root)
    {
        root.BackColor = root is TextBoxBase ? Base : Window;
        root.ForeColor = Text;
        if (root is Button button) Style(button);
        if (root is TextBoxBase text) text.BorderStyle = text is RichTextBox ? BorderStyle.None : BorderStyle.FixedSingle;
        if (root is ComboBox combo)
        {
            combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Raised; combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.ItemHeight = Math.Max(24, combo.Font.Height + 8);
            combo.DrawItem += (_, args) =>
            {
                using var fill = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Border : Raised);
                args.Graphics.FillRectangle(fill, args.Bounds);
                var label = args.Index >= 0 && args.Index < combo.Items.Count ? combo.Items[args.Index]?.ToString() ?? "" : combo.Text;
                TextRenderer.DrawText(args.Graphics, label, combo.Font, Rectangle.Inflate(args.Bounds, -6, 0), combo.Enabled ? Text : Secondary, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            combo.EnabledChanged += (_, _) => combo.Invalidate();
        }
        if (root is ScrollableControl or ComboBox) CallNativeUi.Dark(root);
        if (root is TrackBar slider) slider.BackColor = Surface;
        foreach (Control child in root.Controls) Apply(child);
    }
    internal static void Style(Button button, bool primary = false, bool danger = false)
    {
        button.FlatStyle = FlatStyle.Flat; button.Cursor = Cursors.Hand;
        button.BackColor = primary ? Accent : danger ? Color.FromArgb(58, 32, 35) : Raised;
        button.ForeColor = primary ? Color.FromArgb(16, 34, 25) : danger ? Danger : Text;
        button.FlatAppearance.BorderColor = danger ? Color.FromArgb(103, 56, 61) : Border;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(131, 225, 177) : Color.FromArgb(42, 48, 56);
        button.Padding = new Padding(8, 0, 8, 0); button.MinimumSize = new Size(0, 36);
    }
}

internal sealed class CallCard : Panel
{
    internal CallCard() { DoubleBuffered = true; BackColor = CallTheme.Surface; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(CallTheme.Border);
        using var path = new GraphicsPath();
        var r = new Rectangle(0, 0, Width - 1, Height - 1); const int d = 18;
        path.AddArc(r.Left, r.Top, d, d, 180, 90); path.AddArc(r.Right-d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); path.AddArc(r.Left, r.Bottom-d, d, d, 90, 90);
        path.CloseFigure(); e.Graphics.DrawPath(pen, path);
    }
}

public sealed partial class CallForm
{
    private static readonly string[] MetricTitles = ["연결 경로 · 첫 상대", "망 RTT · 왕복 시간", "수신 지터", "수신 손실 · 최근 보고", "PLC · 보정 오디오 시간", "앱 재생 큐", "입력 레벨", "수신 레벨"];
    private readonly Label[] designedMetricValues = new Label[8];
    private Panel? designedJoin, designedCall, designedSettings, designedMain;
    private TableLayoutPanel? designedShell;
    private Control? designedDiagnostics;
    private Label? roomHeading, participantCount, selfName, selfState, joinHint, waitingHint;
    private Panel? selfCard, waitingCard;
    private Button? joinSettings;

    private static Label DesignLabel(string text, float size = 10, bool bold = false, Color? color = null)
        => new() { Text = text, AutoSize = false, AutoEllipsis = true, ForeColor = color ?? CallTheme.Text,
            Font = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular), BackColor = Color.Transparent };
    private static Button DesignButton(string text, EventHandler action)
    {
        var result = new Button { Text = text, AutoSize = true, Height = 38, Margin = new Padding(4) };
        CallTheme.Style(result); result.Click += action; return result;
    }
    private static void Put(Control parent, Control child, int x, int y, int width, int height)
    { parent.Controls.Add(child); child.SetBounds(x, y, width, height); }
    private static void DesignInput(Panel parent, string title, Control input, int y)
    {
        Put(parent, DesignLabel(title, 9, true, CallTheme.Secondary), 28, y, 310, 24);
        parent.Controls.Add(input); input.AccessibleName = title; input.Dock = DockStyle.None; input.Margin = Padding.Empty;
        input.SetBounds(28, y + 28, Math.Max(100, parent.ClientSize.Width - 56), 34);
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        parent.SizeChanged += (_, _) => input.Width = Math.Max(100, parent.ClientSize.Width - 56);
    }
    private void BuildDesignedView(TableLayoutPanel diagnostics)
    {
        SuspendLayout();
        ClientSize = new Size(960, 720); MinimumSize = new Size(776, 639);
        BackColor = CallTheme.Window; ForeColor = CallTheme.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        CallTheme.Apply(diagnostics);
        Controls.Remove(diagnostics);
        var diagnosticPage = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = CallTheme.Window };
        var back = DesignButton("← 통화로 돌아가기", (_, _) => ShowDesignedPage(false));
         back.Dock = DockStyle.Top; back.Height = 44;
        var diagnosticBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        diagnosticBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
        diagnosticBody.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        diagnosticBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        for (var column = 0; column < 4; column++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var row = 0; row < 2; row++) metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        for (var i = 0; i < MetricTitles.Length; i++)
        {
            var tile = new CallCard { Dock = DockStyle.Fill, Margin = new Padding(5), Padding = new Padding(12) };
            var label = DesignLabel(MetricTitles[i], 8, color: CallTheme.Secondary); label.Dock = DockStyle.Top; label.Height = 24;
            var value = DesignLabel("미측정", 14, true); value.Dock = DockStyle.Fill;
            tile.Controls.Add(value); tile.Controls.Add(label); metrics.Controls.Add(tile, i % 4, i / 4); designedMetricValues[i] = value;
        }
        var diagnosticActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var reportCopy = DesignButton("진단 복사", (_, _) => { try { Clipboard.SetText(ReportJson()); } catch (Exception ex) { AppendLog("복사 실패: " + ex.Message); } });
        var diagnosticStack = new Panel { Dock = DockStyle.Fill };
        var simpleLog = new Panel { Dock = DockStyle.Fill };
        var logTitle = DesignLabel("이벤트 로그 · 진단 수치는 전체 음성 지연이 아닙니다.", 9, color: CallTheme.Secondary); logTitle.Dock = DockStyle.Top; logTitle.Height = 30;
        log.Dock = DockStyle.Fill; simpleLog.Controls.Add(log); simpleLog.Controls.Add(logTitle);
        diagnostics.Dock = DockStyle.Fill; diagnostics.Visible = false; diagnosticStack.Controls.Add(diagnostics); diagnosticStack.Controls.Add(simpleLog);
        var more = DesignButton("상세 진단·고급 설정", (_, _) =>
        {
            var advanced = !diagnostics.Visible;
            diagnostics.Visible = advanced; simpleLog.Visible = !advanced;
            metrics.Visible = !advanced; diagnosticBody.RowStyles[0].Height = advanced ? 0 : 184;
            if (advanced) { diagnostics.Controls.Add(log, 0, 5); diagnostics.BringToFront(); }
            else { simpleLog.Controls.Add(log); log.BringToFront(); }
        });
        diagnosticActions.Controls.AddRange([reportCopy, more]);
        diagnosticBody.Controls.Add(metrics, 0, 0); diagnosticBody.Controls.Add(diagnosticActions, 0, 1); diagnosticBody.Controls.Add(diagnosticStack, 0, 2);
        diagnosticPage.Controls.Add(diagnosticBody); diagnosticPage.Controls.Add(back);
        // The join form owns room/nickname; the settings panel owns the server field.
        diagnostics.RowStyles[0].Height = 0;
        Controls.Add(diagnosticPage); designedDiagnostics = diagnosticPage;
        designedShell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        designedShell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        designedShell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        designedMain = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = CallTheme.Window };
        designedSettings = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = CallTheme.Surface, Visible = false, AutoScroll = true };
        CallNativeUi.Dark(designedSettings);
        designedShell.Controls.Add(designedMain, 0, 0); designedShell.Controls.Add(designedSettings, 1, 0); Controls.Add(designedShell);

        designedJoin = new Panel { Dock = DockStyle.Fill, BackColor = CallTheme.Base };
        var entry = new CallCard { Width = 392, Height = 452, BackColor = CallTheme.Window };
        Put(entry, DesignLabel("명동 콜링", 18, true, CallTheme.Accent), 28, 26, 332, 40);
        Put(entry, DesignLabel("방에 참가하기", 20, true), 28, 78, 332, 44);
        Put(entry, DesignLabel("닉네임과 방 이름을 입력하세요.", 9, color: CallTheme.Secondary), 28, 128, 332, 28);
        DesignInput(entry, "닉네임", nickname, 168); DesignInput(entry, "방 이름", room, 245);
        join.Text = "방 참가"; join.AutoSize = false; CallTheme.Style(join, primary: true); Put(entry, join, 28, 330, 332, 40);
        join.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        createRoom.AutoSize = false; CallTheme.Style(createRoom); Put(entry, createRoom, 28, 380, 332, 38);
        createRoom.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        createRoom.Click += async (_, _) =>
        { if (callSession is null || callSession.WantsCall || callSession.IsBusy) return; room.Text = "voice-" + Guid.NewGuid().ToString("N")[..8]; joinOperation = ConnectFromUiAsync(); await joinOperation; };
        joinSettings = DesignButton("연결 설정", (_, _) => ToggleDesignedSettings());
        joinSettings.AutoSize = false; joinSettings.BackColor = CallTheme.Base;
        joinHint = DesignLabel("같은 방 이름으로 참가하면 바로 연결됩니다.", 9, color: CallTheme.Secondary);
        designedJoin.Controls.Add(entry); designedJoin.Controls.Add(joinSettings); designedJoin.Controls.Add(joinHint);
        void CenterEntry()
        {
            entry.Width = Math.Min(392, Math.Max(320, designedJoin.ClientSize.Width - 32));
            entry.Left = (designedJoin.ClientSize.Width - entry.Width) / 2;
            entry.Top = Math.Max(16, (designedJoin.ClientSize.Height - 530) / 2);
            joinSettings.SetBounds(entry.Left + (entry.Width - 112) / 2, entry.Bottom + 12, 112, 36);
            joinHint.SetBounds(entry.Left, entry.Bottom + 58, entry.Width, 45);
        }
        designedJoin.Resize += (_, _) => CenterEntry();

        designedCall = new Panel { Dock = DockStyle.Fill, Visible = false, BackColor = CallTheme.Window };
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = CallTheme.Base };
        var context = DesignLabel("현재 통화", 9, true, CallTheme.Accent); Put(header, context, 24, 12, 200, 24);
        roomHeading = DesignLabel("", 19, true); Put(header, roomHeading, 24, 36, 390, 42);
        var copyRoom = DesignButton("방 이름 복사", (_, _) =>
        { try { Clipboard.SetText(room.Text.Trim()); callHint.Text = "방 이름을 복사했어요."; } catch (Exception ex) { AppendLog("복사 실패: " + ex.Message); } });
        copyRoom.SetBounds(425, 34, 116, 38); header.Controls.Add(copyRoom);
        participantCount = DesignLabel("", 10, true, CallTheme.Accent); Put(header, participantCount, 560, 46, 150, 24);
        callState.Dock = DockStyle.None; callState.AutoSize = false; callState.Font = new Font("맑은 고딕", 9, FontStyle.Bold); callState.ForeColor = CallTheme.Accent;
        Put(header, callState, 510, 16, 200, 24);
        void ArrangeHeader()
        {
            var compact = header.Width < 620; context.Visible = !compact;
            var titleWidth = TextRenderer.MeasureText(roomHeading.Text, roomHeading.Font).Width + 4;
            roomHeading.Width = Math.Clamp(titleWidth, 80, Math.Max(80, header.Width - (compact ? 168 : 500)));
            copyRoom.Left = roomHeading.Right + 12;
            callState.AutoEllipsis = true;
            callState.Left = compact ? 24 : copyRoom.Right + 20;
            callState.Top = compact ? 8 : 43;
            callState.Width = compact ? Math.Max(80, header.Width-160) : 180;
            participantCount.Left = compact ? header.Width-100 : callState.Right+16;
            participantCount.Top = compact ? 8 : 43; participantCount.Width = 88;
        }
        header.Resize += (_, _) => ArrangeHeader(); roomHeading.TextChanged += (_, _) => ArrangeHeader();
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(16), BackColor = CallTheme.Base, WrapContents = true };
        muteMicrophone.AutoSize = false; muteMicrophone.Size = new Size(158, 40); muteMicrophone.FlatStyle = FlatStyle.Flat;
        muteMicrophone.FlatAppearance.BorderColor = CallTheme.Border; muteMicrophone.BackColor = CallTheme.Raised;
        var settings = DesignButton("장치 설정", (_, _) => ToggleDesignedSettings());
        var details = DesignButton("연결 상세", (_, _) => ShowDesignedPage(true));
        leave.AutoSize = false; leave.Size = new Size(100, 40); CallTheme.Style(leave, danger: true);
        footer.SizeChanged += (_, _) => { var height = footer.Width < 600 ? 116 : 78; if (footer.Height != height) footer.Height = height; };
        footer.Controls.AddRange([muteMicrophone, settings, details, leave]);
        callHint.Dock = DockStyle.Bottom; callHint.Height = 44; callHint.Padding = new Padding(24, 10, 16, 0); callHint.ForeColor = CallTheme.Secondary; callHint.AutoSize = false;
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24) };
        var title = DesignLabel("참가자", 13, true); title.Dock = DockStyle.Top; title.Height = 42;
        participants.Dock = DockStyle.Fill; participants.FlowDirection = FlowDirection.LeftToRight; participants.WrapContents = true;
        CallNativeUi.Dark(participants); participants.BackColor = CallTheme.Window; participants.Padding = Padding.Empty;
        participants.SizeChanged += (_, _) => ArrangeParticipantCards();
        selfCard = new CallCard { Height = 108, Margin = new Padding(0, 0, 12, 12) };
        selfName = DesignLabel("", 13, true); Put(selfCard, selfName, 20, 20, 280, 32);
        selfState = DesignLabel("", 9, color: CallTheme.Accent); Put(selfCard, selfState, 20, 62, 280, 40);
        waitingCard = new CallCard { Height = 188, Margin = new Padding(0, 0, 12, 12) };
        Put(waitingCard, DesignLabel("친구를 기다리고 있습니다", 15, true), 20, 22, 340, 36);
        waitingHint = DesignLabel("같은 서버와 방 이름으로 참가해 주세요.\n최대 6명까지 함께 통화할 수 있습니다.", 10, color: CallTheme.Secondary);
        Put(waitingCard, waitingHint, 20, 72, 330, 72);
        participants.Controls.AddRange([selfCard, waitingCard]);
        content.Controls.Add(participants); content.Controls.Add(title);
        designedCall.Controls.Add(content); designedCall.Controls.Add(callHint); designedCall.Controls.Add(footer); designedCall.Controls.Add(header);
        designedMain.Controls.Add(designedCall); designedMain.Controls.Add(designedJoin);

        Put(designedSettings, DesignLabel("오디오 및 연결", 9, true, CallTheme.Accent), 24, 20, 280, 25);
        Put(designedSettings, DesignLabel("설정", 20, true), 24, 52, 280, 40);
        DesignInput(designedSettings, "마이크", captureDevice, 116); DesignInput(designedSettings, "출력", renderDevice, 196);
        CallTheme.Apply(captureDevice); CallTheme.Apply(renderDevice);
        refreshAudio.AutoSize = false; CallTheme.Style(refreshAudio); Put(designedSettings, refreshAudio, 28, 278, 270, 38);
        refreshAudio.Click += async (_, _) => { var before = (SelectedCaptureId, SelectedRenderId); RefreshDeviceChoices(before.Item1, before.Item2); if (before != (SelectedCaptureId, SelectedRenderId)) await AudioSelectionChangedAsync(); };
        Put(designedSettings, deviceHint, 28, 326, 278, 56);
        DesignInput(designedSettings, "연결 서버", server, 394);
        DesignInput(designedSettings, "마이크 토글 단축키", hotkeyChoice, 480); CallTheme.Apply(hotkeyChoice);
        Put(designedSettings, hotkeyHint, 28, 548, 278, 54);
        var advanced = DesignButton("진단 및 고급 설정 →", (_, _) => ShowDesignedPage(true));
        Put(designedSettings, advanced, 28, 614, 270, 40);
        var done = DesignButton("완료", (_, _) => ToggleDesignedSettings(false));
        CallTheme.Style(done, primary: true); Put(designedSettings, done, 28, 674, 270, 40);
        designedShell.BringToFront(); CenterEntry(); ResumeLayout(true);
    }
    private void ToggleDesignedSettings(bool? show = null)
    {
        if (designedSettings is null || designedShell is null) return;
        var visible = show ?? !designedSettings.Visible;
        designedSettings.Visible = visible; designedShell.ColumnStyles[1].Width = visible ? 340 : 0;
        if (visible) captureDevice.Focus();
    }
    private void ShowDesignedPage(bool diagnostics)
    {
        if (designedDiagnostics is null || designedShell is null) return;
        designedDiagnostics.Visible = diagnostics; designedShell.Visible = !diagnostics;
        if (diagnostics) { ToggleDesignedSettings(false); designedDiagnostics.BringToFront(); }
        else designedShell.BringToFront();
    }
    private void UpdateDesignedView(CallDiagnostics snapshot, bool locked)
    {
        if (designedJoin is null || designedCall is null) return;
        var peer = snapshot.Peers.FirstOrDefault();
        var values = new[] {
            peer?.Route ?? "대기", peer?.Precision?.RttAgeSeconds is <= 15 ? Number(peer.Precision.RtcpRttMs, " ms") : "미측정",
            Number(peer?.ReceiveJitterMs, " ms"), peer?.Precision?.ReceiveReportAgeSeconds is <= 15 ? Number(peer.Precision.ReceiveLossPercent, "%") : "미측정",
            Number(peer?.Audio.RtpPlayout?.ConcealedMs, " ms"), Number(peer?.Audio.QueueMs, " ms"),
            Number(snapshot.Microphone?.LevelDbFs, " dBFS"), Number(peer?.Audio.LevelDbFs, " dBFS") };
        for (var i = 0; i < values.Length; i++) if (designedMetricValues[i].Text != values[i]) designedMetricValues[i].Text = values[i];
        designedJoin.Visible = !locked; designedCall.Visible = locked;
        if (locked) designedCall.BringToFront(); else designedJoin.BringToFront();
        roomHeading!.Text = room.Text; participantCount!.Text = $"{(snapshot.InCall ? snapshot.Peers.Length + 1 : 0)} / 6명";
        selfName!.Text = nickname.Text + " · 나"; selfState!.Text = muteMicrophone.Checked ? "마이크 꺼짐" : "마이크 켜짐";
        selfState.ForeColor = muteMicrophone.Checked ? CallTheme.Danger : CallTheme.Accent;
        selfCard!.Visible = snapshot.Peers.Length > 0; waitingCard!.Visible = snapshot.Peers.Length == 0;
        if (callSession?.IsBusy == true) waitingHint!.Text = callSession.State;
        else waitingHint!.Text = "같은 서버와 방 이름으로 참가해 주세요.\n최대 6명까지 함께 통화할 수 있습니다.";
        joinHint!.Text = callHint.Text;
        muteMicrophone.Text = muteMicrophone.Checked ? "마이크 켜기" : "마이크 음소거";
        muteMicrophone.ForeColor = muteMicrophone.Checked ? CallTheme.Danger : CallTheme.Text;
    }
    private sealed record ParticipantCardControls(Panel Panel, Label State, TrackBar Volume, CheckBox Muted, Label Amount, Label AudioState)
    {
        internal bool? RemoteMuted { get; set; }
    }
    private static ParticipantCardControls CreateParticipantCard(string name)
    {
        var panel = new CallCard { Height = 108, Width = 400, Margin = new Padding(0, 0, 12, 12) };
        var title = DesignLabel(name, 13, true); Put(panel, title, 76, 16, 220, 32); title.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var state = DesignLabel("연결 중", 9, color: CallTheme.Secondary); Put(panel, state, 76, 49, 220, 24); state.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var audioState = DesignLabel("상대 마이크 상태 미확인", 8, color: CallTheme.Secondary); Put(panel, audioState, 20, 78, 364, 24); audioState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var avatar = DesignLabel(name.Length > 0 ? System.Globalization.StringInfo.GetNextTextElement(name) : "?", 14, true, CallTheme.Accent);
        avatar.AutoEllipsis = false; avatar.BackColor = CallTheme.Raised; avatar.TextAlign = ContentAlignment.MiddleCenter; Put(panel, avatar, 20, 23, 42, 42);
        var volume = new TrackBar { Minimum = 0, Maximum = 200, Value = 100, TickStyle = TickStyle.None, AutoSize = false, BackColor = CallTheme.Surface, AccessibleName = name + " 볼륨" };
        Put(panel, volume, 14, 106, 235, 32); volume.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var amount = DesignLabel("100%", 9, color: CallTheme.Secondary); Put(panel, amount, 270, 110, 70, 30); amount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        var muted = new CallToggle { Text = "내 쪽 음소거", ForeColor = CallTheme.Secondary, AutoSize = true, BackColor = CallTheme.Surface };
        Put(panel, muted, 20, 149, 180, 28);
        volume.Visible = amount.Visible = muted.Visible = false;
        var toggle = DesignButton("볼륨", (_, _) =>
        {
            var expanded = !volume.Visible; volume.Visible = amount.Visible = muted.Visible = expanded;
            panel.Height = expanded ? 190 : 108;
        });
        toggle.AutoSize = false; Put(panel, toggle, 314, 18, 70, 34); toggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        return new(panel, state, volume, muted, amount, audioState);
    }
    private void ArrangeParticipantCards()
    {
        if (participants.IsDisposed) return;
        var width = Math.Max(260, participants.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 14);
        var columns = width >= 620 ? 2 : 1;
        foreach (Control card in participants.Controls)
            card.Width = card == waitingCard ? width : (width - (columns - 1) * 12) / columns;
    }
}
