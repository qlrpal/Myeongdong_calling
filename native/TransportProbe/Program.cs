using System.Text.Json;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

try
{
if (args is ["--send-queue-test"])
{
    var queue = new VoiceNative.AudioSendQueue();
    for (var i = 0; i < 6; i++) queue.Enqueue(new byte[] { (byte)i });
    queue.Complete(); var sequences = new List<long>();
    await foreach (var frame in queue.Read(CancellationToken.None)) sequences.Add(frame.Sequence);
    if (!sequences.SequenceEqual(new long[] { 4, 5, 6 }) || queue.Snapshot().DroppedFrames != 3 || queue.Snapshot().PendingFrames != 0)
        throw new Exception("Bounded sender did not retain the newest frames.");
    if (VoiceNative.AudioSendQueue.Timestamp(100, 1, 4) != 2980 || VoiceNative.AudioSendQueue.Timestamp(uint.MaxValue - 959, 1, 2) != 0)
        throw new Exception("Dropped frame timestamp accounting is incorrect.");
    Console.WriteLine("PASS: bounded nonblocking sender, latest-frame retention, drop counters, completion and RTP skip accounting.");
    return;
}
if (args is ["--capture-test"])
{
    using var captureEncoder = new AudioEncoder(includeOpus: true);
    var capture = new VoiceNative.WindowsAudioDevice(captureEncoder, disableSink: true);
    var frames = 0; string? error = null;
    capture.OnAudioSourceEncodedSample += (_, _) => Interlocked.Increment(ref frames);
    capture.OnAudioSourceError += message => error = message;
    try
    {
        await capture.StartAudio();
        for (var i = 0; i < 100; i++) { capture.Snapshot(); await Task.Delay(30); }
        var stats = capture.Snapshot();
        if (error is not null || frames < 100) throw new Exception(error ?? $"Insufficient microphone frames: {frames}");
        Console.WriteLine($"PASS: actual WASAPI microphone generated {frames} Opus frames in 3s; callback P95={stats.CaptureGapP95Ms:F2}ms max={stats.CaptureGapMaxMs:F2}ms. Audio was not saved.");
    }
    finally { await capture.Close(); }
    return;
}

if (args is ["--device-restart-test"])
{
    using var codec = new AudioEncoder(includeOpus: true);
    var device = new VoiceNative.WindowsAudioDevice(codec, disableSource: true);
    using var source = new AudioEncoder(includeOpus: true);
    var opus = source.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
    var silence = source.EncodeAudio(new short[960], opus);
    try
    {
        await device.StartAudioSink();
        for (var i = 0; i < 100; i++)
        {
            if (i == 50) await device.RestartOutputAsync();
            device.GotEncodedMediaFrame(new EncodedAudioFrame(0, opus, 20, silence));
            await Task.Delay(20);
        }
        var state = device.Snapshot();
        if (state.PlaybackState != "재생" || state.DeviceRestarts != 1 || state.QueueResets != 0 || state.DeviceError is not null)
            throw new Exception("Device restart did not restore healthy playback.");
        Console.WriteLine("PASS: actual shared WASAPI output reopened during a silent stream; playback resumed without queue trimming.");
    }
    finally { await device.Close(); }
    return;
}

if (args is ["--buffer-recovery-test"])
{
    var buffer = new VoiceNative.PcmPlayoutBuffer();
    var audio = Enumerable.Repeat((byte)1, 1920).ToArray(); var output = new byte[1920];
    buffer.AddSamples(audio); buffer.AddSamples(audio);
    buffer.Read(output); buffer.Read(output); buffer.Read(output);
    buffer.AddSamples(audio); buffer.Read(output);
    if (output.Any(b => b != 1) || buffer.Snapshot().Underruns != 1 || buffer.Snapshot().QueueMs != 0)
        throw new Exception("Short underflow must not cause another 40ms refill delay.");
    if (buffer.Snapshot().UnderfillMs != 20 || buffer.Snapshot().MaxUnderfillMs != 20) throw new Exception("Underfill duration must exclude startup reserve.");
    buffer.Suspend(); buffer.AddSamples(audio); buffer.Read(output);
    if (output.Any(b => b != 0) || buffer.Snapshot().QueueMs != 0) throw new Exception("Failed device accumulated stale audio.");
    buffer.Resume(); buffer.AddSamples(audio); buffer.Read(output);
    if (output.Any(b => b != 0)) throw new Exception("Device restart did not restore startup reserve.");
    buffer.AddSamples(audio); buffer.Read(output);
    if (output.Any(b => b != 1)) throw new Exception("Device restart did not resume fresh audio.");
    Console.WriteLine("PASS: short underflow recovery, suspended-device discard and fresh restart reserve.");
    return;
}

if (args is ["--precision-self-test"])
{
    var metrics = new VoiceNative.RtcpMetrics();
    metrics.Packet(10, 65535, 0); metrics.Packet(10, 0, 0.02); metrics.Packet(10, 65534, 0.03); metrics.Packet(10, 0, 0.04);
    var order = metrics.Snapshot(1);
    if (order.ReorderedPackets != 1 || order.DuplicatePackets != 1 || order.RtcpRttMs is not null) throw new Exception("Sequence wrap or missing RTT handling failed.");
    metrics.Sender(20, 1234, 10);
    var report = new ReceptionReportSample(20, 64, 2, 100, 480, 1234, 32768); // 0.5s report delay.
    metrics.Remote(report, 10.75);
    var measured = metrics.Snapshot(12);
    if (measured.RtcpRttMs != 250 || measured.RttAgeSeconds != 1.25 || measured.SendLossPercent != 25 || measured.RemoteJitterMs != 10)
        throw new Exception("RTCP RTT, loss or jitter units are incorrect.");
    metrics.Remote(new ReceptionReportSample(99, 255, 100, 100, 0, 1234, 0), 13);
    if (metrics.Snapshot(13).SendLossPercent != 25) throw new Exception("Foreign SSRC report contaminated statistics.");
    metrics.Local(new ReceptionReportSample(10, 32, -1, 100, 0, 0, 0), 14);
    if (metrics.Snapshot(15).ReceiveLossPercent != 12.5 || metrics.Snapshot(15).ReceivePacketsLost != -1) throw new Exception("Inbound loss scale or signed cumulative loss failed.");
    metrics.Packet(11, 5, 16);
    if (metrics.Snapshot(16).ReceiveLossPercent is not null) throw new Exception("Inbound SSRC change did not reset loss.");
    metrics.Sender(21, 9999, 20);
    metrics.Remote(new ReceptionReportSample(21, 0, 0, 1, 0, 9999, 65536), 20.5);
    if (metrics.Snapshot(21).RtcpRttMs is not null) throw new Exception("Negative RTT must not be displayed as zero.");
    var timing = new VoiceNative.ProcessingWindow();
    for (var i = 1; i <= 100; i++) timing.Add(i);
    if (timing.Snapshot().P95 != 95 || timing.Snapshot().Max != 100) throw new Exception("Processing percentile failed.");
    Console.WriteLine("PASS: RTP wrap/reorder/duplicates, SSRC filtering/reset, RTCP RTT delay subtraction, report age, loss direction, negative RTT rejection, P95.");
    return;
}

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
var rtcpIntegration = args is ["--rtcp-integration"];
if (args.Length > 0 && !rtcpIntegration)
{
    using var http = new HttpClient();
    using var settings = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(args[0]), "api/config")));
    configuration.iceServers = settings.RootElement.GetProperty("iceServers").EnumerateArray()
        .Select(s => new RTCIceServer { urls = s.GetProperty("urls").GetString()! }).ToList();
}
using var left = new RTCPeerConnection(configuration);
using var right = new RTCPeerConnection(configuration);
var leftRtcp = new VoiceNative.RtcpMetrics();
var rightRtcp = new VoiceNative.RtcpMetrics();
left.OnSendReport += (media, report) => { if (media == SDPMediaTypesEnum.audio) leftRtcp.Outgoing(report); };
right.OnSendReport += (media, report) => { if (media == SDPMediaTypesEnum.audio) rightRtcp.Outgoing(report); };
left.OnReceiveReport += (_, media, report) => { if (media == SDPMediaTypesEnum.audio) leftRtcp.Incoming(report); };
right.OnReceiveReport += (_, media, report) => { if (media == SDPMediaTypesEnum.audio) rightRtcp.Incoming(report); };
left.OnRtpPacketReceived += (_, media, packet) => { if (media == SDPMediaTypesEnum.audio) leftRtcp.Packet(packet.Header.SyncSource, packet.Header.SequenceNumber); };
right.OnRtpPacketReceived += (_, media, packet) => { if (media == SDPMediaTypesEnum.audio) rightRtcp.Packet(packet.Header.SyncSource, packet.Header.SequenceNumber); };
var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
uint? previousRtpTimestamp = null;
var expectedRtpTimestampJump = false;
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
    if (previousRtpTimestamp is { } previousStamp && unchecked(packet.Header.Timestamp - previousStamp) == 2880) expectedRtpTimestampJump = true;
    previousRtpTimestamp = packet.Header.Timestamp;
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
    var firstTimestamp = left.AudioLocalTrack.Timestamp;
    for (var i = 0; i < 10 && !received.Task.IsCompleted; i++)
    {
        left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(firstTimestamp, 1, i + 1), 0, format.FormatID);
        await Task.Delay(20);
    }
    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var timestampBase = previousRtpTimestamp!.Value;
    left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(timestampBase, 1, 4), 1, format.FormatID);
    for (var attempt = 0; attempt < 50 && !expectedRtpTimestampJump; attempt++) await Task.Delay(10);
    if (!expectedRtpTimestampJump) throw new Exception("RTP timestamp failed to preserve the two-frame skipped interval.");
    if (rtcpIntegration)
    {
        using var secondEncoder = new AudioEncoder(includeOpus: true);
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        var sequence = 5L;
        while (deadline.Elapsed.TotalSeconds < 20)
        {
            left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(timestampBase, 1, sequence++), 0, format.FormatID);
            right.SendAudio(960, secondEncoder.EncodeAudio(samples, format));
            await Task.Delay(20);
            if (leftRtcp.Snapshot() is { RtcpRttMs: not null, ReceiveLossPercent: not null } && rightRtcp.Snapshot() is { RtcpRttMs: not null, ReceiveLossPercent: not null }) break;
        }
        var lhs = leftRtcp.Snapshot(); var rhs = rightRtcp.Snapshot();
        if (lhs.RtcpRttMs is null || rhs.RtcpRttMs is null || lhs.ReceiveLossPercent is null || rhs.ReceiveLossPercent is null)
            throw new Exception("Native RTCP integration did not produce RTT and inbound loss on both sides.");
        Console.WriteLine($"PASS: live native RTCP correlation; left RTT={lhs.RtcpRttMs:F2}ms inbound loss={lhs.ReceiveLossPercent:F2}%; right RTT={rhs.RtcpRttMs:F2}ms inbound loss={rhs.ReceiveLossPercent:F2}%.");
    }
    Console.WriteLine("PASS: two native peers negotiated Opus, connected via ICE/DTLS, and delivered a decodable 20ms SRTP audio frame.");
}
finally { left.Close("probe complete"); right.Close("probe complete"); }

}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error.GetType().Name + ": " + error.Message);
    Console.Error.WriteLine(error.ToString());
    Environment.ExitCode = 1;
}
