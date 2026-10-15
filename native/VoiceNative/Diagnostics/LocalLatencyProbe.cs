using NAudio.Wave;
using SIPSorcery.Media;

namespace VoiceNative;

public sealed record LocalLatencyTrial(DateTimeOffset Timestamp, double DelayMs, double DetectionMarginMs,
    string OutputDevice, string OutputFormat, double OutputApiMs, double? InputApiMs, string InputDevice, int RequestedOutputMs, string RunId, int TrialNumber);

// Measures output submission -> acoustic microphone capture, using one monotonic clock.
// No Opus, network, call queue or remote clock correction is involved.
internal static class LocalLatencyProbe
{
    private sealed class SignalProvider : IWaveProvider
    {
        private readonly object sync = new();
        private byte[]? signal;
        private int position;
        private TaskCompletionSource<double>? submitted;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public Task<double> Trigger()
        {
            var generator = new AcousticCaptureProbe();
            _ = generator.Inject();
            var bytes = new byte[19200 * 2];
            for (var frame = 0; frame < 20; frame++)
            {
                var pcm = new short[960]; generator.Process(pcm, ProbeClock.Now);
                Buffer.BlockCopy(pcm, 0, bytes, frame * 1920, 1920);
            }
            lock (sync)
            {
                signal = bytes; position = 0;
                submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return submitted.Task;
            }
        }
        public int Read(Span<byte> destination)
        {
            destination.Clear();
            lock (sync)
            {
                if (signal is null) return destination.Length;
                if (position == 0) submitted!.TrySetResult(ProbeClock.Now);
                var count = Math.Min(destination.Length, signal.Length - position);
                signal.AsSpan(position, count).CopyTo(destination); position += count;
                if (position == signal.Length) signal = null;
                return destination.Length;
            }
        }
    }

    public static async Task RunAsync(Action<string> status, Action<LocalLatencyTrial> result, CancellationToken token, int requestedOutputMs = 40)
    {
        if (requestedOutputMs is not (20 or 30 or 40)) throw new ArgumentOutOfRangeException(nameof(requestedOutputMs));
        var runId = Guid.NewGuid().ToString("N");
        using var encoder = new AudioEncoder(includeOpus: true);
        var capture = new WindowsAudioDevice(encoder, disableSink: true);
        var provider = new SignalProvider();
        WasapiPlayer? player = null;
        var failure = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.OnAudioSourceError += error => failure.TrySetException(new InvalidOperationException(error));
        try
        {
            player = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(requestedOutputMs).Build();
            player.PlaybackStopped += (_, args) => { if (args.Exception is not null) failure.TrySetException(args.Exception); };
            player.Init(provider);
            status($"로컬 기준 측정 · 요청 {requestedOutputMs}ms · 자신의 스피커를 자신의 마이크가 듣도록 하고 조용히 유지하세요. 5회 신호를 재생합니다.");
            await capture.StartAudio(); player.Play();
            status($"로컬 장치 · 입력 {capture.Snapshot().Device} · 출력 {player.DeviceFriendlyName} · {player.OutputWaveFormat}");
            await Task.Delay(1500, token);
            for (var trial = 1; trial <= 5; trial++)
            {
                var detected = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                capture.Acoustic.Arm(Guid.NewGuid().ToString("N"), stamp => detected.TrySetResult(stamp));
                try
                {
                    var submitted = await provider.Trigger().WaitAsync(TimeSpan.FromSeconds(2), token);
                    var completed = await Task.WhenAny(detected.Task, failure.Task).WaitAsync(TimeSpan.FromSeconds(6), token);
                    var delay = (await completed - submitted) * 1000;
                    if (!double.IsFinite(delay) || delay < 0 || delay > 5000)
                        throw new InvalidOperationException("유효하지 않은 검출 시각입니다. 주변 발화·음악을 멈추고 다시 검사하세요.");
                    var snapshot = capture.Snapshot();
                    var entry = new LocalLatencyTrial(DateTimeOffset.Now, delay, 20, player.DeviceFriendlyName,
                        player.OutputWaveFormat.ToString(), player.CurrentLatency.TotalMilliseconds, snapshot.DeviceLatencyMs, snapshot.Device ?? "미확인", requestedOutputMs, runId, trial);
                    result(entry);
                    status($"로컬 기준 {trial}/5: {delay:F1}ms · 검출 여유 ±20ms · 출력 제출→마이크 재캡처, 장치 단독 지연 아님");
                }
                catch (TimeoutException) { status($"로컬 기준 {trial}/5: 검출 실패 · 스피커 볼륨·마이크 경로·노이즈 제거를 확인하세요."); }
                finally { capture.Acoustic.Cancel(); }
                await Task.Delay(700, token);
            }
        }
        finally { try { player?.Stop(); } finally { player?.Dispose(); await capture.Close(); } }
    }
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("측정값이 없습니다.");
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    public static async Task CompareAsync(Action<string> status, Action<LocalLatencyTrial> result, CancellationToken token)
    {
        status("출력 대기 비교 시작 · 40→30→20→40ms, 각 5회. 마지막 40ms는 환경 변화 확인용입니다. 통화 설정은 변경하지 않습니다.");
        foreach (var requested in new[] { 40, 30, 20, 40 })
        {
            token.ThrowIfCancellationRequested();
            var entries = new List<LocalLatencyTrial>();
            try
            {
                await RunAsync(status, entry => { entries.Add(entry); result(entry); }, token, requested);
                if (entries.Count == 0) status($"요청 {requested}ms: 성공 0/5 · 비교 불가");
                else status($"요청 {requested}ms: 성공 {entries.Count}/5 · 실제 API {Median(entries.Select(x => x.OutputApiMs)):F1}ms · 음향 중앙값 {Median(entries.Select(x => x.DelayMs)):F1}ms · 최소/최대 {entries.Min(x => x.DelayMs):F1}/{entries.Max(x => x.DelayMs):F1}ms · 검출 여유 ±20ms");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { status($"요청 {requested}ms 검사 실패: {ex.Message} · 다음 설정으로 진행합니다."); }
        }
        status("출력 비교 완료 · 요청값과 실제 API 지연은 다를 수 있습니다. 이 검사는 지속 통화의 끊김 안정성을 검증하지 않습니다. 진단 JSON을 저장하세요.");
    }

}
