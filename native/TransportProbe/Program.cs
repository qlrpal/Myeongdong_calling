using System.Text.Json;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

if (args is ["--device-playout-test"])
{
    foreach (var mode in new[] { "WaveOut", "WASAPI" })
    {
        var buffer = new VoiceNative.PcmPlayoutBuffer();
        using NAudio.Wave.IWavePlayer output = mode == "WaveOut"
            ? new NAudio.Wave.WaveOut { DeviceNumber = -1, BufferMilliseconds = 20, NumberOfBuffers = 2 }
            : new NAudio.Wave.WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
        output.Init(buffer);
        var silence = new byte[1920]; buffer.AddSamples(silence); buffer.AddSamples(silence);
        output.Play();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var supplied = 0;
        while (clock.Elapsed.TotalSeconds < 5)
        {
            var expected = (int)(clock.Elapsed.TotalMilliseconds / 20);
            while (supplied < expected) { buffer.AddSamples(silence); supplied++; }
            await Task.Delay(2);
        }
        output.Stop(); var snapshot = buffer.Snapshot();
        Console.WriteLine($"{mode}: elapsed={clock.ElapsedMilliseconds}ms input={snapshot.DecodedMs}ms consumed={snapshot.ConsumedMs}ms requested={snapshot.RequestedMs}ms queue={snapshot.QueueMs}ms trims={snapshot.Trims} underruns={snapshot.Underruns}");
        if (mode == "WASAPI" && (snapshot.Trims > 0 || snapshot.RequestedMs < 4700)) throw new Exception("WASAPI playback is not consuming audio at real-time speed.");
    }
    return;
}

if (args is ["--playout-self-test"])
{
    var buffer = new VoiceNative.PcmPlayoutBuffer();
    var frame = Enumerable.Repeat((byte)1, 1920).ToArray();
    var output = new byte[1920];
    buffer.AddSamples(frame); buffer.Read(output);
    if (output.Any(x => x != 0)) throw new Exception("Startup reserve was not respected.");
    buffer.AddSamples(frame); buffer.Read(output);
    if (output.Any(x => x != 1)) throw new Exception("Buffered audio was not played.");
    for (var i = 0; i < 500; i++) { buffer.AddSamples(frame); buffer.Read(output); }
    var steady = buffer.Snapshot();
    if (steady.Trims != 0 || steady.Underruns != 0 || steady.QueueMs != 20) throw new Exception("Steady playback must not discard audio.");
    for (var i = 0; i < 5; i++) buffer.AddSamples(frame); // 120ms burst, old code cleared >80ms.
    if (buffer.Snapshot().Trims != 0) throw new Exception("Ordinary bursts must not clear playback.");
    for (var i = 0; i < 20; i++) buffer.AddSamples(frame);
    if (buffer.Snapshot().QueueMs is < 120 or > 240 || buffer.Snapshot().Trims == 0) throw new Exception("Large backlog must retain playable audio.");
    for (var i = 0; i < 20; i++) buffer.Read(output);
    if (buffer.Snapshot().Underruns != 1) throw new Exception("Underflow should be counted once before refilling.");
    Console.WriteLine("PASS: startup reserve, 10s continuous playback, burst preservation, bounded backlog and underflow recovery.");
    return;
}

if (args is ["--metrics-self-test"])
{
    var metrics = new VoiceNative.PeerMetrics();
    var first = metrics.Sample(0);
    if (first.TxKbps is not null || first.RxKbps is not null || first.Jitter is not null || first.Loss is not null)
        throw new Exception("Unmeasured diagnostics must remain null.");
    metrics.Sent(1000);
    metrics.Received(1, uint.MaxValue - 959, 1000, 0);
    metrics.Received(1, 0, 1000, 0.020); // RTP timestamp wrap, perfect 20ms spacing.
    var second = metrics.Sample(2);
    if (second.TxKbps != 4 || second.RxKbps != 8 || second.Jitter != 0)
        throw new Exception("Interval rates or RTP timestamp wrap are incorrect.");
    metrics.Received(1, 960, 1000, 0.050); // 10ms arrival variation => RFC jitter update 10/16.
    var third = metrics.Sample(3);
    if (Math.Abs(third.Jitter!.Value - 0.625) > 0.000001 || third.TxKbps != 0 || third.RxKbps != 8)
        throw new Exception("Jitter or interval reset is incorrect.");
    metrics.Received(2, 900000, 1000, 0.070); // New SSRC must not inherit old jitter.
    if (metrics.Sample(4).Jitter is not null) throw new Exception("SSRC change did not reset jitter.");
    metrics.ReportLoss(64);
    if (metrics.Sample().Loss != 25) throw new Exception("RTCP loss fraction scale is incorrect.");
    await using var engine = new VoiceNative.WebRtcCallEngine();
    var idle = engine.GetDiagnostics();
    if (idle.InCall || idle.Peers.Length != 0 || idle.Microphone is not null) throw new Exception("Idle diagnostics are incorrect.");
    var json = JsonSerializer.Serialize(idle);
    if (!JsonDocument.Parse(json).RootElement.TryGetProperty("Timestamp", out _)) throw new Exception("Snapshot serialization failed.");
    Console.WriteLine("PASS: interval bitrate, missing values, timestamp wrap, jitter, SSRC reset, RTCP loss scale, idle snapshot and JSON export.");
    return;
}

