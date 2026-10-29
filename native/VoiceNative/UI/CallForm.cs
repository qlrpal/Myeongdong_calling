using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Net.NetworkInformation;

namespace VoiceNative;

public sealed partial class CallForm : Form
{
    private static readonly string[] ReportNotes = new[] { "Payload bitrate excludes network headers.", "ICMP/RTCP RTT is not end-to-end audio latency.", "Remote loss is outbound; local RTCP loss is inbound. Both are last report interval; inspect age.", "Queue is application compressed RTP plus remaining decoded PCM in RTP mode, or PCM in legacy mode; excludes device buffering. PLC duration is separate from decoded duration and zero-fill underflow.", "Device API latency is an implementation estimate, not physical mouth-to-ear latency.", "Latency model assumes RTT/2 and the sender frame duration. Device buffering may overlap; codec and physical delays are unverified. Not an end-to-end measurement.", "Processing percentiles cover the most recent 256 frames. Sequence duplicate window is bounded." };
    private readonly ICallEngine engine;
    private readonly TextBox server = new() { Text = "https://qlrpal.ddns.net/", Dock = DockStyle.Fill };
    private readonly TextBox room = new() { Text = "test", Dock = DockStyle.Fill };
    private readonly TextBox nickname = new() { Text = Environment.UserName, Dock = DockStyle.Fill };
    private readonly CheckBox lowLatencyMode = new CallToggle() { Text = "공유 저지연 우선 (검증)", AutoSize = true, Checked = true };
    private readonly ComboBox opusProfile = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 255 };
    private readonly CheckBox rtpMode = new CallToggle() { Text = "RTP 순서 재생·손실 보정 (검증)", AutoSize = true, Checked = true };
    private readonly CheckBox adaptiveMode = new CallToggle() { Text = "지터 적응 20~40ms", AutoSize = true };
    private readonly ComboBox playbackPolicy = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly ComboBox latencyExperiment = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox shortCapture = new CallToggle() { Text = "짧은 공유 입력 주기 우선", AutoSize = true, Checked = true };
    private readonly ComboBox mediaInterface = new CallComboBox() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270 };
    private readonly CheckBox rawOutput = new CallToggle() { Text = "통화 출력 효과 우회 우선", Checked = true, AutoSize = true };
    private readonly Button join = new() { Text = "통화 참가", AutoSize = true };
    private readonly Button leave = new() { Text = "나가기", AutoSize = true, Enabled = false };
    private readonly ReadingTextView log = new("이벤트 로그") { Dock = DockStyle.Fill };
    private readonly ReadingTextView stats = new("상세 진단") { Dock = DockStyle.Fill };
    private readonly Queue<string> logLines = new();
    private readonly Queue<CallDiagnostics> history = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly CancellationTokenSource diagnosticsCancellation = new();
    private bool closing, closeRequested;
    private Task? joinOperation, leaveOperation, localMeasurement;
    private readonly List<LocalLatencyTrial> localTrials = new();
    private readonly Dictionary<string, (double At, PeerDiagnostics Peer)> previousDiagnostics = new();
    public CallForm() : this(null) { }
    public CallForm(string? initialMediaAddress, string? initialExperiment = null, bool sharedOutput = false)
    {
        engine = new WebRtcCallEngine { UseSharedOutput = sharedOutput };
        Text = "명동 콜링"; ClientSize = new Size(900, 780); MinimumSize = new Size(740, 680); Font = new Font("맑은 고딕", 10);
#if BETA_RELEASE
        Text += " · " + typeof(CallForm).Assembly.GetName().Version?.ToString(3) + " 베타";
#endif
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
        foreach (var size in new[] { 112, 150, 75 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, size));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.Controls.Add(new Label { Text = "연결 서버", AutoSize = true }, 0, 0); fields.Controls.Add(server, 1, 0);
        fields.Controls.Add(new Label { Text = "방 이름", AutoSize = true }, 0, 1); fields.Controls.Add(room, 1, 1);
        fields.Controls.Add(new Label { Text = "닉네임", AutoSize = true }, 0, 2); fields.Controls.Add(nickname, 1, 2); layout.Controls.Add(fields, 0, 0);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true };
        opusProfile.Items.AddRange(Enum.GetValues<OpusProfile>().Select(p => (object)OpusAudioEncoder.Label(p)).ToArray());
        opusProfile.SelectedIndex = (int)OpusAudioEncoder.DefaultProfile;
        opusProfile.SelectedIndexChanged += (_, _) =>
        {
            try { engine.SetOpusProfile((OpusProfile)opusProfile.SelectedIndex); }
            catch (Exception ex) { AppendLog("코덱 설정 오류: " + ex.Message); }
        };
        var copy = new Button { Text = "진단 복사", AutoSize = true }; var save = new Button { Text = "진단 저장", AutoSize = true };
        var restartOutput = new Button { Text = "출력 재시작", AutoSize = true };
        controls.Controls.AddRange([join, leave, copy, save, restartOutput]); layout.Controls.Add(controls, 0, 1);
        controls.Controls.Add(opusProfile);
        restartOutput.Click += async (_, _) => { restartOutput.Enabled = false; try { await engine.RestartOutputAsync(); } catch (Exception ex) { AppendLog("출력 재시작 오류: " + ex.Message); } finally { if (!IsDisposed) restartOutput.Enabled = true; } };
        controls.Controls.Add(lowLatencyMode);
        controls.Controls.Add(shortCapture);
        rawOutput.CheckedChanged += (_, _) =>
        {
            try { engine.SetRawOutputPreference(rawOutput.Checked); }
            catch (Exception ex) { AppendLog(ex.Message); }
        };
        controls.Controls.Add(rawOutput);
        mediaInterface.Items.AddRange(MediaInterfaceSelection.Choices().Cast<object>().ToArray());
        mediaInterface.SelectedIndex = 0;
        mediaInterface.SelectedIndexChanged += (_, _) =>
        {
            try { engine.SetMediaInterface((mediaInterface.SelectedItem as MediaInterfaceChoice)?.Address); }
            catch (Exception ex) { AppendLog(ex.Message); }
        };
        controls.Controls.Add(mediaInterface);
        if (initialMediaAddress is not null)
        {
            var selected = MediaInterfaceSelection.Resolve(initialMediaAddress)!;
            mediaInterface.SelectedItem = mediaInterface.Items.Cast<MediaInterfaceChoice>().First(c => c.Address == selected.Address);
        }
        shortCapture.CheckedChanged += (_, _) => { try { engine.SetShortCapturePreference(shortCapture.Checked); } catch (Exception ex) { AppendLog(ex.Message); } };
        controls.Controls.Add(rtpMode);
        playbackPolicy.Items.AddRange(["재생: 저지연 우선", "재생: 기존 방식", "재생: 자동 저지연 (시험)"]);
        playbackPolicy.SelectedIndex = 0;
        engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
        controls.Controls.Add(playbackPolicy);
        latencyExperiment.Items.AddRange(LatencyExperiment.Names.Cast<object>().ToArray());
        latencyExperiment.SelectedIndex = 0;
        latencyExperiment.SelectedIndexChanged += (_, _) =>
        {
            try
            {
                if (latencyExperiment.SelectedIndex > 0) { lowLatencyMode.Checked = rtpMode.Checked = true; playbackPolicy.SelectedIndex = 0; }
                engine.SetLatencyExperiment(LatencyExperiment.FromName(latencyExperiment.SelectedItem!.ToString()!));
            }
            catch (Exception ex) { AppendLog("실험 선택: " + ex.Message); }
        };
        controls.Controls.Add(new Label { Text = "극저지연 실험", AutoSize = true }); controls.Controls.Add(latencyExperiment);
        playbackPolicy.SelectedIndexChanged += (_, _) =>
        {
            try
            {
                engine.SetPlayoutPolicy(playbackPolicy.SelectedIndex switch
                {
                    0 => PlayoutPolicy.LowLatency,
                    2 => PlayoutPolicy.ResponsiveLowLatency,
                    _ => PlayoutPolicy.Compatible
                });
                if (playbackPolicy.SelectedIndex != 1) adaptiveMode.Checked = false;
            }
            catch (Exception ex) { AppendLog(ex.Message); }
        };
        controls.Controls.Add(adaptiveMode);
        adaptiveMode.CheckedChanged += (_, _) => { try { engine.SetAdaptivePlayoutPreference(adaptiveMode.Checked); } catch (Exception ex) { AppendLog(ex.Message); } };
        rtpMode.CheckedChanged += (_, _) => { try { engine.SetRtpPlayoutPreference(rtpMode.Checked); } catch (Exception ex) { AppendLog(ex.Message); } };
        lowLatencyMode.CheckedChanged += (_, _) => { try { engine.SetLowLatencyPreference(lowLatencyMode.Checked); } catch (Exception ex) { AppendLog(ex.Message); } };
        var outputLatency = new CallComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
        outputLatency.Items.AddRange(["일반·복귀 출력 40ms", "일반·복귀 출력 20ms"]);
        outputLatency.SelectedIndex = 0;
        controls.Controls.Add(outputLatency);
        outputLatency.SelectedIndexChanged += async (_, _) =>
        {
            if (localMeasurement is { IsCompleted: false }) { AppendLog("로컬 검사 중 통화 설정 변경은 적용 후 다음 통화에 사용됩니다."); }
            outputLatency.Enabled = false;
            try { await engine.SetOutputLatencyAsync(outputLatency.SelectedIndex == 1 ? 20 : 40); }
            catch (Exception ex) { AppendLog("출력 설정 실패: " + ex.Message + " · 기존 40ms를 선택해 복구하세요."); }
            finally { if (!IsDisposed && !closeRequested) outputLatency.Enabled = true; }
        };
        var acoustic = new Button { Text = "음향 측정 (음성형 신호)", AutoSize = true };
        controls.Controls.Add(acoustic);
        acoustic.Click += async (_, _) =>
        {
            acoustic.Enabled = false;
            try { await engine.MeasureAcousticLatencyAsync(); }
            catch (TimeoutException) { AppendLog("음향 측정 시간 초과 · 상대의 마이크가 상대 스피커 소리를 받을 수 있는지 확인하세요. 한쪽에서만 측정합니다."); }
            catch (Exception ex) { if (!closeRequested) AppendLog("음향 측정 실패: " + ex.Message); }
            finally { if (!IsDisposed && !closeRequested) acoustic.Enabled = true; }
        };
        var loopbackMeasure = new Button { Text = "루프백 지연 측정", AutoSize = true };
        controls.Controls.Add(loopbackMeasure);
        loopbackMeasure.Click += async (_, _) =>
        {
            loopbackMeasure.Enabled = false;
            try { await engine.MeasureLoopbackLatencyAsync(); }
            catch (Exception ex) { if (!closeRequested) AppendLog("루프백 측정 실패: " + ex.Message); }
            finally { if (!IsDisposed && !closeRequested) loopbackMeasure.Enabled = true; }
        };
        var local = new Button { Text = "출력 대기 비교 (20회)", AutoSize = true };
        controls.Controls.Add(local);
        local.Click += async (_, _) =>
        {
            if (engine.GetDiagnostics().InCall || callSession?.WantsCall == true || callSession?.IsBusy == true || joinOperation is { IsCompleted: false })
            { AppendLog("로컬 기준 측정은 통화에서 나간 뒤 실행하세요."); return; }
            local.Enabled = join.Enabled = false;
            try
            {
                localMeasurement = LocalLatencyProbe.CompareAsync(AppendLog, trial =>
                {
                    localTrials.Add(trial);
                    if (localTrials.Count > 100) localTrials.RemoveAt(0);
                }, diagnosticsCancellation.Token);
                await localMeasurement;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!closeRequested) AppendLog("로컬 기준 측정 실패: " + ex.Message); }
            finally { if (!IsDisposed && !closeRequested) local.Enabled = join.Enabled = true; }
        };
        var packetTrace = new Button { Text = "패킷 경로 기록 (30초)", AutoSize = true };
        controls.Controls.Add(packetTrace);
        controls.Controls.SetChildIndex(packetTrace, 2);
        packetTrace.Click += (_, _) =>
        {
            if (!engine.GetDiagnostics().InCall) { AppendLog("통화 중 패킷 경로 기록을 시작하세요."); return; }
            if (engine is not WebRtcCallEngine traceEngine) { AppendLog("이 엔진은 패킷 경로 기록을 지원하지 않습니다."); return; }
            traceEngine.StartPipelineTrace(30);
            AppendLog("패킷 경로 30초 기록 시작 · 음성/페이로드 없이 RTP 식별자·길이·앱 시각만 기록합니다. 양쪽에서 시작한 뒤 진단을 저장하세요. 다시 누르면 이전 기록을 초기화합니다.");
        };
        var diagnostics = new FlowLayoutPanel { Dock = DockStyle.Fill };
