using System.Text.Json;
using SIPSorcery.Net;
using VoiceNative;

internal static class PacketBoundaryIntegration
{
    public static async Task RunAsync(RTCPeerConnection sender, RTCPeerConnection receiver, int payloadType)
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var payload = encoder.EncodeAudio(Enumerable.Range(0, 240)
            .Select(i => (short)(Math.Sin(i * Math.PI / 24) * 8000)).ToArray(), encoder.SupportedFormats.Single());
        var tx = new PipelineTrace(); var rx = new PipelineTrace(); tx.Start(30); rx.Start(30);
        using var buffer = new RtpOpusPlayoutBuffer(new(), experiment: options, pipeline: rx, tracePeer: "receiver");
        var sync = new object();
        var expected = new Queue<(int Index, uint Stamp)>();
        RTPPacket? held = null;
        var received = 0; var rawReceived = 0; var transmitted = 0; string? error = null;
        const uint basis = 17000000;
        void Observe(System.Net.IPEndPoint endpoint, SDPMediaTypesEnum media, RTPPacket packet)
        {
            if (media != SDPMediaTypesEnum.audio) return;
            lock (sync)
            {
                if (expected.Count == 0) { error = "Unscheduled RTP observation"; return; }
                var item = expected.Dequeue();
                if (packet.Header.Timestamp != item.Stamp) error = "Unexpected wire RTP timestamp";
                // Controlled test gates AFTER SDK decryption, not actual network loss.
                if (item.Index is >= 40 and < 45) { Interlocked.Increment(ref received); return; }
                var at = ProbeClock.Now;
                if (item.Index is not (>= 60 and < 65))
                {
                    if (item.Index == 80) held = packet;
                    else
                    {
                        buffer.Add(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload);
                        if (item.Index == 81 && held is { } saved)
                        { buffer.Add(saved.Header.SyncSource, saved.Header.Timestamp, saved.Payload); held = null; }
                        if (item.Index == 82) buffer.Add(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload);
                    }
                }
                rx.Received(new("receiver", packet.Header.SyncSource, packet.Header.Timestamp,
                    packet.Header.SequenceNumber, at, at, ProbeClock.Now, packet.Payload.Length));
                Interlocked.Increment(ref received);
            }
        }
        var channel = receiver.GetRtpChannel();
        var receiveBufferBytes = channel.RtpSocket.ReceiveBufferSize;
        void ObserveChannel(int port, System.Net.IPEndPoint endpoint, byte[] packet)
        { rx.ObserveRtpChannel("receiver", packet, payloadType, receiveBufferBytes); Interlocked.Increment(ref rawReceived); }
        channel.OnRTPDataReceived += ObserveChannel;
        receiver.OnRtpPacketReceived += Observe;
        try
        {
            for (var index = 0; index < 120; index++)
            {
                if (index is >= 25 and < 30) continue; // Source/API omission; no send boundary record.
                var stamp = basis + (uint)index * 240;
                lock (sync) expected.Enqueue((index, stamp));
                var began = ProbeClock.Now;
                sender.SendRtpRaw(SDPMediaTypesEnum.audio, payload, stamp, 0, payloadType);
                tx.Sent(new(sender.AudioLocalTrack.Ssrc, stamp, index + 1, began, began, began,
                    ProbeClock.Now, null, 240, index * 240));
                transmitted++;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (Volatile.Read(ref received) < transmitted || Volatile.Read(ref rawReceived) < transmitted) await Task.Delay(1, timeout.Token);
                if (error is not null) throw new InvalidOperationException(error);
            }
            tx.Stop(); rx.Stop(); var left = tx.Snapshot(); var right = rx.Snapshot();
            if (received != 115 || right.RtpChannel?.Length != 115 || right.Receives.Length != 110
                || right.Admissions.Count(a => a.Outcome == "queued") != 105 || buffer.Details().InvalidPackets != 0)
                throw new InvalidOperationException("Controlled packet boundary counts failed");
            var directory = Path.GetFullPath("native/.local/reports/packet-boundary-integration");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "sender.json"), JsonSerializer.Serialize(new { pipeline = left }, DiagnosticJson.Readable));
            File.WriteAllText(Path.Combine(directory, "receiver.json"), JsonSerializer.Serialize(new { pipeline = right }, DiagnosticJson.Readable));
            File.WriteAllText(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new
            {
                sourceOmitted = 5, decryptedCallbacks = received, observationGate = 5, admissionGate = 5,
                expectedMissingAtReceiver = 5, expectedSeenWithoutAdmission = 5, expectedApplicationSequenceGaps = 5,
                limit = "Gates are injected after actual local SRTP decryption; not wire loss. No physical device or cross-host latency measurement."
            }, DiagnosticJson.Readable));
            Console.WriteLine("PASS: actual SRTP with controlled observation/admission gates, source omission, reorder/duplicate; wrote paired bounded metadata fixtures.");
        }
        finally { channel.OnRTPDataReceived -= ObserveChannel; receiver.OnRtpPacketReceived -= Observe; tx.Stop(); rx.Stop(); }
    }
}