if (args is ["--lan-self-test"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    var successLeft = false;
    var successRight = false;
    await Task.WhenAll(
        VoiceNative.LanUdpProbe.RunAsync("127.0.0.2", message => { if (message.Contains("왕복 응답 성공")) successLeft = true; }, timeout.Token, System.Net.IPAddress.Parse("127.0.0.1")),
        VoiceNative.LanUdpProbe.RunAsync("127.0.0.1", message => { if (message.Contains("왕복 응답 성공")) successRight = true; }, timeout.Token, System.Net.IPAddress.Parse("127.0.0.2")));
    if (!successLeft || !successRight) throw new Exception("LAN probe failed to verify both directions.");
    Console.WriteLine("PASS: LAN UDP diagnostic verified both directions and timed out cleanly.");
    return;
}

// Exercise native ICE + DTLS/SRTP + Opus without opening physical audio devices.
using var encoder = new AudioEncoder(includeOpus: true);
using var decoder = new AudioEncoder(includeOpus: true);
var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
var configuration = new RTCConfiguration();
if (args.Length > 0)
{
    using var http = new HttpClient();
    using var settings = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(args[0]), "api/config")));
    configuration.iceServers = settings.RootElement.GetProperty("iceServers").EnumerateArray()
        .Select(s => new RTCIceServer { urls = s.GetProperty("urls").GetString()! }).ToList();
}
using var left = new RTCPeerConnection(configuration);
using var right = new RTCPeerConnection(configuration);
var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var leftConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rightConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
left.addTrack(new MediaStreamTrack([format], MediaStreamStatusEnum.SendRecv));
right.addTrack(new MediaStreamTrack([format], MediaStreamStatusEnum.SendRecv));
var pendingLeft = new List<RTCIceCandidateInit>();
var pendingRight = new List<RTCIceCandidateInit>();
var sync = new object();
void Forward(RTCIceCandidate candidate, RTCPeerConnection target, List<RTCIceCandidateInit> pending)
{
    if (candidate is null) return;
    Console.WriteLine($"ICE candidate: {candidate.type} {candidate.address}:{candidate.port}");
    var wire = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
    var copy = JsonSerializer.Deserialize<RTCIceCandidateInit>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    lock (sync) { if (target.remoteDescription is null) pending.Add(copy); else target.addIceCandidate(copy); }
}
left.onicecandidate += candidate => Forward(candidate, right, pendingRight);
right.onicecandidate += candidate => Forward(candidate, left, pendingLeft);
left.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) leftConnected.TrySetResult(); };
right.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) rightConnected.TrySetResult(); };
right.OnRtpPacketReceived += (_, media, packet) =>
{
    if (media != SDPMediaTypesEnum.audio) return;
    try
    {
        var pcm = decoder.DecodeAudio(packet.Payload, format);
        if (pcm.Length != 960 || pcm.All(x => x == 0)) throw new Exception("Decoded Opus frame is invalid.");
        received.TrySetResult();
    }
    catch (Exception ex) { received.TrySetException(ex); }
};
try
{
    var offer = left.createOffer(null);
    await left.setLocalDescription(offer);
    lock (sync)
    {
        if (right.setRemoteDescription(offer) != SetDescriptionResultEnum.OK) throw new Exception("Offer rejected.");
        foreach (var candidate in pendingRight) right.addIceCandidate(candidate);
    }
    var answer = right.createAnswer(null);
    await right.setLocalDescription(answer);
    lock (sync)
    {
        if (left.setRemoteDescription(answer) != SetDescriptionResultEnum.OK) throw new Exception("Answer rejected.");
        foreach (var candidate in pendingLeft) left.addIceCandidate(candidate);
    }
    await Task.WhenAll(leftConnected.Task, rightConnected.Task).WaitAsync(TimeSpan.FromSeconds(20));
    var samples = Enumerable.Range(0, 960).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 8000)).ToArray();
    for (var i = 0; i < 10 && !received.Task.IsCompleted; i++)
    {
        left.SendAudio(960, encoder.EncodeAudio(samples, format));
        await Task.Delay(20);
    }
    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: two native peers negotiated Opus, connected via ICE/DTLS, and delivered a decodable 20ms SRTP audio frame.");
}
finally { left.Close("probe complete"); right.Close("probe complete"); }
