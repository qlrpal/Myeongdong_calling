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
    // Publish only on membership changes; sending never copies or locks the map.
    private Peer[] sendPeers = [];
    private readonly object framePolicySync = new();
    private FrameAdaptationDiagnostics? frameAdaptation;
    private readonly List<RTCIceServer> iceServers = new();
    private CancellationTokenSource? cancellation;
    private Task? eventLoop;
    private Uri? server;
    private Session? session;
    private WindowsAudioDevice? microphone;
    private OpusAudioEncoder? encoder;
    private OpusProfile opusProfile = OpusAudioEncoder.DefaultProfile;
    private AudioSendQueue? sendQueue;
    private CancellationTokenSource? sendCancellation;
    private Task? sendTask;
    private DedicatedAudioSender? dedicatedSender;
    private readonly SemaphoreSlim measurementGate = new(1);
    private AcousticLatencyResult? acousticResult;
    private int outputLatencyMs = 40;
    private bool preferLowLatency = true;
    private bool useRtpPlayout = true;
    private bool adaptivePlayout;
    private PlayoutPolicy playoutPolicy;
    // Individual overrides are for diagnostic comparisons; the UI selects a policy.
    internal bool FixedReserveClockTarget { get; init; }
    internal bool TransientEmptyQueueHold { get; init; }
    internal bool FreshRestart { get; init; }
    internal bool? BoundedRender { get; init; }
    internal bool? RawRender { get; init; }
    private bool preferRawOutput = true;
    private string? mediaAddress;
    internal bool IntermediateCapture { get; init; } = true;
    internal string? CaptureDeviceId { get; init; }
    private string? selectedCaptureId, selectedRenderId;
    private bool microphoneMuted;
    private LatencyExperiment? experiment;
    private double FrameDurationMs => microphone?.FrameDurationMs ?? experiment?.FrameMs ?? 10;
    public void SetLatencyExperiment(LatencyExperiment? experiment)
    {
        experiment?.Validate();
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 실험을 바꿀 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("통화에서 나온 뒤 실험을 선택하세요.");
            this.experiment = experiment;
        }
        finally { gate.Release(); }
    }
    public void SetMicrophoneMuted(bool muted)
    {
        Volatile.Write(ref microphoneMuted, muted);
        microphone?.SetMuted(muted);
    }
    public void SetPeerVolume(string peerId, float volume)
    {
        if (!float.IsFinite(volume) || volume is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(volume));
        lock (peers) { if (peers.TryGetValue(peerId, out var peer)) peer.Sink.SetVolume(volume); }
    }
    public void SetAudioDevices(string? captureId, string? renderId)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 장치를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("통화에서 나간 뒤 장치를 변경하세요.");
            selectedCaptureId = captureId; selectedRenderId = renderId;
        }
        finally { gate.Release(); }
    }
    internal bool ForceRelay { get; init; }
    internal bool DirectOnly { get; init; }
    // Diagnostic-only reserve comparison. Product callers keep the 20ms default.
    internal int DiagnosticReserveMs { get; init; } = 20;
    internal bool ResponsiveReserve { get; init; }
    internal bool? QuickGapRecovery { get; init; }
    internal Action<short[]>? DiagnosticCaptureTransform { get; init; }
    private bool preferShortCapture = true;
    private readonly PipelineTrace pipeline = new();
    internal void StartPipelineTrace(int seconds) => pipeline.Start(seconds);
    internal PipelineTraceSnapshot StopPipelineTrace() { pipeline.Stop(); return pipeline.Snapshot(); }
    private LoopbackLatencyCapture? loopback;
    private sealed record ProbeReply(ProbeMessage Message, double ReceivedAt);
    public event Action<string>? Status;
    private sealed record Session(string Id, string Token, RemotePeer[] Peers);
    private sealed record RemotePeer(string Id, string Name);
    private sealed record Peer(RTCPeerConnection Connection, WindowsAudioDevice Sink, AudioEncoder Decoder, string Name, PeerMetrics Metrics, RtcpMetrics Precision,
        ConcurrentDictionary<string, byte> CandidateFamilies)
    {
        public AudioSendLifetime SendLifetime { get; } = new();
        public AdaptiveFramePolicy FramePolicy { get; } = new();
        public long LastSentSequence { get; set; }
        public long FirstSequence { get; set; }
        public long FirstSampleOffset { get; set; }
        public uint TimestampBase { get; set; }
        public int PayloadTypeId { get; set; } = 111;
        public RTCDataChannel? DiagnosticsChannel { get; set; }
        public TelemetryState Telemetry { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource<ProbeReply>> ProbeWaiters { get; } = new();
    }

    private Uri Endpoint(string path) => new(server!, path);
    private sealed class SignalingException(System.Net.HttpStatusCode code, string message) : InvalidOperationException(message)
    { public System.Net.HttpStatusCode Code { get; } = code; }
    private async Task<JsonElement> Post(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(Endpoint(path), body, Json);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        if (!response.IsSuccessStatusCode)
            throw new SignalingException(response.StatusCode, (result.TryGetProperty("error", out var error) ? error.GetString() : response.ReasonPhrase) ?? "서버 요청 실패");
        return result;
    }
    private async Task Send(string to, object signal)
    {
        var current = session;
        if (current is null) return;
        try { await Post("api/signal", new { current.Id, current.Token, to, signal }); }
        catch (SignalingException ex) when (ex.Code == System.Net.HttpStatusCode.NotFound && ex.Message == "참여자가 통화를 나갔습니다.")
        {
            // The join snapshot can race a remote departure. The queued peer-left
            // event removes that peer once the negotiation gate is released.
            Status?.Invoke("상대가 협상 중 나갔습니다 · 방 연결은 유지합니다.");
        }
    }

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
            if (ForceRelay && DirectOnly) throw new InvalidOperationException("Conflicting diagnostic route requirements.");
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
            if (DirectOnly) iceServers.RemoveAll(s => s.urls.StartsWith("turn:", StringComparison.OrdinalIgnoreCase) || s.urls.StartsWith("turns:", StringComparison.OrdinalIgnoreCase));
            Status?.Invoke($"ICE 서버 {iceServers.Count}개 · TURN 중계 " +
                (iceServers.Any(s => s.urls.StartsWith("turn:", StringComparison.Ordinal) || s.urls.StartsWith("turns:", StringComparison.Ordinal)) ? "사용 가능" : "미설정"));
            if (ForceRelay && !iceServers.Any(s => s.urls.StartsWith("turn:", StringComparison.Ordinal) || s.urls.StartsWith("turns:", StringComparison.Ordinal)))
                throw new InvalidOperationException("TURN 강제 시험에 필요한 TURN 서버가 없습니다.");
            encoder = new OpusAudioEncoder(experiment is null ? opusProfile : OpusProfile.LowDelay64, experiment);
            Status?.Invoke("송신 코덱 · " + (experiment is null ? OpusAudioEncoder.Label(opusProfile) : encoder.Snapshot().Profile) + $" · {FrameDurationMs}ms");
            if (experiment is not null && (!useRtpPlayout || !preferLowLatency)) throw new InvalidOperationException("극저지연 실험은 공유 저지연·RTP 재생을 켜야 합니다.");
            microphone = new WindowsAudioDevice(encoder, disableSink: true, preferLowLatency: preferLowLatency, frameMs: FrameDurationMs, pipeline: pipeline, intermediateCapture: IntermediateCapture && preferShortCapture, captureDeviceId: selectedCaptureId ?? CaptureDeviceId, experiment: experiment)
            { DiagnosticCaptureTransform = DiagnosticCaptureTransform };
            microphone.SetMuted(Volatile.Read(ref microphoneMuted));
            microphone.OnAudioSourceError += message => Status?.Invoke("마이크 오류: " + message);
            // Short frames can arrive in a single device-period burst. A one-frame
            // asynchronous queue would discard valid siblings before the worker wakes.
            var sendCapacity = experiment?.DedicatedSend == true
                ? AudioSendQueue.BurstCapacity(microphone.Snapshot().EnginePeriodMs ?? 10, FrameDurationMs)
                : experiment?.LatestSend == true ? 1 : 3;
            sendQueue = new AudioSendQueue(sendCapacity); sendCancellation = new CancellationTokenSource();
            var activeQueue = sendQueue;
            dedicatedSender = experiment?.DedicatedSend == true ? new(activeQueue,
                frame => DispatchFrame(frame, pipeline.Enabled ? ProbeClock.Now : 0), experiment.CriticalAudioPriority) : null;
            var activeSender = dedicatedSender;
            microphone.OnAudioSourceEncodedSample += (samples, bytes) =>
            {
                if (experiment?.InlineSend == true)
                {
                    var frame = activeQueue.CreateFrame(bytes, microphone?.EncodedTraceHash, microphone?.LastCaptureOrigin, (int)samples);
                    activeQueue.Observe(frame); DispatchFrame(frame, pipeline.Enabled ? ProbeClock.Now : 0);
                }
                else
                {
                    activeQueue.Enqueue(bytes, microphone?.EncodedTraceHash, microphone?.LastCaptureOrigin, (int)samples);
                    activeSender?.Notify();
                }
            };
            var sendToken = sendCancellation.Token;
            sendTask = activeSender?.Completion ?? (experiment?.InlineSend == true ? null : Task.Run(() => DispatchAudio(activeQueue, sendToken)));
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
            Status?.Invoke($"방 참가 완료 · {(experiment?.Pcm == true ? "L16 PCM" : "Opus")} 48kHz / {FrameDurationMs}ms · 상대방 연결 대기");
        }
        catch { await LeaveAsync(); throw; }
    }

    private readonly HashSet<string> departed = new();
    private Peer CreatePeer(string id, string name)
    {
        lock (peers) if (peers.TryGetValue(id, out var existing)) return existing;
        var selectedInterface = MediaInterfaceSelection.Resolve(mediaAddress);
        var decoder = new AudioEncoder(includeOpus: true);
        var lowDelay = experiment is not null || playoutPolicy is PlayoutPolicy.LowLatency or PlayoutPolicy.ResponsiveLowLatency;
        var responsive = ResponsiveReserve || playoutPolicy == PlayoutPolicy.ResponsiveLowLatency;
        var rawOutput = RawRender ?? (preferRawOutput && preferLowLatency && lowDelay && useRtpPlayout);
        var sink = new WindowsAudioDevice(decoder, disableSource: true, outputLatencyMs: outputLatencyMs, preferLowLatency: preferLowLatency, frameMs: 10, useRtpPlayout: useRtpPlayout, adaptiveReserve: adaptivePlayout, pipeline: pipeline, tracePeer: id, fixedReserveClockTarget: FixedReserveClockTarget || lowDelay, transientEmptyQueueHold: TransientEmptyQueueHold || lowDelay, freshRestart: FreshRestart || lowDelay, boundedRender: BoundedRender ?? (lowDelay && useRtpPlayout), rawRender: rawOutput, renderCategory: rawOutput ? NAudio.CoreAudioApi.AudioStreamCategory.GameChat : NAudio.CoreAudioApi.AudioStreamCategory.Other, rtpReserveMs: responsive ? 15 : DiagnosticReserveMs, responsiveReserve: responsive, quickGapRecovery: QuickGapRecovery ?? responsive, renderDeviceId: selectedRenderId, experiment: experiment);
        RTCPeerConnection? pc = null;
        try
        {
            pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers,
                iceTransportPolicy = ForceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
                X_BindAddress = selectedInterface is null ? null : System.Net.IPAddress.Parse(selectedInterface.Address!) });
            if (selectedInterface is not null) MediaInterfaceSelection.Apply(pc.GetRtpChannel().RtpSocket, selectedInterface);
        }
        catch { pc?.Close("media interface unavailable"); sink.Close().GetAwaiter().GetResult(); decoder.Dispose(); throw; }
        var metrics = new PeerMetrics();
        var candidateFamilies = new ConcurrentDictionary<string, byte>();
        var precision = new RtcpMetrics();
        var receivedPayloadType = 111;
        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio)
            {
                var receivedAt = ProbeClock.Now;
                if (sink.Timeline.Enabled) sink.Timeline.Update(AudioFrameTimeline.Hash(packet.Payload), old => old with { ReceivedAt = old.ReceivedAt ?? receivedAt, RtpNotificationAt = receivedAt, Receipts = old.Receipts + 1 });
                metrics.Received(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload.Length);
                precision.Packet(packet.Header.SyncSource, packet.Header.SequenceNumber);
                if (useRtpPlayout && packet.Header.PayloadType == Volatile.Read(ref receivedPayloadType))
                {
                    var bufferCallAt = pipeline.Enabled ? ProbeClock.Now : 0;
                    sink.GotRtpAudioPacket(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload);
                    if (pipeline.Enabled) pipeline.Received(new(id, packet.Header.SyncSource, packet.Header.Timestamp,
                        packet.Header.SequenceNumber, receivedAt, bufferCallAt, ProbeClock.Now));
                }
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
        if (!useRtpPlayout) pc.OnAudioFrameReceived += frame => sink.GotEncodedMediaFrame(frame);
        pc.onicecandidate += candidate =>
        {
            if (candidate is not null)
            {
                candidateFamilies.TryAdd(AddressFamily(candidate.address), 0);
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
        var peer = new Peer(pc, sink, decoder, name, metrics, precision, candidateFamilies);
        pc.ondatachannel += channel => { if (channel.label == "voice-latency-v1") AttachTelemetry(peer, channel); };
        pc.OnAudioFormatsNegotiated += formats =>
        {
            peer.PayloadTypeId = formats.First().FormatID;
            Volatile.Write(ref receivedPayloadType, peer.PayloadTypeId);
        };
        lock (peers)
        {
            peers.Add(id, peer);
            Volatile.Write(ref sendPeers, peers.Values.ToArray());
        }
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
            lock (peers)
            {
                peers.Remove(id, out peer);
                Volatile.Write(ref sendPeers, peers.Values.ToArray());
            }
            pending.Remove(id);
            if (peer is not null) { await ClosePeerAsync(peer, "left"); Status?.Invoke(peer.Name + ": 나감"); }
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
                frameAdaptation = null;
                sendQueue?.Complete(); sendCancellation?.Cancel();
                dedicatedSender?.Stop();
                if (sendTask is not null) await sendTask;
                dedicatedSender?.Dispose(); dedicatedSender = null;
                sendCancellation?.Dispose(); sendCancellation = null; sendTask = null; sendQueue = null;
                encoder?.Dispose(); encoder = null;
                Peer[] closing;
                lock (peers)
                {
                    closing = peers.Values.ToArray(); peers.Clear();
                    Volatile.Write(ref sendPeers, []);
                }
                foreach (var peer in closing) await ClosePeerAsync(peer, "leave");
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

    public void SetRtpPlayoutPreference(bool enabled)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 재생 모드를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("재생 모드는 통화에서 나간 뒤 변경하세요.");
            useRtpPlayout = enabled;
            Status?.Invoke(enabled ? "RTP 시간축 재생 · 20ms 목표 여유·PLC 활성" : "기존 PCM 재생 경로 사용");
        }
        finally { gate.Release(); }
    }
    public void SetAdaptivePlayoutPreference(bool enabled)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 적응 여유를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("적응 여유는 통화에서 나간 뒤 변경하세요.");
            if (enabled && playoutPolicy != PlayoutPolicy.Compatible) throw new InvalidOperationException("지터 적응은 기존 재생 정책에서 선택하세요.");
            adaptivePlayout = enabled;
            Status?.Invoke(enabled ? "지터 적응 여유 · 20~40ms" : "고정 여유 · 20ms, 소비율 보정 유지");
        }
        finally { gate.Release(); }
    }
    public void SetMediaInterface(string? address)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 네트워크를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("네트워크는 통화에서 나간 뒤 변경하세요.");
            var selected = MediaInterfaceSelection.Resolve(address);
            mediaAddress = selected?.Address;
            Status?.Invoke(selected is null ? "미디어 네트워크: OS 자동 · VPN 경로 유지" : $"미디어 네트워크: {selected.Label} · 이 앱의 UDP만 선택한 인터페이스 사용");
        }
        finally { gate.Release(); }
    }
    public void SetRawOutputPreference(bool enabled)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 출력 효과 설정을 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("출력 효과 설정은 통화에서 나간 뒤 변경하세요.");
            preferRawOutput = enabled;
            Status?.Invoke(enabled ? "저지연 재생에서 통화 출력 효과 우회 우선 · 미지원 시 공유 출력 복귀" : "기존 통화 출력 효과 사용");
        }
        finally { gate.Release(); }
    }
    public void SetShortCapturePreference(bool enabled)
    {
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 입력 모드를 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("입력 모드는 통화에서 나간 뒤 변경하세요.");
            preferShortCapture = enabled;
            Status?.Invoke(enabled ? "짧은 공유 입력 주기 우선 · 미지원 시 기존 공유 입력 사용" : "기존 공유 입력 사용 · 출력 설정 유지");
        }
        finally { gate.Release(); }
    }
    public void SetPlayoutPolicy(PlayoutPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentOutOfRangeException(nameof(policy));
        if (!gate.Wait(0)) throw new InvalidOperationException("연결 작업 중에는 재생 정책을 변경할 수 없습니다.");
        try
        {
            if (session is not null || microphone is not null) throw new InvalidOperationException("재생 정책은 통화에서 나간 뒤 변경하세요.");
            playoutPolicy = policy;
            if (policy != PlayoutPolicy.Compatible) adaptivePlayout = false;
            Status?.Invoke(policy switch
            {
                PlayoutPolicy.ResponsiveLowLatency => "자동 저지연 · 20ms 시작·15~30ms 적응·반복 공백 빠른 복구 (시험)",
                PlayoutPolicy.LowLatency => "저지연 재생 · 20ms 목표·짧은 공백 보류·최신 큐 복귀",
                _ => "기존 재생 · 시작 여유 학습"
            });
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
        UpdateFramePolicy(current.Select(p => p.Value));
        var mic = microphone?.Snapshot();
        var sender = sendQueue?.Snapshot();
        if (sender is not null) sender = sender with { Mode = dedicatedSender is not null ? "dedicated" : experiment?.InlineSend == true ? "inline" : "queued",
            MmcssActive = dedicatedSender?.MmcssActive, Error = dedicatedSender?.Failure };
        var snapshots = current.Select(entry =>
        {
            var peer = entry.Value;
            var sample = peer.Metrics.Sample();
            var detail = peer.Precision.Snapshot();
            var audio = peer.Sink.Snapshot();
            var remote = peer.Telemetry.Snapshot();
            var fresh = remote.Age is <= 3 && detail.RttAgeSeconds is <= 15 && peer.Connection.connectionState == RTCPeerConnectionState.connected;
            var view = new LatencyView(
                fresh && mic?.LastFrameAgeMs is <= 250 && remote.Remote?.OutputFrameAgeMs is <= 250 && remote.Remote.OutputState == "재생" ? TelemetryState.Model(mic?.DeviceAverageLatencyMs, mic?.ProcessingMs, sender?.LastWaitMs, detail.RtcpRttMs, remote.Remote.PlaybackQueueMs, remote.Remote.OutputApiMs, remote.Remote.DecodeMs, FrameDurationMs) : null,
                fresh && remote.Remote?.InputFrameAgeMs is <= 250 && audio.LastFrameAgeMs is <= 250 && audio.PlaybackState == "재생" ? TelemetryState.Model(remote.Remote?.InputApiMs, remote.Remote?.EncodeMs, remote.Remote?.SendWaitMs, detail.RtcpRttMs, audio.QueueMs, audio.DeviceLatencyMs, audio.ProcessingMs, remote.Remote?.FrameMs ?? 20) : null,
                remote.Age, remote.Remote, "합산 모델: API 지연+코덱 처리+송신자 프레임 대기+송신 대기+RTT/2+수신 큐+출력 API. 대칭 경로 가정이며 중복 대기·코덱 내부·물리 장치 지연은 검증되지 않음. 전체 음향 지연 실측 아님.");
            var channel = peer.DiagnosticsChannel;
            if (channel?.readyState == RTCDataChannelState.open && channel.bufferedAmount < 4096)
            {
                var feedback = audio.RtpPlayout is { } playout && audio.DecodedMs is { } decodedMs
                    ? new ReceiverFrameFeedback(experiment?.AdaptiveShortReserve == true && experiment.Pcm == false,
                        sample.Rx, decodedMs, playout.ConcealedMs, playout.LatePackets, playout.SourceChanges,
                        audio.DeviceError is null && audio.PlaybackState == "재생" && audio.LastFrameAgeMs is <= 250) : null;
                try { channel.send(JsonSerializer.Serialize(new LatencyTelemetry(1, mic?.DeviceAverageLatencyMs, mic?.ProcessingMs, sender?.LastWaitMs, audio.QueueMs, audio.DeviceLatencyMs, audio.ProcessingMs, audio.PlaybackState, mic?.LastFrameAgeMs, audio.LastFrameAgeMs, FrameDurationMs, feedback))); }
                catch (Exception) { } // Diagnostics must not interrupt audio if the channel closes.
            }
            var nominated = peer.Connection.GetRtpChannel().NominatedEntry;
            return new PeerDiagnostics(entry.Key, peer.Name, peer.Connection.connectionState.ToString(), peer.Connection.iceConnectionState.ToString(),
                nominated is null ? "경로 미선정" : nominated.LocalCandidate.type == RTCIceCandidateType.relay || nominated.RemoteCandidate.type == RTCIceCandidateType.relay ? "TURN 중계" : "직접 연결",
                nominated is null ? null : FormatEndpoint(nominated.LocalCandidate),
                nominated is null ? null : FormatEndpoint(nominated.RemoteCandidate),
                sample.Tx, sample.Rx, sample.TxKbps, sample.RxKbps, sample.Jitter, detail.SendLossPercent, detail.SendReportAgeSeconds, audio, detail, view,
                nominated?.LocalCandidate.protocol.ToString(), nominated is null ? null : AddressFamily(nominated.LocalCandidate.address),
                nominated is null ? null : AddressFamily(nominated.RemoteCandidate.address), peer.CandidateFamilies.Keys.Order().ToArray());
        }).ToArray();
        return new CallDiagnostics(DateTimeOffset.Now, session is not null, mic, snapshots,
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency, sender, acousticResult, FrameDurationMs, encoder?.Snapshot(),
            FixedReserveClockTarget || TransientEmptyQueueHold || FreshRestart ? "experimental" : playoutPolicy.ToString(), mediaAddress, experiment, frameAdaptation);
    }
    private void UpdateFramePolicy(IEnumerable<Peer> current)
    {
        var source = microphone;
        if (experiment?.AdaptiveFrames != true || source is null) return;
        lock (framePolicySync)
        {
            var connected = current.Where(p => p.Connection.connectionState == RTCPeerConnectionState.connected).ToArray();
            // A shared encoder cannot independently satisfy several receivers.
            // Keep multi-peer calls fixed until per-peer control is qualified.
            var ready = connected.Length == 1;
            var now = ProbeClock.Now;
            var reports = connected.Select(peer => (Peer: peer, Report: peer.Telemetry.Snapshot())).ToArray();
            if (reports.Any(p => p.Report.Age is not <= 3 || p.Report.Remote?.FrameFeedback is not { Supported: true, OutputHealthy: true })) ready = false;
            foreach (var entry in reports)
            {
                if (!ready) entry.Peer.FramePolicy.Suspend();
                else entry.Peer.FramePolicy.Observe(now, entry.Report.Remote!.FrameFeedback!);
            }
            var target = ready ? connected.Max(p => p.FramePolicy.TargetMs) : FrameDurationMs;
            if (ready) source.RequestFrameDuration(target);
            frameAdaptation = new(FrameDurationMs, target, ready,
                ready ? string.Join("; ", connected.Select(p => p.FramePolicy.Reason)) : "requires one fresh compatible receiver; hold current frame",
                connected.Sum(p => p.FramePolicy.Changes));
        }
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
        if (experiment is not null) throw new InvalidOperationException("극저지연 실험에서는 전용 파이프라인 계측을 사용하세요. 기존 음향 검사는 기본 모드에서 실행합니다.");
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
            if (useLoopback && !AudioFrameTimeline.HasVerifiedPlayout(sourceStages, receiverStages))
                throw new InvalidOperationException("테스트 시작 패킷의 정상 디코딩·출력 제출을 확인하지 못해 루프백 측정에서 제외합니다. 지연 도착·폐기·중복 또는 추적 누락을 확인하세요.");
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
                var dequeuedAt = pipeline.Enabled ? ProbeClock.Now : 0;
                DispatchFrame(frame, dequeuedAt);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void DispatchFrame(OutgoingAudio frame, double dequeuedAt)
    {
        foreach (var peer in Volatile.Read(ref sendPeers))
        {
            if (!peer.SendLifetime.TryEnter()) continue;
            string? error = null;
            try
            {
                if (peer.Connection.connectionState != RTCPeerConnectionState.connected) continue;
                if (peer.FirstSequence == 0) { peer.FirstSequence = frame.Sequence; peer.FirstSampleOffset = frame.SampleOffset; peer.TimestampBase = peer.Connection.AudioLocalTrack.Timestamp; }
                var timestamp = unchecked(peer.TimestampBase + (uint)(frame.SampleOffset - peer.FirstSampleOffset));
                var sentAt = ProbeClock.Now;
                if (frame.TraceHash is { } traceHash) TraceSentFrame(traceHash, sentAt);
                peer.Connection.SendRtpRaw(SDPMediaTypesEnum.audio, frame.Payload, timestamp,
                    peer.LastSentSequence == 0 || frame.Sequence != peer.LastSentSequence + 1 ? 1 : 0, peer.PayloadTypeId);
                if (pipeline.Enabled) pipeline.Sent(new(peer.Connection.AudioLocalTrack.Ssrc, timestamp, frame.Sequence,
                    frame.Created / (double)System.Diagnostics.Stopwatch.Frequency, dequeuedAt, sentAt, ProbeClock.Now, frame.Origin, frame.Samples, frame.SampleOffset));
                peer.LastSentSequence = frame.Sequence; peer.Metrics.Sent(frame.Payload.Length);
            }
            catch (Exception ex) { error = peer.Name + ": 음성 송신 오류: " + ex.Message; }
            finally { peer.SendLifetime.Exit(); }
            // User callbacks must not hold a send lease needed by peer shutdown.
            if (error is not null) Status?.Invoke(error);
        }
    }
    private static async Task ClosePeerAsync(Peer peer, string reason)
    {
        await peer.SendLifetime.StopAsync();
        try { peer.Connection.Close(reason); }
        finally
        {
            try { await peer.Sink.Close(); }
            finally { peer.Decoder.Dispose(); }
        }
    }
    private void TraceSentFrame(string hash, double at) =>
        microphone?.Timeline.Update(hash, old => old with { SentAt = at });
    private static string FormatEndpoint(RTCIceCandidate candidate) =>
        candidate.address.Contains(':') ? $"[{candidate.address}]:{candidate.port}" : $"{candidate.address}:{candidate.port}";
    private static string AddressFamily(string address) => System.Net.IPAddress.TryParse(address, out var parsed)
        ? parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4" : "hostname";
}
