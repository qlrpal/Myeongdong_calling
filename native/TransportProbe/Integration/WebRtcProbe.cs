using System.Text.Json;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

internal static class WebRtcProbe
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    public static async Task RunAsync(string[] args)
    {
        // Exercise native ICE + DTLS/SRTP + Opus without opening physical audio devices.
        var profileProbe = args is ["--opus-profile-integration", _];
        var variableFrameProbe = args is ["--variable-frame-integration"];
        var variableDurations = new System.Collections.Concurrent.ConcurrentQueue<int>();
        string? variableError = null;
        var selectedProfile = profileProbe ? Enum.Parse<VoiceNative.OpusProfile>(args[1]) : variableFrameProbe ? VoiceNative.OpusProfile.LowDelay64 : VoiceNative.OpusProfile.Baseline;
        using var encoder = new VoiceNative.OpusAudioEncoder(selectedProfile);
        using var decoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var configuration = new RTCConfiguration();
        var turnProbe = args is ["--turn-integration", _];
        var rtpPlayoutProbe = args is ["--rtp-playout-integration"];
        var ipv6Probe = args is ["--ipv6-loopback-integration"];
        if (ipv6Probe) configuration.X_BindAddress = System.Net.IPAddress.IPv6Loopback;
        var tenMsProbe = args is ["--ten-ms-rtcp-integration"] || turnProbe || profileProbe || rtpPlayoutProbe || ipv6Probe || variableFrameProbe;
        var probeFrameSamples = tenMsProbe ? 480 : 960;
        var rtcpIntegration = args is ["--rtcp-integration"] || tenMsProbe;
        if (turnProbe || args.Length > 0 && !rtcpIntegration)
        {
            using var http = new HttpClient();
            using var settings = JsonDocument.Parse(await http.GetStringAsync(new Uri(new Uri(turnProbe ? args[1] : args[0]), "api/config")));
            configuration.iceServers = settings.RootElement.GetProperty("iceServers").EnumerateArray()
                .SelectMany(s =>
                {
                    var urls = s.GetProperty("urls");
                    var entries = urls.ValueKind == JsonValueKind.Array ? urls.EnumerateArray().Select(x => x.GetString()!) : [urls.GetString()!];
                    return entries.Select(url => new RTCIceServer
                    {
                        urls = url,
                        username = s.TryGetProperty("username", out var user) ? user.GetString() : null,
                        credential = s.TryGetProperty("credential", out var password) ? password.GetString() : null
                    });
                }).ToList();
            if (turnProbe)
            {
                if (!configuration.iceServers.Any(s => s.urls.StartsWith("turn:", StringComparison.Ordinal))) throw new InvalidOperationException("No TURN server in configuration.");
                configuration.iceTransportPolicy = RTCIceTransportPolicy.relay;
            }
        }
        using var left = new RTCPeerConnection(configuration);
        using var right = new RTCPeerConnection(configuration);
        if (ipv6Probe)
        {
            var index = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .First(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .GetIPProperties().GetIPv6Properties().Index;
            foreach (var peer in new[] { left, right })
            {
                var socket = peer.GetRtpChannel().RtpSocket;
                VoiceNative.MediaInterfaceSelection.Apply(socket, new("::1", index, "loopback test"));
                if ((int)socket.GetSocketOption(System.Net.Sockets.SocketOptionLevel.IPv6, (System.Net.Sockets.SocketOptionName)31)! != index)
                    throw new InvalidOperationException("IPv6 interface socket option mismatch");
            }
        }
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
        var telemetryReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetryEchoed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var telemetryChannel = await left.createDataChannel("voice-latency-v1");
        telemetryChannel.onmessage += (_, _, bytes) => { if (new VoiceNative.TelemetryState().Receive(bytes)) telemetryEchoed.TrySetResult(); };
        right.ondatachannel += channel =>
        {
            channel.onmessage += (_, _, bytes) =>
            {
                if (new VoiceNative.TelemetryState().Receive(bytes)) { telemetryReceived.TrySetResult(); channel.send(System.Text.Encoding.UTF8.GetString(bytes)); }
            };
        };
        var pendingLeft = new List<RTCIceCandidateInit>();
        var pendingRight = new List<RTCIceCandidateInit>();
        var sync = new object();
        void Forward(RTCIceCandidate candidate, RTCPeerConnection target, List<RTCIceCandidateInit> pending)
        {
            if (candidate is null) return;
            Console.WriteLine($"ICE candidate: {candidate.type} {candidate.address}:{candidate.port}");
            var wire = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
            var copy = JsonSerializer.Deserialize<RTCIceCandidateInit>(wire, WebJson)!;
            lock (sync) { if (target.remoteDescription is null) pending.Add(copy); else target.addIceCandidate(copy); }
        }
        left.onicecandidate += candidate => Forward(candidate, right, pendingRight);
        right.onicecandidate += candidate => Forward(candidate, left, pendingLeft);
        left.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) leftConnected.TrySetResult(); };
        right.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) rightConnected.TrySetResult(); };
        right.OnRtpPacketReceived += (_, media, packet) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            if (previousRtpTimestamp is { } previousStamp && unchecked(packet.Header.Timestamp - previousStamp) == (uint)probeFrameSamples * 3) expectedRtpTimestampJump = true;
            previousRtpTimestamp = packet.Header.Timestamp;
            try
            {
                var pcm = decoder.DecodeAudio(packet.Payload, format);
                var expectedSamples = variableFrameProbe ? Concentus.Structs.OpusPacketInfo.GetNumSamples(packet.Payload, 48000) : probeFrameSamples;
                if (pcm.Length != expectedSamples || pcm.All(x => x == 0)) throw new InvalidOperationException("Decoded Opus frame is invalid.");
                if (variableFrameProbe) variableDurations.Enqueue(pcm.Length);
                received.TrySetResult();
            }
            catch (Exception ex) { if (variableFrameProbe) Volatile.Write(ref variableError, ex.ToString()); received.TrySetException(ex); }
        };
        try
        {
            var offer = left.createOffer(null);
            await left.setLocalDescription(offer);
            lock (sync)
            {
                if (right.setRemoteDescription(offer) != SetDescriptionResultEnum.OK) throw new InvalidOperationException("Offer rejected.");
                foreach (var candidate in pendingRight) right.addIceCandidate(candidate);
            }
            var answer = right.createAnswer(null);
            await right.setLocalDescription(answer);
            lock (sync)
            {
                if (left.setRemoteDescription(answer) != SetDescriptionResultEnum.OK) throw new InvalidOperationException("Answer rejected.");
                foreach (var candidate in pendingLeft) left.addIceCandidate(candidate);
            }
            await Task.WhenAll(leftConnected.Task, rightConnected.Task).WaitAsync(TimeSpan.FromSeconds(20));
            if (ipv6Probe && new[] { left, right }.Any(p => p.GetRtpChannel().NominatedEntry?.LocalCandidate.address != "::1"))
                throw new InvalidOperationException("IPv6 probe silently selected a different address family");
            if (turnProbe)
            {
                foreach (var peer in new[] { left, right })
                {
                    var route = peer.GetRtpChannel().NominatedEntry;
                    if (route?.LocalCandidate.type != RTCIceCandidateType.relay) throw new InvalidOperationException("TURN-only test did not nominate a relay candidate.");
                    Console.WriteLine($"PASS: selected TURN relay {route.LocalCandidate.address}:{route.LocalCandidate.port} -> {route.RemoteCandidate.address}:{route.RemoteCandidate.port}");
                }
            }
            var samples = Enumerable.Range(0, probeFrameSamples).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 8000)).ToArray();
            var firstTimestamp = left.AudioLocalTrack.Timestamp;
            for (var i = 0; i < 10 && !received.Task.IsCompleted; i++)
            {
                left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(firstTimestamp, 1, i + 1, (uint)probeFrameSamples), 0, format.FormatID);
                await Task.Delay(tenMsProbe ? 10 : 20);
            }
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using (var telemetryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (telemetryChannel.readyState != RTCDataChannelState.open) await Task.Delay(20, telemetryTimeout.Token);
                telemetryChannel.send(JsonSerializer.Serialize(new VoiceNative.LatencyTelemetry(1, 20, 1, 2, 40, 30, 1, "재생")));
                await Task.WhenAll(telemetryReceived.Task, telemetryEchoed.Task).WaitAsync(telemetryTimeout.Token);
                Console.WriteLine("PASS: latency telemetry exchanged bidirectionally over the native WebRTC data channel alongside Opus.");
            }
            var timestampBase = previousRtpTimestamp!.Value;
            left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(timestampBase, 1, 4, (uint)probeFrameSamples), 1, format.FormatID);
            for (var attempt = 0; attempt < 50 && !expectedRtpTimestampJump; attempt++) await Task.Delay(10);
            if (!expectedRtpTimestampJump) throw new InvalidOperationException("RTP timestamp failed to preserve the two-frame skipped interval.");
            if (rtcpIntegration)
            {
                using var secondEncoder = new AudioEncoder(includeOpus: true);
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                var sequence = 5L;
                while (deadline.Elapsed.TotalSeconds < 20)
                {
                    left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), VoiceNative.AudioSendQueue.Timestamp(timestampBase, 1, sequence++, (uint)probeFrameSamples), 0, format.FormatID);
                    right.SendAudio((uint)probeFrameSamples, secondEncoder.EncodeAudio(samples, format));
                    await Task.Delay(tenMsProbe ? 10 : 20);
                    if (leftRtcp.Snapshot() is { RtcpRttMs: not null, ReceiveLossPercent: not null } && rightRtcp.Snapshot() is { RtcpRttMs: not null, ReceiveLossPercent: not null }) break;
                }
                var lhs = leftRtcp.Snapshot(); var rhs = rightRtcp.Snapshot();
                if (lhs.RtcpRttMs is null || rhs.RtcpRttMs is null || lhs.ReceiveLossPercent is null || rhs.ReceiveLossPercent is null)
                    throw new InvalidOperationException("Native RTCP integration did not produce RTT and inbound loss on both sides.");
                Console.WriteLine($"PASS: live native RTCP correlation; left RTT={lhs.RtcpRttMs:F2}ms inbound loss={lhs.ReceiveLossPercent:F2}%; right RTT={rhs.RtcpRttMs:F2}ms inbound loss={rhs.ReceiveLossPercent:F2}%.");
            }
            Console.WriteLine($"PASS: two native peers negotiated Opus, connected via ICE/DTLS, and delivered a decodable {probeFrameSamples / 48}ms SRTP audio frame.");
            if (variableFrameProbe)
            {
                var before120 = variableDurations.Count(n => n == 120);
                var before240 = variableDurations.Count(n => n == 240);
                var before480 = variableDurations.Count(n => n == 480);
                var stamp = unchecked(previousRtpTimestamp!.Value + 480);
                for (var i = 0; i < 24; i++)
                {
                    var size = i % 3 == 0 ? 120 : i % 3 == 1 ? 240 : 480;
                    var pcm = Enumerable.Range(0, size).Select(n => (short)(8000 * Math.Sin(n * Math.PI * 880 / 48000))).ToArray();
                    left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(pcm, format), stamp, 0, format.FormatID);
                    stamp = unchecked(stamp + (uint)size);
                    await Task.Delay(10);
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (variableDurations.Count(n => n == 120) < before120 + 8
                    || variableDurations.Count(n => n == 240) < before240 + 8
                    || variableDurations.Count(n => n == 480) < before480 + 8)
                { if (Volatile.Read(ref variableError) is { } error) throw new InvalidOperationException(error); await Task.Delay(10, deadline.Token); }
                if (Volatile.Read(ref variableError) is { } failure) throw new InvalidOperationException(failure);
                Console.WriteLine("PASS: live SRTP stream switches 2.5/5/10ms Opus with cumulative sample timestamps and decodes every duration.");
            }
            if (rtpPlayoutProbe)
            {
                using var playout = new VoiceNative.RtpOpusPlayoutBuffer(new());
                var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var count = 0;
                RTPPacket? held = null;
                right.OnRtpPacketReceived += (_, media, packet) =>
                {
                    if (media != SDPMediaTypesEnum.audio || arrived.Task.IsCompleted) return;
                    var index = count++;
                    if (index == 0) held = packet;
                    else if (index != 4) playout.Add(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload);
                    if (index == 1 && held is { } first)
                    {
                        playout.Add(first.Header.SyncSource, first.Header.Timestamp, first.Payload);
                        playout.Add(first.Header.SyncSource, first.Header.Timestamp, first.Payload);
                    }
                    if (count == 10) arrived.TrySetResult();
                };
                var start = previousRtpTimestamp!.Value + 480;
                for (var i = 0; i < 10; i++)
                {
                    left.SendRtpRaw(SDPMediaTypesEnum.audio, encoder.EncodeAudio(samples, format), unchecked(start + (uint)i * 480), 0, format.FormatID);
                    await Task.Delay(10);
                }
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var pcm = new byte[960];
                for (var i = 0; i < 10; i++) playout.Read(pcm);
                var state = playout.Details();
                if (state.DecodedPackets != 9 || state.ConcealedMs != 10 || state.DuplicatePackets != 1 || playout.Snapshot().Underruns != 0)
                    throw new InvalidOperationException("SRTP-to-RTP-playout order/loss integration failed.");
                Console.WriteLine("PASS: decrypted SRTP feeds RTP playout; reordered packets, duplicate and a missing 10ms deadline handled without underfill.");
            }
        }
        finally { left.Close("probe complete"); right.Close("probe complete"); }

    }
}