#if BETA_RELEASE
        // Keep report export, output restart and interface selection for beta support.
        // Experimental controls and audible measurement tools stay in developer builds.
        foreach (Control control in controls.Controls.Cast<Control>().ToArray())
            if (control != join && control != leave && control != copy && control != save
                && control != restartOutput && control != packetTrace && control != mediaInterface)
                controls.Controls.Remove(control);
#endif
        var peerAddress = new TextBox { PlaceholderText = "상대 PC 내부 IP", Width = 220 };
        var udpCheck = new Button { Text = "LAN UDP 검사", AutoSize = true }; var routeCheck = new Button { Text = "경로·ping 검사", AutoSize = true };
        diagnostics.Controls.AddRange([peerAddress, udpCheck, routeCheck, new Label { Text = "수치는 전체 음성 지연이 아닙니다. 송수신량은 음성 페이로드만 집계합니다.", AutoSize = true }]);
        layout.Controls.Add(diagnostics, 0, 2); layout.Controls.Add(stats, 0, 3); layout.Controls.Add(new Label { Text = "이벤트 로그 (최근 2,000줄)", AutoSize = true }, 0, 4); layout.Controls.Add(log, 0, 5); Controls.Add(layout);
#if BETA_RELEASE
        diagnostics.Controls.Clear();
        diagnostics.Controls.Add(new Label { Text = "수치는 전체 음성 지연이 아닙니다. 문제가 생기면 진단을 저장해 주세요.", AutoSize = true });
        layout.RowStyles[1].Height = 100; // At most two rows at the minimum window width.
        layout.RowStyles[2].Height = 40;
