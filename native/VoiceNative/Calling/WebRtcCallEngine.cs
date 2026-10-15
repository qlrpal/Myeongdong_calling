using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;

namespace VoiceNative;

public sealed class WebRtcCallEngine : ICallEngine
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient streamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim cleanupGate = new(1);
    private readonly Dictionary<string, Peer> peers = new();
    private readonly List<RTCIceServer> iceServers = new();
    private CancellationTokenSource? cancellation;
    private Task? eventLoop;
    private Uri? server;
    private Session? session;
    private WindowsAudioDevice? microphone;
    private OpusAudioEncoder? encoder;
    private OpusProfile opusProfile = OpusProfile.Baseline;
    private AudioSendQueue? sendQueue;
    private CancellationTokenSource? sendCancellation;
    private Task? sendTask;
    private readonly SemaphoreSlim measurementGate = new(1);
    private AcousticLatencyResult? acousticResult;
    private int outputLatencyMs = 40;
    private bool preferLowLatency = true;
    private LoopbackLatencyCapture? loopback;
    private sealed record ProbeReply(ProbeMessage Message, double ReceivedAt);
    public event Action<string>? Status;
    private sealed record Session(string Id, string Token, RemotePeer[] Peers);
    private sealed record RemotePeer(string Id, string Name);
    private sealed record Peer(RTCPeerConnection Connection, WindowsAudioDevice Sink, AudioEncoder Decoder, string Name, PeerMetrics Metrics, RtcpMetrics Precision)
    {
        public long LastSentSequence { get; set; }
        public long FirstSequence { get; set; }
        public uint TimestampBase { get; set; }
        public int PayloadTypeId { get; set; } = 111;
        public RTCDataChannel? DiagnosticsChannel { get; set; }
        public TelemetryState Telemetry { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<ProbeReply>> ProbeWaiters { get; } = new();
    }

    private Uri Endpoint(string path) => new(server!, path);
    private async Task<JsonElement> Post(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(Endpoint(path), body, Json);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(result.TryGetProperty("error", out var error) ? error.GetString() : response.ReasonPhrase);
        return result;
    }
    private Task Send(string to, object signal) => session is null ? Task.CompletedTask :
        Post("api/signal", new { session.Id, session.Token, to, signal });

    public async Task JoinAsync(Uri address, string room, string name)
    {
        if (session is not null) throw new InvalidOperationException("이미 통화 중입니다.");
        if (NAudio.Wave.WaveIn.DeviceCount == 0 || NAudio.Wave.WaveOut.DeviceCount == 0)
            throw new InvalidOperationException("마이크와 출력 장치를 확인하세요.");
        server = address;
        try
        {
            var config = await http.GetFromJsonAsync<JsonElement>(Endpoint("api/config"));
            iceServers.Clear();
            foreach (var item in config.GetProperty("iceServers").EnumerateArray())
            {
                var urls = item.GetProperty("urls");
                var entries = urls.ValueKind == JsonValueKind.Array ? urls.EnumerateArray().Select(x => x.GetString()!) : [urls.GetString()!];
                foreach (var url in entries) iceServers.Add(new RTCIceServer
                {
                    urls = url,
                    username = item.TryGetProperty("username", out var u) ? u.GetString() : null,
                    credential = item.TryGetProperty("credential", out var p) ? p.GetString() : null
                });
            }
            Status?.Invoke($"ICE 서버 {iceServers.Count}개 · TURN 중계 " +
                (iceServers.Any(s => s.urls.StartsWith("turn:", StringComparison.Ordinal) || s.urls.StartsWith("turns:", StringComparison.Ordinal)) ? "사용 가능" : "미설정"));
            encoder = new OpusAudioEncoder(opusProfile);
            Status?.Invoke("송신 코덱 · " + OpusAudioEncoder.Label(opusProfile));
            microphone = new WindowsAudioDevice(encoder, disableSink: true, preferLowLatency: preferLowLatency, frameMs: 10);
            microphone.OnAudioSourceError += message => Status?.Invoke("마이크 오류: " + message);
            sendQueue = new AudioSendQueue(); sendCancellation = new CancellationTokenSource();
            var activeQueue = sendQueue;
            microphone.OnAudioSourceEncodedSample += (_, bytes) => activeQueue.Enqueue(bytes, microphone?.EncodedTraceHash);
            var sendToken = sendCancellation.Token;
            sendTask = Task.Run(() => DispatchAudio(activeQueue, sendToken));
            session = (await Post("api/join", new { room, name })).Deserialize<Session>(Json)!;
            cancellation = new CancellationTokenSource();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            eventLoop = ReadEvents(ready, cancellation.Token);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await gate.WaitAsync();
            try
            {
                foreach (var remote in session.Peers)
                {
                    // A departure may have been processed while waiting for the stream.
                    if (departed.Contains(remote.Id)) continue;
                    var peer = CreatePeer(remote.Id, remote.Name);
                    AttachTelemetry(peer, await peer.Connection.createDataChannel("voice-latency-v1"));
                    var offer = peer.Connection.createOffer(null);
                    await peer.Connection.setLocalDescription(offer);
                    await Send(remote.Id, new { type = "offer", sdp = offer.sdp });
                }
                await microphone.StartAudio();
            }
            finally { gate.Release(); }
            Status?.Invoke("방 참가 완료 · Opus 48kHz · 상대방 연결 대기");
        }
        catch { await LeaveAsync(); throw; }
    }

    private readonly HashSet<string> departed = new();
    private Peer CreatePeer(string id, string name)
    {
        lock (peers) if (peers.TryGetValue(id, out var existing)) return existing;
        var decoder = new AudioEncoder(includeOpus: true);
        var sink = new WindowsAudioDevice(decoder, disableSource: true, outputLatencyMs: outputLatencyMs, preferLowLatency: preferLowLatency, frameMs: 10);
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers });
        var metrics = new PeerMetrics();
        var precision = new RtcpMetrics();
        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                var receivedAt = ProbeClock.Now;
                if (sink.Timeline.Enabled) sink.Timeline.Update(AudioFrameTimeline.Hash(packet.Payload), old => old with { ReceivedAt = old.ReceivedAt ?? receivedAt, RtpNotificationAt = receivedAt, Receipts = old.Receipts + 1 });
                metrics.Received(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload.Length);
                precision.Packet(packet.Header.SyncSource, packet.Header.SequenceNumber);
            }
        };
        pc.OnReceiveReport += (_, media, report) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            precision.Incoming(report);
        };
        pc.OnSendReport += (media, report) => { if (media == SDPMediaTypesEnum.audio) precision.Outgoing(report); };
        pc.oniceconnectionstatechange += state =>
        {
            Status?.Invoke($"{name}: ICE {state}");
            if (state == RTCIceConnectionState.failed)
                Status?.Invoke("직접 연결 경로 확인 실패 · 두 PC의 앱 방화벽 허용과 Wi-Fi 기기 간 통신 차단 설정을 확인하세요.");
        };
        pc.onicegatheringstatechange += state => Status?.Invoke($"{name}: 연결 후보 수집 {state}");
        pc.addTrack(new MediaStreamTrack(sink.GetAudioSinkFormats(), MediaStreamStatusEnum.SendRecv));
        pc.OnAudioFrameReceived += frame => sink.GotEncodedMediaFrame(frame);
        pc.onicecandidate += candidate =>
        {
            if (candidate is not null)
            {
                Status?.Invoke($"{name}: 로컬 후보 {candidate.type} {candidate.address}:{candidate.port}");
                _ = SendCandidate(id, candidate);
            }
        };
        pc.onconnectionstatechange += state =>
        {
            Status?.Invoke($"{name}: {state}");
            if (state == RTCPeerConnectionState.connected) _ = StartSink(sink);
        };
        sink.OnAudioSinkError += message => Status?.Invoke("출력 오류: " + message);
        sink.OnAudioSinkRecovered += message => Status?.Invoke(name + ": " + message);
        var peer = new Peer(pc, sink, decoder, name, metrics, precision);
        pc.ondatachannel += channel => { if (channel.label == "voice-latency-v1") AttachTelemetry(peer, channel); };
        pc.OnAudioFormatsNegotiated += formats => peer.PayloadTypeId = formats.First().FormatID;
        lock (peers) peers.Add(id, peer);
        return peer;
    }
    private async Task StartSink(WindowsAudioDevice sink)
    {
        try { await sink.StartAudioSink(); }
        catch (Exception ex) { Status?.Invoke("출력 오류: " + ex.Message); }
    }
    private async Task SendCandidate(string id, RTCIceCandidate candidate)
    {
        try
        {
            await Send(id, new
            {
                type = "candidate",
                candidate = new
                {
                    candidate = candidate.candidate,
                    sdpMid = candidate.sdpMid,
                    sdpMLineIndex = candidate.sdpMLineIndex
                }
            });
        }
        catch (Exception ex) { if (session is not null) Status?.Invoke("연결 신호 오류: " + ex.Message); }
    }
    private readonly Dictionary<string, List<RTCIceCandidateInit>> pending = new();
    private async Task Handle(string kind, JsonElement data)
    {
        if (kind == "peer-left")
        {
            var id = data.GetProperty("id").GetString()!;
            departed.Add(id);
            Peer? peer;
            lock (peers) { peers.Remove(id, out peer); }
            pending.Remove(id);
            if (peer is not null) { peer.Connection.Close("left"); await peer.Sink.Close(); peer.Decoder.Dispose(); Status?.Invoke(peer.Name + ": 나감"); }
            return;
        }
        if (kind != "signal") return;
        var from = data.GetProperty("from").GetString()!;
        if (departed.Contains(from)) return;
        var signal = data.GetProperty("signal");
        var type = signal.GetProperty("type").GetString();
        if (type == "candidate")
        {
            var element = signal.GetProperty("candidate");
            if (element.ValueKind == JsonValueKind.Null) return;
            var candidate = element.Deserialize<RTCIceCandidateInit>(Json)!;
            Status?.Invoke($"상대 후보 수신: {candidate.candidate}");
            Peer? peer;
            lock (peers) peers.TryGetValue(from, out peer);
            if (peer?.Connection.remoteDescription is not null) peer.Connection.addIceCandidate(candidate);
            else { if (!pending.ContainsKey(from)) pending[from] = []; pending[from].Add(candidate); }
            return;
        }
        var connection = CreatePeer(from, data.GetProperty("name").GetString()!).Connection;
        var result = connection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = type == "offer" ? RTCSdpType.offer : RTCSdpType.answer,
            sdp = signal.GetProperty("sdp").GetString()
        });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException("SDP 협상 실패: " + result);
        Status?.Invoke($"{data.GetProperty("name").GetString()}: {type} 수신 · SDP 협상 완료");
        if (pending.Remove(from, out var candidates)) foreach (var candidate in candidates) connection.addIceCandidate(candidate);
        if (type == "offer")
        {
            var answer = connection.createAnswer(null);
            await connection.setLocalDescription(answer);
            await Send(from, new { type = "answer", sdp = answer.sdp });
        }
    }

    private async Task ReadEvents(TaskCompletionSource ready, CancellationToken token)
    {
        try
        {
            using var response = await streamHttp.GetAsync(Endpoint($"api/events?id={session!.Id}&token={session.Token}"), HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
            string kind = "", data = "";
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (line.StartsWith("event: ", StringComparison.Ordinal)) kind = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data += line[6..];
                else if (line.Length == 0 && data.Length > 0)
                {
                    if (kind == "ready") ready.TrySetResult();
                    else
                    {
                        await gate.WaitAsync(token);
                        try { using var document = JsonDocument.Parse(data); await Handle(kind, document.RootElement); }
                        finally { gate.Release(); }
                    }
                    kind = ""; data = "";
                }
            }
            if (!token.IsCancellationRequested) throw new IOException("서버 연결이 종료되었습니다.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            Status?.Invoke("연결 오류: " + ex.Message);
            // Do not await the current event loop during its own cleanup.
            _ = Task.Run(LeaveAsync, CancellationToken.None);
        }
    }
    public async Task LeaveAsync()
    {
        await cleanupGate.WaitAsync();
        try
        {
            cancellation?.Cancel();
            if (eventLoop is not null) await eventLoop;
            await gate.WaitAsync();
            try
            {
                if (microphone is not null) { await microphone.Close(); microphone = null; }
                sendQueue?.Complete(); sendCancellation?.Cancel();
                if (sendTask is not null) await sendTask;
                sendCancellation?.Dispose(); sendCancellation = null; sendTask = null; sendQueue = null;
                encoder?.Dispose(); encoder = null;
                Peer[] closing;
                lock (peers) { closing = peers.Values.ToArray(); peers.Clear(); }
                foreach (var peer in closing) { peer.Connection.Close("leave"); await peer.Sink.Close(); peer.Decoder.Dispose(); }
                var old = session; session = null;
                if (old is not null) { try { await Post("api/leave", new { old.Id, old.Token }); } catch { } }
                pending.Clear(); departed.Clear();
                loopback?.Dispose(); loopback = null;
                cancellation?.Dispose(); cancellation = null; eventLoop = null;
            }
            finally { gate.Release(); }
            Status?.Invoke("통화 종료 · 마이크 해제");
        }
        finally { cleanupGate.Release(); }
    }
    public async ValueTask DisposeAsync() { await LeaveAsync(); http.Dispose(); streamHttp.Dispose(); }
    public async Task RestartOutputAsync()
    {
        await gate.WaitAsync();
        try
        {
            Peer[] current;
            lock (peers) current = peers.Values.ToArray();
            foreach (var peer in current)
            {
                try { await peer.Sink.RestartOutputAsync(); Status?.Invoke(peer.Name + ": 출력 재시작 완료"); }
                catch (Exception ex) { Status?.Invoke(peer.Name + ": 출력 재시작 실패: " + ex.Message); }
            }
        }
        finally { gate.Release(); }
    }

    public void SetOpusProfile(OpusProfile profile)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 코덱을 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("코덱은 통화에서 나간 뒤 변경하세요.");
            opusProfile = profile;
            Status?.Invoke("송신 코덱 선택 · " + OpusAudioEncoder.Label(profile));
        }
        finally { gate.Release(); }
    }
    public void SetLowLatencyPreference(bool enabled)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 모드를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("저지연 모드는 통화에서 나간 뒤 변경하세요.");
            preferLowLatency = enabled;
            Status?.Invoke(enabled ? "공유 저지연 우선 · 실제 지원 여부는 진단에 표시됩니다." : "표준 공유 모드 사용 · 기존 10ms 입력/출력 요청 설정으로 연결합니다.");
        }
        finally { gate.Release(); }
    }
    public async Task SetOutputLatencyAsync(int milliseconds)
    {
        if (milliseconds is not (20 or 40)) throw new ArgumentOutOfRangeException(nameof(milliseconds));
        await gate.WaitAsync();
        try
        {
            outputLatencyMs = milliseconds;
            Peer[] current;
            lock (peers) current = peers.Values.ToArray();
            foreach (var peer in current)
            {
                try { await peer.Sink.RestartOutputAsync(outputLatencyMs: milliseconds); }
                catch (Exception ex)
                {
                    Status?.Invoke(peer.Name + ": 출력 설정 적용 실패: " + ex.Message);
                    throw;
                }
            }
            Status?.Invoke($"출력 복귀 요청 {milliseconds}ms 적용 · 저지연이 활성화돼 있으면 기존 엔진 주기를 유지합니다.");
        }
        finally { gate.Release(); }
    }

    public CallDiagnostics GetDiagnostics()
    {
        KeyValuePair<string, Peer>[] current;
        lock (peers) current = peers.ToArray();
        var mic = microphone?.Snapshot();
        var sender = sendQueue?.Snapshot();
        var snapshots = current.Select(entry =>
        {
            var peer = entry.Value;
            var sample = peer.Metrics.Sample();
            var detail = peer.Precision.Snapshot();
            var audio = peer.Sink.Snapshot();
            var remote = peer.Telemetry.Snapshot();
            var fresh = remote.Age is <= 3 && detail.RttAgeSeconds is <= 15 && peer.Connection.connectionState == RTCPeerConnectionState.connected;
            var view = new LatencyView(
                fresh && mic?.LastFrameAgeMs is <= 250 && remote.Remote?.OutputFrameAgeMs is <= 250 && remote.Remote.OutputState == "재생" ? TelemetryState.Model(mic?.DeviceAverageLatencyMs, mic?.ProcessingMs, sender?.LastWaitMs, detail.RtcpRttMs, remote.Remote.PlaybackQueueMs, remote.Remote.OutputApiMs, remote.Remote.DecodeMs, 10) : null,
                fresh && remote.Remote?.InputFrameAgeMs is <= 250 && audio.LastFrameAgeMs is <= 250 && audio.PlaybackState == "재생" ? TelemetryState.Model(remote.Remote?.InputApiMs, remote.Remote?.EncodeMs, remote.Remote?.SendWaitMs, detail.RtcpRttMs, audio.QueueMs, audio.DeviceLatencyMs, audio.ProcessingMs, remote.Remote?.FrameMs ?? 20) : null,
                remote.Age, remote.Remote, "합산 모델: API 지연+코덱 처리+송신자 프레임 대기+송신 대기+RTT/2+수신 큐+출력 API. 대칭 경로 가정이며 중복 대기·코덱 내부·물리 장치 지연은 검증되지 않음. 전체 음향 지연 실측 아님.");
            var channel = peer.DiagnosticsChannel;
            if (channel?.readyState == RTCDataChannelState.open && channel.bufferedAmount < 4096)
            {
                try { channel.send(JsonSerializer.Serialize(new LatencyTelemetry(1, mic?.DeviceAverageLatencyMs, mic?.ProcessingMs, sender?.LastWaitMs, audio.QueueMs, audio.DeviceLatencyMs, audio.ProcessingMs, audio.PlaybackState, mic?.LastFrameAgeMs, audio.LastFrameAgeMs, 10))); }
                catch (Exception) { } // Diagnostics must not interrupt audio if the channel closes.
            }
            var nominated = peer.Connection.GetRtpChannel().NominatedEntry;
            return new PeerDiagnostics(entry.Key, peer.Name, peer.Connection.connectionState.ToString(), peer.Connection.iceConnectionState.ToString(),
                nominated is null ? "경로 미선정" : nominated.LocalCandidate.type == RTCIceCandidateType.relay || nominated.RemoteCandidate.type == RTCIceCandidateType.relay ? "TURN 중계" : "직접 연결",
                nominated is null ? null : FormatEndpoint(nominated.LocalCandidate),
                nominated is null ? null : FormatEndpoint(nominated.RemoteCandidate),
                sample.Tx, sample.Rx, sample.TxKbps, sample.RxKbps, sample.Jitter, detail.SendLossPercent, detail.SendReportAgeSeconds, audio, detail, view);
        }).ToArray();
        return new CallDiagnostics(DateTimeOffset.Now, session is not null, mic, snapshots,
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency, sender, acousticResult, 10, encoder?.Snapshot());
    }
    private void AttachTelemetry(Peer peer, RTCDataChannel channel)
    {
        peer.DiagnosticsChannel = channel;
        channel.onmessage += (_, _, bytes) =>
        {
            var receivedAt = ProbeClock.Now;
            if (!peer.Telemetry.Receive(bytes)) HandleProbe(peer, bytes, receivedAt);
        };
    }
    private static void SendProbe(Peer peer, ProbeMessage message)
    {
        var channel = peer.DiagnosticsChannel;
        if (channel?.readyState != RTCDataChannelState.open) throw new InvalidOperationException("상대와 진단 채널이 연결되지 않았습니다. 양쪽을 최신 앱으로 실행하세요.");
        channel.send(JsonSerializer.Serialize(message));
    }
    private void HandleProbe(Peer peer, byte[] bytes, double receivedAt)
    {
        if (bytes.Length > 2048) return;
        try
        {
            var message = JsonSerializer.Deserialize<ProbeMessage>(bytes);
            if (message is null || !Guid.TryParse(message.Id, out _) || !new[] { message.A, message.B, message.C }.All(double.IsFinite)) return;
            if (message.Kind == "clock") SendProbe(peer, new("clockReply", message.Id, message.A, receivedAt, ProbeClock.Now));
            else if (message.Kind is "arm" or "armLoopback")
            {
                if (microphone is null || measurementGate.CurrentCount == 0) { SendProbe(peer, new("error", message.Id)); return; }
                peer.Sink.Timeline.EnableFor(8);
                Action<double> onDetected = detected =>
                {
                    try { SendProbe(peer, new("detected", message.Id, detected)); }
                    catch (Exception) { }
                };
                if (message.Kind == "armLoopback")
                {
                    loopback ??= new LoopbackLatencyCapture(peer.Sink.OutputDeviceId);
                    loopback.Arm(message.Id, onDetected);
                    Status?.Invoke("루프백 측정 수신 대기 · 출력 장치 " + loopback.Device);
                }
                else microphone.Acoustic.Arm(message.Id, onDetected);
                if (message.Kind == "arm") Status?.Invoke("음향 측정 수신 대기 · 상대가 400ms 음성형 테스트음을 보냅니다.");
                SendProbe(peer, new("ready", message.Id));
            }
            else if (message.Kind == "cancel") { microphone?.Acoustic.Cancel(message.Id); loopback?.Cancel(message.Id); }
            else if (message.Kind == "error")
            {
                foreach (var entry in peer.ProbeWaiters.Where(e => e.Key.StartsWith(message.Id + ":", StringComparison.Ordinal))) entry.Value.TrySetException(new InvalidOperationException("상대 마이크를 사용할 수 없거나 상대도 측정 중입니다. 한쪽에서만 측정하세요."));
            }
            else if (message.Kind == "stages" && message.PayloadHash is { Length: 64 } hash)
                SendProbe(peer, new("stageReply", message.Id, Stages: peer.Sink.Timeline.Find(hash)));
            else if (message.Kind is "clockReply" or "ready" or "detected" or "stageReply")
            {
                if (peer.ProbeWaiters.TryGetValue(message.Id + ":" + message.Kind, out var waiter)) waiter.TrySetResult(new(message, receivedAt));
            }
        }
        catch (Exception) { } // Invalid diagnostic messages must never stop the call.
    }
    private static async Task<ProbeReply> RequestProbe(Peer peer, ProbeMessage message, string expected, CancellationToken token)
    {
        var key = message.Id + ":" + expected;
        var waiter = new TaskCompletionSource<ProbeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!peer.ProbeWaiters.TryAdd(key, waiter)) throw new InvalidOperationException("중복 측정 요청입니다.");
        try { SendProbe(peer, message); return await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2), token); }
        finally { peer.ProbeWaiters.TryRemove(key, out _); }
    }
    public Task MeasureAcousticLatencyAsync() => MeasureSignalLatencyAsync(false);
    public Task MeasureLoopbackLatencyAsync() => MeasureSignalLatencyAsync(true);
    private async Task MeasureSignalLatencyAsync(bool useLoopback)
    {
        if (!await measurementGate.WaitAsync(0)) throw new InvalidOperationException("이미 음향 측정 중입니다.");
        Peer? target = null; string? probeId = null;
        try
        {
            Peer[] connected;
            lock (peers) connected = peers.Values.Where(p => p.Connection.connectionState == RTCPeerConnectionState.connected).ToArray();
            if (connected.Length != 1 || microphone is null || cancellation is null) throw new InvalidOperationException("음향 측정은 연결된 1:1 통화에서 사용하세요.");
            target = connected[0]; var token = cancellation.Token;
            Status?.Invoke(useLoopback ? "루프백 측정 시작 · 상대 출력 장치의 디지털 신호를 감지합니다. 400ms 테스트음을 전송합니다." : "음향 측정 시작 · 상대는 스피커 소리를 마이크로 받을 수 있게 하고 잠시 말하지 마세요. 400ms 음성형 테스트음을 전송합니다.");
            (double Low, double High)? best = null;
            for (var i = 0; i < 8; i++)
            {
                var sentAt = ProbeClock.Now;
                var reply = await RequestProbe(target, new("clock", Guid.NewGuid().ToString("N"), sentAt), "clockReply", token);
                var bounds = ProbeClock.Bounds(sentAt, reply.Message.B, reply.Message.C, reply.ReceivedAt);
                if (bounds is { } value && (best is null || value.High - value.Low < best.Value.High - best.Value.Low)) best = value;
                await Task.Delay(25, token);
            }
            if (best is null) throw new InvalidOperationException("유효한 시계 보정 값을 얻지 못했습니다.");
            probeId = Guid.NewGuid().ToString("N");
            var detected = new TaskCompletionSource<ProbeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            target.ProbeWaiters[probeId + ":detected"] = detected;
            await RequestProbe(target, new(useLoopback ? "armLoopback" : "arm", probeId), "ready", token);
            var sourceAt = await microphone.Acoustic.Inject().WaitAsync(TimeSpan.FromSeconds(2), token);
            var arrival = await detected.Task.WaitAsync(TimeSpan.FromSeconds(6), token);
            var low = (arrival.Message.A - sourceAt - best.Value.High) * 1000 - 20;
            var high = (arrival.Message.A - sourceAt - best.Value.Low) * 1000 + 20;
            if (high < 0 || high > 5000) throw new InvalidOperationException("검출 시각이 유효 범위를 벗어났습니다.");
            AudioFrameStages? sourceStages = null, receiverStages = null;
            if (microphone.LastInjectedHash is { } injectedHash)
            {
                sourceStages = microphone.Timeline.Find(injectedHash);
                var stagesReply = await RequestProbe(target, new("stages", probeId, PayloadHash: injectedHash), "stageReply", token);
                receiverStages = stagesReply.Message.Stages;
            }
            if (receiverStages?.OutputSubmittedAt is { } submittedAt && !AudioFrameTimeline.IsDetectionCoherent(submittedAt, arrival.Message.A, useLoopback))
                throw new InvalidOperationException("검출 시각이 해당 테스트 패킷의 출력 시각과 일치하지 않습니다. 배경 신호 오검출 또는 캡처 시각 이상으로 측정에서 제외합니다.");
            var stageBreakdown = sourceStages is not null && receiverStages is { Receipts: 1, Discarded: false } && sourceStages.Hash == receiverStages.Hash
                ? AudioFrameTimeline.Breakdown(sourceStages, receiverStages, best.Value.Low, best.Value.High, arrival.Message.A) : null;
            acousticResult = new(DateTimeOffset.Now, target.Name, Math.Max(0, low), high,
                (best.Value.High - best.Value.Low) * 1000, 20,
                useLoopback ? "송신 캡처 프레임 QPC→상대 출력 장치 WASAPI 루프백 QPC. 시계 범위와 검출 여유 ±20ms 포함. 루프백 캡처 지연/드라이버 수집 지점 포함. 물리 스피커/마이크·실제 입→귀 지연 아님." :
                "송신 캡처 프레임의 QPC 시각→상대 스피커를 상대 마이크로 재캡처한 시각. 시계 보정 범위와 검출 여유 ±20ms 포함. 검출 여유는 보장 오차가 아님. 마이크 하드웨어 앞단·실제 귀 도달 시점은 별도이며 스피커→마이크 공기 전파가 포함됨.", useLoopback ? "wasapi-loopback" : "acoustic", sourceStages, receiverStages, stageBreakdown);
            Status?.Invoke($"{(useLoopback ? "루프백" : "음향 경로")} 측정: 내→{target.Name} {acousticResult.MinimumMs:F1}~{acousticResult.MaximumMs:F1}ms · 시계 범위 {acousticResult.ClockUncertaintyMs:F1}ms · 검출 ±20ms");
        }
        finally
        {
            if (target is not null && probeId is not null)
            {
                target.ProbeWaiters.TryRemove(probeId + ":detected", out _);
                try { SendProbe(target, new("cancel", probeId)); } catch (Exception) { }
            }
            microphone?.Acoustic.Cancel(); measurementGate.Release();
        }
    }
    private async Task DispatchAudio(AudioSendQueue queue, CancellationToken token)
    {
        try
        {
            await foreach (var frame in queue.Read(token))
            {
                queue.Observe(frame);
                lock (peers)
                {
                    foreach (var peer in peers.Values)
                    {
                        if (peer.Connection.connectionState != RTCPeerConnectionState.connected) continue;
                        try
                        {
                            if (peer.FirstSequence == 0) { peer.FirstSequence = frame.Sequence; peer.TimestampBase = peer.Connection.AudioLocalTrack.Timestamp; }
                            var timestamp = AudioSendQueue.Timestamp(peer.TimestampBase, peer.FirstSequence, frame.Sequence, 480);
                            var sentAt = ProbeClock.Now;
                            if (frame.TraceHash is { } traceHash) microphone?.Timeline.Update(traceHash, old => old with { SentAt = sentAt });
                            peer.Connection.SendRtpRaw(SDPMediaTypesEnum.audio, frame.Payload, timestamp,
                                peer.LastSentSequence == 0 || frame.Sequence != peer.LastSentSequence + 1 ? 1 : 0, peer.PayloadTypeId);
                            peer.LastSentSequence = frame.Sequence; peer.Metrics.Sent(frame.Payload.Length);
                        }
                        catch (Exception ex) { Status?.Invoke(peer.Name + ": 음성 송신 오류: " + ex.Message); }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private static string FormatEndpoint(RTCIceCandidate candidate) =>
        candidate.address.Contains(':') ? $"[{candidate.address}]:{candidate.port}" : $"{candidate.address}:{candidate.port}";
}
