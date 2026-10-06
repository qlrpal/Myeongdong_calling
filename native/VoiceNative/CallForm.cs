using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
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
    private readonly Dictionary<string, (double At, PeerDiagnostics Peer)> previousDiagnostics = new();
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
        var restartOutput = new Button { Text = "출력 재시작", AutoSize = true };
        controls.Controls.AddRange([join, leave, copy, save, restartOutput]); layout.Controls.Add(controls, 0, 1);
        restartOutput.Click += async (_, _) => { restartOutput.Enabled = false; try { await engine.RestartOutputAsync(); } catch (Exception ex) { AppendLog("출력 재시작 오류: " + ex.Message); } finally { if (!IsDisposed) restartOutput.Enabled = true; } };
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
    private static string Fresh(double? value, double? age, string unit) => Number(value, unit) + (age > 15 ? " [오래된 보고]" : "");
    private void RefreshStats()
    {
        try
        {
            var snapshot = engine.GetDiagnostics(); history.Enqueue(snapshot); if (history.Count > 1200) history.Dequeue();
            var text = new StringBuilder(); text.AppendLine($"{snapshot.Timestamp:HH:mm:ss} · {(snapshot.InCall ? "방 참가 중" : "대기")} · Opus 48kHz / 모노 / 20ms 프레임");
            text.AppendLine($"마이크 RMS {Number(snapshot.Microphone?.LevelDbFs, " dBFS")} · 평균 인코딩 {Number(snapshot.Microphone?.ProcessingMs, " ms")}");
            text.AppendLine($"입력 API 지연(참고) {Number(snapshot.Microphone?.DeviceLatencyMs, " ms")} · 인코딩 최근 P95/최대 {Number(snapshot.Microphone?.ProcessingP95Ms)}/{Number(snapshot.Microphone?.ProcessingMaxMs, " ms")} · 마지막 입력 {Number(snapshot.Microphone?.LastFrameAgeMs, " ms 전")}");
            text.AppendLine($"WASAPI 입력 · 입력 콜백 간격 P95/최대 {Number(snapshot.Microphone?.CaptureGapP95Ms)}/{Number(snapshot.Microphone?.CaptureGapMaxMs, " ms")}");
            text.AppendLine($"입력 콜백 전체 처리 P95/최대 {Number(snapshot.Microphone?.CaptureWorkP95Ms)}/{Number(snapshot.Microphone?.CaptureWorkMaxMs, " ms")} · 입력 불연속 {snapshot.Microphone?.CaptureDiscontinuities} / 타임스탬프 오류 {snapshot.Microphone?.CaptureTimestampErrors}");
            text.AppendLine($"송신 대기 {snapshot.Sender?.PendingFrames ?? 0}프레임 · 초과 프레임 버림 {snapshot.Sender?.DroppedFrames ?? 0} · 최대 송신 대기 {Number(snapshot.Sender?.MaxWaitMs, " ms")}");
            foreach (var peer in snapshot.Peers)
            {
                text.AppendLine(); text.AppendLine($"{peer.Name} · {peer.State} / ICE {peer.IceState} · {peer.Route}");
                text.AppendLine($"선택 후보: {peer.LocalEndpoint ?? "미선정"} → {peer.RemoteEndpoint ?? "미선정"}");
                text.AppendLine($"송신 {Number(peer.TxPayloadKbps, " kbps")} ({peer.TxFrames} 프레임) · 수신 {Number(peer.RxPayloadKbps, " kbps")} ({peer.RxPackets} 패킷)");
                text.AppendLine($"수신 지터 {Number(peer.ReceiveJitterMs, " ms")} · 상대 보고 송신 손실 {Fresh(peer.RemoteLossPercent, peer.RemoteReportAgeSeconds, "%")} · 보고 경과 {Number(peer.RemoteReportAgeSeconds, " 초")}");
                var precision = peer.Precision;
                text.AppendLine($"RTCP RTT {Fresh(precision?.RtcpRttMs, precision?.RttAgeSeconds, " ms")} · RTT 경과 {Number(precision?.RttAgeSeconds, " 초")} · 우리 보고 수신 손실 {Fresh(precision?.ReceiveLossPercent, precision?.ReceiveReportAgeSeconds, "%")} / 누적 {precision?.ReceivePacketsLost?.ToString() ?? "미측정"}패킷");
                text.AppendLine($"수신 보고 경과 {Number(precision?.ReceiveReportAgeSeconds, " 초")} · 상대 수신 지터 {Number(precision?.RemoteJitterMs, " ms")} · 마지막 수신 {Number(precision?.LastPacketAgeSeconds, " 초 전")} · 순서 뒤바뀜 {precision?.ReorderedPackets} / 중복 {precision?.DuplicatePackets}");
                text.AppendLine($"최근 패킷 도착 간격 P95/최대 {Number(precision?.ArrivalGapP95Ms)}/{Number(precision?.ArrivalGapMaxMs, " ms")} · 프레임 간격 기준 20ms");
                text.AppendLine($"재생 큐 {Number(peer.Audio.QueueMs, " ms")} · 초과 큐 정리 {peer.Audio.QueueResets}회 · 버퍼 부족 {peer.Audio.Underruns}회");
                text.AppendLine($"부족으로 채운 무음 합계 {Number(peer.Audio.UnderfillMs, " ms")} · 가장 긴 연속 부족 {Number(peer.Audio.MaxUnderfillMs, " ms")}");
                text.AppendLine($"디코딩 {peer.Audio.DecodedFrames}프레임 / {Number(peer.Audio.DecodedMs, " ms")} · 출력 RMS {Number(peer.Audio.LevelDbFs, " dBFS")} · 평균 디코딩 {Number(peer.Audio.ProcessingMs, " ms")}");
                text.AppendLine($"WASAPI 공유/이벤트 재생 · 큐 소비 {Number(peer.Audio.ConsumedMs, " ms")} · 장치 요청 {Number(peer.Audio.RequestedMs, " ms")}");
                text.AppendLine($"출력 장치: {peer.Audio.Device} · {peer.Audio.DeviceFormat}");
                text.AppendLine($"출력 상태 {peer.Audio.PlaybackState} · 재시작 {peer.Audio.DeviceRestarts}회 · 오류 {peer.Audio.DeviceError ?? "없음"}");
                text.AppendLine($"출력 API 지연(참고) {Number(peer.Audio.DeviceLatencyMs, " ms")} · 디코딩 최근 P95/최대 {Number(peer.Audio.ProcessingP95Ms)}/{Number(peer.Audio.ProcessingMaxMs, " ms")}");
                if (previousDiagnostics.TryGetValue(peer.Id, out var before))
                {
                    var seconds = snapshot.MonotonicSeconds - before.At;
                    if (seconds > 0)
                    {
                        var rate = (peer.Audio.ConsumedMs - before.Peer.Audio.ConsumedMs) / seconds;
                        text.AppendLine($"구간 재생 소비 {Number(rate, " ms/s")} · 프레임 정상 속도 기준 약 1000ms/s");
                    }
                    var trims = peer.Audio.QueueResets - before.Peer.Audio.QueueResets;
                    var underruns = peer.Audio.Underruns - before.Peer.Audio.Underruns;
                    if (trims > 0 || underruns > 0) AppendLog($"진단 변화 · {peer.Name}: 초과 큐 정리 +{trims}, 버퍼 부족 +{underruns ?? 0}");
                }
                previousDiagnostics[peer.Id] = (snapshot.MonotonicSeconds, peer);
            }
            foreach (var id in previousDiagnostics.Keys.Except(snapshot.Peers.Select(p => p.Id)).ToArray()) previousDiagnostics.Remove(id);
            text.AppendLine(); text.Append("전체 마이크→상대 출력 지연: 미측정 · 손실은 마지막 RTCP 보고 구간 값"); stats.Text = text.ToString();
        }
        catch (Exception ex) { AppendLog("통계 읽기 오류: " + ex.Message); }
    }
    private string ReportJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 2, generatedAt = DateTimeOffset.Now, applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(),
        notes = new[] { "Payload bitrate excludes network headers.", "ICMP/RTCP RTT is not end-to-end audio latency.", "Remote loss is outbound; local RTCP loss is inbound. Both are last report interval; inspect age.", "Queue is application PCM queue, not device or network delay.", "Device API latency is an implementation estimate, not physical mouth-to-ear latency.", "Processing percentiles cover the most recent 256 frames. Sequence duplicate window is bounded." },
        interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(a => new { name = a.Name, status = a.OperationalStatus.ToString(), addresses = a.GetIPProperties().UnicastAddresses.Select(x => x.Address.ToString()).ToArray() }).ToArray(),
        samples = history.ToArray(), events = logLines.ToArray()
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    internal void SavePreview(string path)
    {
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000);
        Show(); Application.DoEvents(); RefreshStats(); PerformLayout();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), ReportJson(), Encoding.UTF8);
        Hide();
        timer.Stop(); timer.Dispose(); diagnosticsCancellation.Cancel();
        engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
