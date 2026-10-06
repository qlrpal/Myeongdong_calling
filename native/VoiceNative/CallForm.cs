using System.Text;
using System.Text.Json;
using System.Net.NetworkInformation;

namespace VoiceNative;
public sealed class CallForm : Form
{
    private readonly ICallEngine engine = new WebRtcCallEngine();
    private readonly TextBox server = new() { Text = "https://qlrpal.ddns.net/", Dock = DockStyle.Fill };
    private readonly TextBox room = new() { Text = "test", Dock = DockStyle.Fill };
    private readonly TextBox nickname = new() { Text = Environment.UserName, Dock = DockStyle.Fill };
    private readonly Button join = new() { Text = "통화 참가", AutoSize = true };
    private readonly Button leave = new() { Text = "나가기", AutoSize = true, Enabled = false };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
    private readonly TextBox stats = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Queue<string> logLines = new();
    private readonly Queue<CallDiagnostics> history = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource diagnosticsCancellation = new();
    private bool closing, closeRequested;
    private Task? joinOperation, leaveOperation;
    public CallForm()
    {
        Text = "명동 콜링 · 통화 진단"; ClientSize = new Size(840, 760); MinimumSize = new Size(650, 650); Font = new Font("맑은 고딕", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        foreach (var size in new[] { 112, 44, 75 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.Controls.Add(new Label { Text = "연결 서버", AutoSize = true }, 0, 0); fields.Controls.Add(server, 1, 0);
        fields.Controls.Add(new Label { Text = "방 이름", AutoSize = true }, 0, 1); fields.Controls.Add(room, 1, 1);
        fields.Controls.Add(new Label { Text = "닉네임", AutoSize = true }, 0, 2); fields.Controls.Add(nickname, 1, 2); layout.Controls.Add(fields, 0, 0);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var copy = new Button { Text = "진단 복사", AutoSize = true }; var save = new Button { Text = "진단 저장", AutoSize = true };
        controls.Controls.AddRange([join, leave, copy, save]); layout.Controls.Add(controls, 0, 1);
        var diagnostics = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var peerAddress = new TextBox { PlaceholderText = "상대 PC 내부 IP", Width = 220 };
        var udpCheck = new Button { Text = "LAN UDP 검사", AutoSize = true }; var routeCheck = new Button { Text = "경로·ping 검사", AutoSize = true };
        diagnostics.Controls.AddRange([peerAddress, udpCheck, routeCheck, new Label { Text = "수치는 전체 음성 지연이 아닙니다. 송수신량은 Opus 페이로드만 집계합니다.", AutoSize = true }]);
        layout.Controls.Add(diagnostics, 0, 2); layout.Controls.Add(stats, 0, 3); layout.Controls.Add(new Label { Text = "이벤트 로그 (최근 2,000줄)", AutoSize = true }, 0, 4); layout.Controls.Add(log, 0, 5); Controls.Add(layout);
        engine.Status += AppendLog;
        udpCheck.Click += async (_, _) => { udpCheck.Enabled = false; try { await LanUdpProbe.RunAsync(peerAddress.Text.Trim(), AppendLog, diagnosticsCancellation.Token); } catch (Exception ex) { if (!closeRequested) AppendLog("UDP 검사 오류: " + ex.Message); } finally { if (!IsDisposed && !closeRequested) udpCheck.Enabled = true; } };
        routeCheck.Click += async (_, _) => { routeCheck.Enabled = false; try { await NetworkProbe.RunAsync(peerAddress.Text.Trim(), AppendLog, diagnosticsCancellation.Token); } catch (Exception ex) { if (!closeRequested) AppendLog("경로 검사 오류: " + ex.Message); } finally { if (!IsDisposed && !closeRequested) routeCheck.Enabled = true; } };
        join.Click += async (_, _) =>
        {
            join.Enabled = false;
            try { var address = new Uri(server.Text.Trim().TrimEnd('/') + "/"); if (address.Scheme is not ("http" or "https")) throw new InvalidOperationException("HTTP 또는 HTTPS 주소를 입력하세요."); joinOperation = engine.JoinAsync(address, room.Text.Trim(), nickname.Text.Trim()); await joinOperation; if (!closeRequested) leave.Enabled = true; }
            catch (Exception ex) { AppendLog(ex.Message); if (!closeRequested) join.Enabled = true; }
        };
        leave.Click += async (_, _) => { leave.Enabled = false; leaveOperation = engine.LeaveAsync(); try { await leaveOperation; } catch (Exception ex) { AppendLog("종료 오류: " + ex.Message); } if (!closeRequested) join.Enabled = true; };
        copy.Click += (_, _) => { try { Clipboard.SetText(ReportJson()); AppendLog("진단 보고서 복사 완료"); } catch (Exception ex) { AppendLog("복사 실패: " + ex.Message); } };
        save.Click += async (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "진단 JSON|*.json", FileName = $"voice-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var report = ReportJson(); try { await File.WriteAllTextAsync(dialog.FileName, report, Encoding.UTF8); AppendLog("진단 저장 완료: " + dialog.FileName); } catch (Exception ex) { AppendLog("저장 실패: " + ex.Message); }
        };
        timer.Tick += (_, _) => RefreshStats(); timer.Start();
        Shown += (_, _) => { AppendLog("진단 보고서에는 IP·닉네임이 포함됩니다. 음성과 세션 토큰은 저장하지 않습니다."); RefreshStats(); };
        FormClosing += async (_, args) =>
        {
            if (closing) return; args.Cancel = true; if (closeRequested) return;
            closeRequested = true; timer.Stop(); diagnosticsCancellation.Cancel(); join.Enabled = leave.Enabled = false;
            try { if (joinOperation is not null) { try { await joinOperation; } catch { } } if (leaveOperation is not null) { try { await leaveOperation; } catch { } } await engine.DisposeAsync(); }
            finally { timer.Dispose(); closing = true; Close(); }
        };
    }
    private void AppendLog(string message)
    {
        if (IsDisposed || closeRequested || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => AppendLog(message)); } catch (InvalidOperationException) { } return; }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}"; logLines.Enqueue(line);
        if (logLines.Count > 2000) { logLines.Dequeue(); log.Lines = logLines.ToArray(); } else log.AppendText(line + Environment.NewLine);
        if (message.StartsWith("통화 종료")) { join.Enabled = true; leave.Enabled = false; }
    }
    private static string Number(double? value, string unit = "") => value is { } n ? $"{n:F2}{unit}" : "미측정";
    private void RefreshStats()
    {
        try
        {
            var snapshot = engine.GetDiagnostics(); history.Enqueue(snapshot); if (history.Count > 1200) history.Dequeue();
            var text = new StringBuilder(); text.AppendLine($"{snapshot.Timestamp:HH:mm:ss} · {(snapshot.InCall ? "방 참가 중" : "대기")} · Opus 48kHz / 모노 / 20ms 프레임");
            text.AppendLine($"마이크 RMS {Number(snapshot.Microphone?.LevelDbFs, " dBFS")} · 평균 인코딩 {Number(snapshot.Microphone?.ProcessingMs, " ms")}");
            foreach (var peer in snapshot.Peers)
            {
                text.AppendLine(); text.AppendLine($"{peer.Name} · {peer.State} / ICE {peer.IceState} · {peer.Route}");
                text.AppendLine($"선택 후보: {peer.LocalEndpoint ?? "미선정"} → {peer.RemoteEndpoint ?? "미선정"}");
                text.AppendLine($"송신 {Number(peer.TxPayloadKbps, " kbps")} ({peer.TxFrames} 프레임) · 수신 {Number(peer.RxPayloadKbps, " kbps")} ({peer.RxPackets} 패킷)");
                text.AppendLine($"수신 지터 {Number(peer.ReceiveJitterMs, " ms")} · 상대 보고 송신 손실 {Number(peer.RemoteLossPercent, "%")} · 보고 경과 {Number(peer.RemoteReportAgeSeconds, " 초")}");
                text.AppendLine($"재생 큐 {Number(peer.Audio.QueueMs, " ms")} · 초과 큐 정리 {peer.Audio.QueueResets}회 · 버퍼 부족 {peer.Audio.Underruns}회");
                text.AppendLine($"디코딩 {peer.Audio.DecodedFrames}프레임 / {Number(peer.Audio.DecodedMs, " ms")} · 출력 RMS {Number(peer.Audio.LevelDbFs, " dBFS")} · 평균 디코딩 {Number(peer.Audio.ProcessingMs, " ms")}");
                text.AppendLine($"WASAPI 공유/이벤트 재생 · 큐 소비 {Number(peer.Audio.ConsumedMs, " ms")} · 장치 요청 {Number(peer.Audio.RequestedMs, " ms")}");
            }
            text.AppendLine(); text.Append("전체 마이크→상대 출력 지연: 미측정 · 손실은 마지막 RTCP 보고 구간 값"); stats.Text = text.ToString();
        }
        catch (Exception ex) { AppendLog("통계 읽기 오류: " + ex.Message); }
    }
    private string ReportJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, generatedAt = DateTimeOffset.Now, applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(),
        notes = new[] { "Payload bitrate excludes network headers.", "ICMP RTT is not end-to-end audio latency.", "Remote loss is last RTCP interval; inspect age.", "Queue is application PCM queue, not device or network delay." },
        interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(a => new { name = a.Name, status = a.OperationalStatus.ToString(), addresses = a.GetIPProperties().UnicastAddresses.Select(x => x.Address.ToString()).ToArray() }).ToArray(),
        samples = history.ToArray(), events = logLines.ToArray()
    }, new JsonSerializerOptions { WriteIndented = true });
    internal void SavePreview(string path)
    {
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
        Show(); Application.DoEvents(); RefreshStats(); PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Hide();
        timer.Stop(); timer.Dispose(); diagnosticsCancellation.Cancel();
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
