using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

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
    private AudioEncoder? encoder;
    public event Action<string>? Status;
    private sealed record Session(string Id, string Token, RemotePeer[] Peers);
    private sealed record RemotePeer(string Id, string Name);
    private sealed record Peer(RTCPeerConnection Connection, WindowsAudioDevice Sink, AudioEncoder Decoder, string Name, PeerMetrics Metrics);

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
                foreach (var url in entries) iceServers.Add(new RTCIceServer { urls = url,
                    username = item.TryGetProperty("username", out var u) ? u.GetString() : null,
                    credential = item.TryGetProperty("credential", out var p) ? p.GetString() : null });
            }
            Status?.Invoke($"ICE 서버 {iceServers.Count}개 · TURN 중계 " +
                (iceServers.Any(s => s.urls.StartsWith("turn:") || s.urls.StartsWith("turns:")) ? "사용 가능" : "미설정"));
            encoder = new AudioEncoder(includeOpus: true);
            microphone = new WindowsAudioDevice(encoder, disableSink: true);
            microphone.OnAudioSourceError += message => Status?.Invoke("마이크 오류: " + message);
            microphone.OnAudioSourceEncodedSample += (_, bytes) =>
            {
                lock (peers)
                    foreach (var peer in peers.Values)
                        if (peer.Connection.connectionState == RTCPeerConnectionState.connected)
                        {
                            peer.Connection.SendAudio(960, bytes); // 20ms at the Opus 48kHz RTP clock.
                            peer.Metrics.Sent(bytes.Length);
                        }
            };
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
        var sink = new WindowsAudioDevice(decoder, disableSource: true);
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = iceServers });
        var metrics = new PeerMetrics();
        pc.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media == SDPMediaTypesEnum.audio) metrics.Received(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload.Length);
        };
        pc.OnReceiveReport += (_, media, report) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            var samples = report.ReceiverReport?.ReceptionReports ?? report.SenderReport?.ReceptionReports;
            var sample = samples?.FirstOrDefault();
            if (sample is not null) metrics.ReportLoss(sample.FractionLost);
        };
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
        var peer = new Peer(pc, sink, decoder, name, metrics);
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
        try { await Send(id, new { type = "candidate", candidate = new {
            candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex } }); }
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
        var result = connection.setRemoteDescription(new RTCSessionDescriptionInit {
            type = type == "offer" ? RTCSdpType.offer : RTCSdpType.answer, sdp = signal.GetProperty("sdp").GetString() });
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
                if (line.StartsWith("event: ")) kind = line[7..];
                else if (line.StartsWith("data: ")) data += line[6..];
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
            _ = Task.Run(LeaveAsync);
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
            encoder?.Dispose(); encoder = null;
            Peer[] closing;
            lock (peers) { closing = peers.Values.ToArray(); peers.Clear(); }
            foreach (var peer in closing) { peer.Connection.Close("leave"); await peer.Sink.Close(); peer.Decoder.Dispose(); }
            var old = session; session = null;
            if (old is not null) { try { await Post("api/leave", new { old.Id, old.Token }); } catch { } }
            pending.Clear(); departed.Clear();
            cancellation?.Dispose(); cancellation = null; eventLoop = null;
        }
        finally { gate.Release(); }
        Status?.Invoke("통화 종료 · 마이크 해제");
        }
        finally { cleanupGate.Release(); }
    }
    public async ValueTask DisposeAsync() { await LeaveAsync(); http.Dispose(); streamHttp.Dispose(); }

    public CallDiagnostics GetDiagnostics()
    {
        KeyValuePair<string, Peer>[] current;
        lock (peers) current = peers.ToArray();
        var snapshots = current.Select(entry =>
        {
            var peer = entry.Value;
            var sample = peer.Metrics.Sample();
            var nominated = peer.Connection.GetRtpChannel().NominatedEntry;
            return new PeerDiagnostics(entry.Key, peer.Name, peer.Connection.connectionState.ToString(), peer.Connection.iceConnectionState.ToString(),
                nominated is null ? "경로 미선정" : nominated.LocalCandidate.type == RTCIceCandidateType.relay || nominated.RemoteCandidate.type == RTCIceCandidateType.relay ? "TURN 중계" : "직접 연결",
                nominated is null ? null : $"{nominated.LocalCandidate.address}:{nominated.LocalCandidate.port}",
                nominated is null ? null : $"{nominated.RemoteCandidate.address}:{nominated.RemoteCandidate.port}",
                sample.Tx, sample.Rx, sample.TxKbps, sample.RxKbps, sample.Jitter, sample.Loss, sample.Age, peer.Sink.Snapshot());
        }).ToArray();
        return new CallDiagnostics(DateTimeOffset.Now, session is not null, microphone?.Snapshot(), snapshots);
    }
}