#endif
        engine.Status += AppendLog;
        udpCheck.Click += async (_, _) => { udpCheck.Enabled = false; try { await LanUdpProbe.RunAsync(peerAddress.Text.Trim(), AppendLog, diagnosticsCancellation.Token); } catch (Exception ex) { if (!closeRequested) AppendLog("UDP 검사 오류: " + ex.Message); } finally { if (!IsDisposed && !closeRequested) udpCheck.Enabled = true; } };
        routeCheck.Click += async (_, _) => { routeCheck.Enabled = false; try { await NetworkProbe.RunAsync(peerAddress.Text.Trim(), AppendLog, diagnosticsCancellation.Token); } catch (Exception ex) { if (!closeRequested) AppendLog("경로 검사 오류: " + ex.Message); } finally { if (!IsDisposed && !closeRequested) routeCheck.Enabled = true; } };
        join.Click += async (_, _) =>
        {
            joinOperation = ConnectFromUiAsync(); await joinOperation;
        };
        leave.Click += async (_, _) => { leaveOperation = DisconnectFromUiAsync(); await leaveOperation; };
        copy.Click += (_, _) => { try { Clipboard.SetText(ReportJson()); AppendLog("진단 보고서 복사 완료"); } catch (Exception ex) { AppendLog("복사 실패: " + ex.Message); } };
        save.Click += async (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "진단 JSON|*.json", FileName = $"voice-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var report = ReportJson(); try { await File.WriteAllTextAsync(dialog.FileName, report, Encoding.UTF8); AppendLog("진단 저장 완료: " + dialog.FileName); } catch (Exception ex) { AppendLog("저장 실패: " + ex.Message); }
        };
        timer.Tick += (_, _) => RefreshStats(); timer.Start();
        Shown += (_, _) => { AppendLog("진단과 로컬 로그에는 IP·닉네임이 포함됩니다. 음성과 세션 토큰은 저장하지 않습니다."); RefreshStats(); };
        FormClosing += async (_, args) =>
        {
            if (closing) return; args.Cancel = true; if (closeRequested) return;
            closeRequested = true; timer.Stop(); join.Enabled = leave.Enabled = false;
            microphoneHotkey.Release();
            var cleanup = new CallCleanup();
            try
            {
                cleanup.Run("진단 취소", diagnosticsCancellation.Cancel);
                cleanup.Run("설정 저장", SaveUserSettings);
                if (callSession is not null) await cleanup.RunAsync("통화 제어 종료", () => callSession.DisposeAsync().AsTask());
                if (localMeasurement is not null) await cleanup.RunAsync("로컬 측정 종료", async () =>
                { try { await localMeasurement; } catch (OperationCanceledException) when (diagnosticsCancellation.IsCancellationRequested) { } });
                if (joinOperation is not null) await cleanup.RunAsync("진행 참가 종료", () => joinOperation);
                if (leaveOperation is not null) await cleanup.RunAsync("진행 퇴장 종료", () => leaveOperation);
                await cleanup.RunAsync("통화 엔진 종료", () => engine.DisposeAsync().AsTask());
                cleanup.Run("진단 취소 해제", diagnosticsCancellation.Dispose);
                cleanup.ThrowIfFailed();
            }
            catch (Exception ex) { AppLog.Write("종료 오류: " + ex.Message); }
            finally { timer.Dispose(); closing = true; Close(); }
        };
        BuildProductView(layout);
        if (initialExperiment is not null)
        {
            _ = LatencyExperiment.FromName(initialExperiment);
            latencyExperiment.SelectedItem = initialExperiment == "baseline" ? "기본 모드" : initialExperiment;
        }
    }
    private void AppendLog(string message)
    {
        if (IsDisposed || closeRequested || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => AppendLog(message)); } catch (InvalidOperationException) { } return; }
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}"; logLines.Enqueue(line);
        AppLog.Write(message);
        if (logLines.Count > 2000)
        {
            var removed = (logLines.Dequeue() + Environment.NewLine).ReplaceLineEndings("\n");
            log.ReplaceReadingText(string.Join(Environment.NewLine, logLines) + Environment.NewLine, followTail: true, removedLines: removed.Count(c => c == '\n'), removedCharacters: removed.Length);
        }
        else log.AppendReadingText(line + Environment.NewLine);
        if (message.StartsWith("통화 종료", StringComparison.Ordinal)) { join.Enabled = true; leave.Enabled = false; }
    }
    private static string Number(double? value, string unit = "") => value is { } n ? $"{n:F2}{unit}" : "미측정";
    private static string Fresh(double? value, double? age, string unit) => Number(value, unit) + (age > 15 ? " [오래된 보고]" : "");
    private void RefreshStats()
    {
        try
        {
            var snapshot = engine.GetDiagnostics();
            UpdateProductView(snapshot);
            lowLatencyMode.Enabled = opusProfile.Enabled = rtpMode.Enabled = !snapshot.InCall && join.Enabled; history.Enqueue(snapshot); if (history.Count > 1200) history.Dequeue();
            playbackPolicy.Enabled = !snapshot.InCall && join.Enabled && rtpMode.Checked;
            shortCapture.Enabled = !snapshot.InCall && join.Enabled && lowLatencyMode.Checked;
            rawOutput.Enabled = !snapshot.InCall && join.Enabled && lowLatencyMode.Checked;
            mediaInterface.Enabled = !snapshot.InCall && join.Enabled;
            adaptiveMode.Enabled = !snapshot.InCall && join.Enabled && rtpMode.Checked && playbackPolicy.SelectedIndex == 1;
            latencyExperiment.Enabled = !snapshot.InCall && join.Enabled;
            if (latencyExperiment.SelectedIndex > 0) opusProfile.Enabled = playbackPolicy.Enabled = adaptiveMode.Enabled = lowLatencyMode.Enabled = rtpMode.Enabled = false;
            var text = new StringBuilder(); text.AppendLine(CultureInfo.CurrentCulture, $"{snapshot.Timestamp:HH:mm:ss} · {(snapshot.InCall ? "방 참가 중" : "대기")} · {(snapshot.Experiment?.Pcm == true ? "L16 PCM" : "Opus")} 48kHz / 모노 / {snapshot.FrameMs}ms 프레임");
            if (snapshot.CleanupError is { } cleanupFailure) text.AppendLine("정리 경고 · " + cleanupFailure);
            if (snapshot.SharedOutput is { } shared)
                text.AppendLine(CultureInfo.CurrentCulture, $"공통 출력 (시험) · 출력 스트림 {shared.OutputStreams}개 · 활성 상대 {shared.ActiveSources}/{shared.Sources} · 믹싱 포화 {shared.ClippedSamples}샘플");
            if (snapshot.Experiment is { } experiment) text.AppendLine(CultureInfo.CurrentCulture, $"극저지연 실험 · 여유 하한 {experiment.ReserveMs}ms · 기본 빈 큐 보간 {experiment.EmptyHoldMs}ms · 전체 연속 PLC 상한 {experiment.PlcLimitMs}ms · 출력 안전 여유 {experiment.OutputHeadroomMs}ms · 최신 송신 {experiment.LatestSend} · 최소 출력 {experiment.MinimalOutput} · PCM 손실은 무음 채움");
            text.AppendLine(snapshot.PlaybackPolicy switch
            {
                "ResponsiveLowLatency" => "재생 정책: 자동 저지연 (시험)",
                "LowLatency" => "재생 정책: 저지연 우선",
                _ => "재생 정책: 기존 방식"
            });
            if (snapshot.Encoder is { } codec)
                text.AppendLine(CultureInfo.CurrentCulture, $"송신 코덱 {codec.Profile} · {codec.Application} · 목표 {codec.TargetBitrate?.ToString(CultureInfo.CurrentCulture) ?? "기존 기본값"} bps · 코덱 선행 대기 {Number(codec.LookaheadMs, " ms")} · {codec.Backend}");
            text.AppendLine(CultureInfo.CurrentCulture, $"마이크 RMS {Number(snapshot.Microphone?.LevelDbFs, " dBFS")} · 평균 인코딩 {Number(snapshot.Microphone?.ProcessingMs, " ms")}");
            text.AppendLine(CultureInfo.CurrentCulture, $"입력 API 지연(참고) {Number(snapshot.Microphone?.DeviceLatencyMs, " ms")} · 인코딩 최근 P95/최대 {Number(snapshot.Microphone?.ProcessingP95Ms)}/{Number(snapshot.Microphone?.ProcessingMaxMs, " ms")} · 마지막 입력 {Number(snapshot.Microphone?.LastFrameAgeMs, " ms 전")}");
            text.AppendLine(CultureInfo.CurrentCulture, $"미디어 네트워크: {snapshot.MediaAddress ?? "OS 자동 (VPN 경로 유지)"}");
            text.AppendLine(CultureInfo.CurrentCulture, $"WASAPI 입력 · 입력 콜백 간격 P95/최대 {Number(snapshot.Microphone?.CaptureGapP95Ms)}/{Number(snapshot.Microphone?.CaptureGapMaxMs, " ms")}");
            text.AppendLine(CultureInfo.CurrentCulture, $"입력 콜백 전체 처리 P95/최대 {Number(snapshot.Microphone?.CaptureWorkP95Ms)}/{Number(snapshot.Microphone?.CaptureWorkMaxMs, " ms")} · 입력 불연속 {snapshot.Microphone?.CaptureDiscontinuities} / 타임스탬프 오류 {snapshot.Microphone?.CaptureTimestampErrors}");
            if (snapshot.Microphone?.CaptureExecution is { } inputRun)
                text.AppendLine(CultureInfo.CurrentCulture, $"입력 실행 · 버퍼 조기 반환 {inputRun.StagedBuffers} · 최대 장치 버퍼 보유 {Number(inputRun.MaxLeaseMs, " ms")} / 콜백 처리 {Number(inputRun.MaxCallbackMs, " ms")} · 최대 기상 간격 {Number(inputRun.MaxWakeGapMs, " ms")} · 대기 시간 초과 {inputRun.TimeoutWakes}회 · 오디오 우선순위 {inputRun.MmcssActive}");
            text.AppendLine(CultureInfo.CurrentCulture, $"송신 대기 {snapshot.Sender?.PendingFrames ?? 0}프레임 · 초과 프레임 버림 {snapshot.Sender?.DroppedFrames ?? 0} · 최대 송신 대기 {Number(snapshot.Sender?.MaxWaitMs, " ms")}");
            if (snapshot.FrameAdaptation is { } frames)
                text.AppendLine(CultureInfo.CurrentCulture, $"프레임 자동 조절 · 현재/요청 {frames.CurrentMs}/{frames.RequestedMs}ms · 피드백 준비 {frames.AllPeersReady} · 전환 {frames.Changes}회 · {frames.Reason}");
            text.AppendLine(CultureInfo.CurrentCulture, $"입력 공유 저지연: {snapshot.Microphone?.LowLatencyActive} · 주기 {Number(snapshot.Microphone?.EnginePeriodMs, " ms")} · 복귀 사유 {snapshot.Microphone?.LowLatencyUnavailableReason ?? "없음"}");
            if (snapshot.Acoustic is { } measured)
                text.AppendLine(CultureInfo.CurrentCulture, $"최근 음향 경로 측정 [{measured.Timestamp:HH:mm:ss}] 내→{measured.Peer}: {measured.MinimumMs:F1}~{measured.MaximumMs:F1}ms · 경로 {measured.MeasurementPath} · 검출 ±{measured.DetectionResolutionMs:F0}ms");
            foreach (var peer in snapshot.Peers)
            {
                text.AppendLine(); text.AppendLine(CultureInfo.CurrentCulture, $"{peer.Name} · {peer.State} / ICE {peer.IceState} · {peer.Route}");
                text.AppendLine(CultureInfo.CurrentCulture, $"선택 후보: {peer.LocalEndpoint ?? "미선정"} → {peer.RemoteEndpoint ?? "미선정"}");
                text.AppendLine(CultureInfo.CurrentCulture, $"후보 전송 {peer.CandidateTransport ?? "미선정"} · 주소 계열 {peer.LocalAddressFamily ?? "?"} → {peer.RemoteAddressFamily ?? "?"} · 수집 {string.Join(", ", peer.CollectedAddressFamilies ?? [])} · TURN 클라이언트 구간 전송은 별도 검증 필요");
                text.AppendLine(CultureInfo.CurrentCulture, $"지연 합산 모델(실측 아님): 내→상대 {Number(peer.Latency?.OutboundModelMs, " ms")} · 상대→내 {Number(peer.Latency?.InboundModelMs, " ms")} · 상대 보고 경과 {Number(peer.Latency?.RemoteAgeSeconds, " 초")}");
                text.AppendLine(CultureInfo.CurrentCulture, $"상대 입력 API {Number(peer.Latency?.Remote?.InputApiMs, " ms")} · 상대 송신 대기 {Number(peer.Latency?.Remote?.SendWaitMs, " ms")} · 상대 재생 큐 {Number(peer.Latency?.Remote?.PlaybackQueueMs, " ms")} · 상대 출력 API {Number(peer.Latency?.Remote?.OutputApiMs, " ms")}");
                text.AppendLine(CultureInfo.CurrentCulture, $"송신 {Number(peer.TxPayloadKbps, " kbps")} ({peer.TxFrames} 프레임) · 수신 {Number(peer.RxPayloadKbps, " kbps")} ({peer.RxPackets} 패킷)");
                text.AppendLine(CultureInfo.CurrentCulture, $"수신 지터 {Number(peer.ReceiveJitterMs, " ms")} · 상대 보고 송신 손실 {Fresh(peer.RemoteLossPercent, peer.RemoteReportAgeSeconds, "%")} · 보고 경과 {Number(peer.RemoteReportAgeSeconds, " 초")}");
                var precision = peer.Precision;
                text.AppendLine(CultureInfo.CurrentCulture, $"RTCP RTT {Fresh(precision?.RtcpRttMs, precision?.RttAgeSeconds, " ms")} · RTT 경과 {Number(precision?.RttAgeSeconds, " 초")} · 우리 보고 수신 손실 {Fresh(precision?.ReceiveLossPercent, precision?.ReceiveReportAgeSeconds, "%")} / 보고값 {precision?.ReceivePacketsLost?.ToString(CultureInfo.CurrentCulture) ?? "미측정"}패킷");
                text.AppendLine(CultureInfo.CurrentCulture, $"수신 보고 경과 {Number(precision?.ReceiveReportAgeSeconds, " 초")} · 상대 수신 지터 {Number(precision?.RemoteJitterMs, " ms")} · 마지막 수신 {Number(precision?.LastPacketAgeSeconds, " 초 전")} · 순서 뒤바뀜 {precision?.ReorderedPackets} / 중복 {precision?.DuplicatePackets}");
                text.AppendLine(CultureInfo.CurrentCulture, $"최근 패킷 도착 간격 P95/최대 {Number(precision?.ArrivalGapP95Ms)}/{Number(precision?.ArrivalGapMaxMs, " ms")} · 송신 프레임 {snapshot.FrameMs}ms · 수신은 상대 설정에 따름");
                text.AppendLine(CultureInfo.CurrentCulture, $"재생 큐 {Number(peer.Audio.QueueMs, " ms")} · 초과 큐 정리 {peer.Audio.QueueResets}회 · 버퍼 부족 {peer.Audio.Underruns}회");
                if (peer.Audio.RtpPlayout is { } rtp)
                {
                    text.AppendLine(CultureInfo.CurrentCulture, $"RTP 재생 · 목표 여유 {rtp.TargetReserveMs}ms · 수신 시간 단위 {rtp.PacketQuantumMs}ms · 대기 {rtp.BufferedPackets}패킷 · PLC {rtp.PlcFrames}회 / {rtp.ConcealedMs:F1}ms");
                    text.AppendLine(CultureInfo.CurrentCulture, $"적응 여유 {(rtp.AdaptiveReserve ? "켜짐" : "꺼짐")} · 도착 간격 초과 P95 {Number(rtp.ArrivalJitterP95Ms, " ms")} · PLC 중 대기 삽입 {rtp.HeldMs:F1}ms · 최대 연속 PLC {rtp.MaxConsecutivePlcMs:F1}ms");
                    text.AppendLine(CultureInfo.CurrentCulture, $"여유 정책: {rtp.ReserveLearningState switch { "Learning" => "초기/재학습 중", "Holding" => "학습값 유지", "Disabled" => "고정", "ContinuousFallback" => "안전 복귀: 연속 자동", _ => "연속 자동" }} · 재학습 {rtp.ReserveRelearns}회 · 큐는 장치 버퍼 제외, RTT는 왕복 네트워크 값");
                    text.AppendLine(CultureInfo.CurrentCulture, $"소비율 보정 {rtp.RateCorrectionPpm:+0;-0;0}ppm · 출력 요청 P95/최대 {Number(rtp.ReadRequestP95Ms, " ms")}/{Number(rtp.ReadRequestMaxMs, " ms")} · 읽기 직후 큐 {Number(rtp.QueueAfterReadMs, " ms")}");
                    text.AppendLine(CultureInfo.CurrentCulture, $"지연 도착 {rtp.LatePackets} · 중복 {rtp.DuplicatePackets} · 잘못된 패킷 {rtp.InvalidPackets} · 재동기화 {rtp.Resyncs} · 재버퍼링 {rtp.Rebuffers}");
                    if (rtp.RecoveryDiscardedPackets > 0) text.AppendLine(CultureInfo.CurrentCulture, $"지연 회복을 위해 생략한 음성 {rtp.RecoveryDiscardedMs:F1}ms / {rtp.RecoveryDiscardedPackets}패킷");
                    if (rtp.RecoveryReserveMs is { } recoveryReserve) text.AppendLine(CultureInfo.CurrentCulture, $"반복 공백 빠른 재시작 {rtp.ShortReserveRestarts}회 · 재개 여유 {recoveryReserve}ms (정상 목표와 별도)");
                }
                text.AppendLine(CultureInfo.CurrentCulture, $"부족으로 채운 무음 합계 {Number(peer.Audio.UnderfillMs, " ms")} · 가장 긴 연속 부족 {Number(peer.Audio.MaxUnderfillMs, " ms")}");
                text.AppendLine(CultureInfo.CurrentCulture, $"디코딩 {peer.Audio.DecodedFrames}프레임 / {Number(peer.Audio.DecodedMs, " ms")} · 출력 RMS {Number(peer.Audio.LevelDbFs, " dBFS")} · 평균 디코딩 {Number(peer.Audio.ProcessingMs, " ms")}");
                text.AppendLine(CultureInfo.CurrentCulture, $"WASAPI 공유/이벤트 재생 · 큐 소비 {Number(peer.Audio.ConsumedMs, " ms")} · 장치 요청 {Number(peer.Audio.RequestedMs, " ms")}");
                text.AppendLine(CultureInfo.CurrentCulture, $"출력 장치: {peer.Audio.Device} · {peer.Audio.DeviceFormat}");
                text.AppendLine(CultureInfo.CurrentCulture, $"출력 상태 {peer.Audio.PlaybackState} · 재시작 {peer.Audio.DeviceRestarts}회 · 오류 {peer.Audio.DeviceError ?? "없음"}");
                if (peer.Audio.Render is { } render)
                {
                    text.AppendLine(CultureInfo.CurrentCulture, $"출력 버퍼 · 제한 공급 {render.BoundedActive} · 목표 {Number(render.TargetPaddingMs, " ms")} / 용량 {Number(render.CapacityMs, " ms")} · 장치 버퍼 소진 관측 {render.EmptyEvents}회 · 안전 복귀 {render.SafetyFallbacks}회 · 출력 효과 우회 {render.RawActive}");
                    if (render.Execution is { } outputRun)
                    {
                        text.AppendLine(CultureInfo.CurrentCulture, $"출력 실행 · 버퍼 단기 보유 {outputRun.StagedBuffers} · 재생 중 최대 공급 처리 {Number(outputRun.MaxRunningProviderMs, " ms")} / 장치 버퍼 보유 {Number(outputRun.MaxLeaseMs, " ms")} · 최대 기상 간격 {Number(outputRun.MaxWakeGapMs, " ms")} · 기상 시/공급 중 소진 {outputRun.EmptyOnWake}/{outputRun.EmptyDuringSupply}회 · 대기 시간 초과 {outputRun.TimeoutWakes}회 · 오디오 우선순위 {outputRun.MmcssActive}");
                        if (outputRun.PrimingWaitMs is { } primeWait)
                            text.AppendLine(CultureInfo.CurrentCulture, $"출력 시작 준비 · 유효 미디어 확보 {outputRun.PrimedWithMedia} · 대기 {Number(primeWait, " ms")} · 시작 전 준비이며 고정 재생 여유와 별개");
                    }
                }
                text.AppendLine(CultureInfo.CurrentCulture, $"출력 공유 저지연: {peer.Audio.LowLatencyActive} · 주기 {Number(peer.Audio.EnginePeriodMs, " ms")} · 복귀 사유 {peer.Audio.LowLatencyUnavailableReason ?? "없음"}");
                text.AppendLine(CultureInfo.CurrentCulture, $"출력 요청 {peer.Audio.RequestedOutputMs}ms · 출력 API 지연(참고) {Number(peer.Audio.DeviceLatencyMs, " ms")} · 디코딩 최근 P95/최대 {Number(peer.Audio.ProcessingP95Ms)}/{Number(peer.Audio.ProcessingMaxMs, " ms")}");
                if (previousDiagnostics.TryGetValue(peer.Id, out var before))
                {
                    var seconds = snapshot.MonotonicSeconds - before.At;
                    if (seconds > 0)
                    {
                        var rate = (peer.Audio.ConsumedMs - before.Peer.Audio.ConsumedMs) / seconds;
                        text.AppendLine(CultureInfo.CurrentCulture, $"구간 재생 소비 {Number(rate, " ms/s")} · 기준 약 1000ms/s · 소비율 보정 중 달라질 수 있음");
                    }
                    var trims = peer.Audio.QueueResets - before.Peer.Audio.QueueResets;
                    var underruns = peer.Audio.Underruns - before.Peer.Audio.Underruns;
                    if (trims > 0 || underruns > 0) AppendLog($"진단 변화 · {peer.Name}: 초과 큐 정리 +{trims}, 버퍼 부족 +{underruns ?? 0}");
                }
                previousDiagnostics[peer.Id] = (snapshot.MonotonicSeconds, peer);
            }
            foreach (var id in previousDiagnostics.Keys.Except(snapshot.Peers.Select(p => p.Id)).ToArray()) previousDiagnostics.Remove(id);
            text.AppendLine(); text.Append("전체 마이크→상대 출력 지연: 미측정 · 손실은 마지막 RTCP 보고 구간 값"); stats.ReplaceReadingText(text.ToString());
        }
        catch (Exception ex) { AppendLog("통계 읽기 오류: " + ex.Message); }
    }
    private string ReportJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        generatedAt = DateTimeOffset.Now,
        applicationVersion = typeof(CallForm).Assembly.GetName().Version?.ToString(),
        sdkBuild = SdkBuildIdentity.Read(),
        opusBackend = OpusBackend.Snapshot(),
        os = Environment.OSVersion.ToString(),
        notes = ReportNotes,
        interfaces = NetworkInterface.GetAllNetworkInterfaces().Select(a => new { name = a.Name, status = a.OperationalStatus.ToString(), addresses = a.GetIPProperties().UnicastAddresses.Select(x => x.Address.ToString()).ToArray() }).ToArray(),
        localLatencyTrials = localTrials.ToArray(),
        measurementLimits = "로컬: 출력 제출→스피커→마이크 캡처. 통화: 송신 캡처 프레임 시각→상대 마이크 재캡처. 서로 출발점이 달라 단순 차감 불가. ADC/DAC·드라이버·DSP·공기 경로는 합산값이며 장치 단독으로 분리되지 않음. 발화·음악은 오검출 가능. 동일 장치·볼륨·노이즈 제거 조건으로 반복 비교 필요.",
        samples = history.ToArray(),
        pipeline = (engine as WebRtcCallEngine)?.SnapshotPipelineTrace(),
        events = logLines.ToArray()
    }, VoiceNative.DiagnosticJson.Readable);

}
