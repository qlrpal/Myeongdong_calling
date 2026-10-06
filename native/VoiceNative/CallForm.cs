namespace VoiceNative;

public sealed class CallForm : Form
{
    private readonly ICallEngine engine = new WebRtcCallEngine();
    private readonly TextBox server = new() { Text = "http://localhost:3000/", Width = 420 };
    private readonly TextBox room = new() { Text = "test", Width = 420 };
    private readonly TextBox nickname = new() { Text = Environment.UserName, Width = 420 };
    private readonly Button join = new() { Text = "통화 참가", AutoSize = true };
    private readonly Button leave = new() { Text = "나가기", AutoSize = true, Enabled = false };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Width = 440, Height = 210 };
    private bool closing;
    private Task? joinOperation;
    public CallForm()
    {
        Text = "명동 콜링 · Native prototype";
        ClientSize = new Size(490, 490);
        Font = new Font("맑은 고딕", 10);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        layout.Controls.AddRange([new Label { Text = "연결 서버", AutoSize = true }, server,
            new Label { Text = "방 이름", AutoSize = true }, room, new Label { Text = "닉네임", AutoSize = true }, nickname]);
        var buttons = new FlowLayoutPanel { Width = 440, Height = 42 };
        buttons.Controls.AddRange([join, leave]); layout.Controls.Add(buttons); layout.Controls.Add(log); Controls.Add(layout);
        engine.Status += message => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); if (message.StartsWith("통화 종료")) { join.Enabled = true; leave.Enabled = false; } }); };
        join.Click += async (_, _) =>
        {
            join.Enabled = false;
            try
            {
                var address = new Uri(server.Text.Trim().TrimEnd('/') + "/");
                if (address.Scheme is not ("http" or "https")) throw new InvalidOperationException("HTTP 또는 HTTPS 서버 주소를 입력하세요.");
                joinOperation = engine.JoinAsync(address, room.Text.Trim(), nickname.Text.Trim());
                await joinOperation; leave.Enabled = true;
            }
            catch (Exception ex) { log.AppendText(ex.Message + "\r\n"); join.Enabled = true; }
        };
        leave.Click += async (_, _) => { leave.Enabled = false; await engine.LeaveAsync(); join.Enabled = true; };
        FormClosing += async (_, args) =>
        {
            if (closing) return;
            args.Cancel = true; join.Enabled = leave.Enabled = false;
            if (joinOperation is not null) { try { await joinOperation; } catch { } }
            await engine.DisposeAsync(); closing = true; Close();
        };
    }
}
