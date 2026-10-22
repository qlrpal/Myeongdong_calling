using System.Buffers.Binary;
using SIPSorcery.Net;
using VoiceNative;

// Actual capture assembler -> encoder -> SRTP -> receive buffer -> 10ms render pulls.
// Media time is deterministic; network arrival is awaited before each simulated pull.
internal static class CaptureFrameIntegration
{
    private delegate void CaptureBlock(ReadOnlySpan<byte> data, double? at, double callbackAt, int samples, int flags);
    private static readonly double[] Durations = [2.5, 5, 10, 5, 2.5, 10, 2.5, 5, 10];
    public static async Task RunAsync(RTCPeerConnection left, RTCPeerConnection right, int payloadType)
    {
        var options = LatencyExperiment.FromName("opus-auto-frames")!;
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
        var source = new WindowsAudioDevice(encoder, disableSource: true, disableSink: true, experiment: options);
        using var playout = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
        var capture = typeof(WindowsAudioDevice).GetMethod("Capture", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .CreateDelegate<CaptureBlock>(source);
        var queue = new AudioSendQueue(1);
        var sync = new object();
        var expected = new Queue<(uint Stamp, int Samples)>();
        string? error = null;
        var arrived = 0;
        var sent = 0;
        uint basis = uint.MaxValue - 2400;
        void OnPacket(System.Net.IPEndPoint endpoint, SDPMediaTypesEnum media, RTPPacket packet)
        {
            if (media != SDPMediaTypesEnum.audio) return;
            lock (sync)
            {
                if (expected.Count == 0) { error = "Unexpected media packet"; return; }
                var item = expected.Dequeue();
                var samples = Concentus.Structs.OpusPacketInfo.GetNumSamples(packet.Payload, 48000);
                if (packet.Header.Timestamp != item.Stamp || samples != item.Samples)
                    error = "Capture length/payload duration/RTP sample position mismatch";
                playout.Add(packet.Header.SyncSource, packet.Header.Timestamp, packet.Payload);
                Interlocked.Increment(ref arrived);
            }
        }
        source.OnAudioSourceEncodedSample += (samples, payload) =>
        {
            var frame = queue.CreateFrame(payload, samples: (int)samples);
            var stamp = AudioSendQueue.Timestamp(basis, 0, frame);
            lock (sync) expected.Enqueue((stamp, frame.Samples));
            Interlocked.Increment(ref sent);
            left.SendRtpRaw(SDPMediaTypesEnum.audio, payload, stamp, 0, payloadType);
        };
        var input = new byte[960];
        for (var i = 0; i < 480; i++) BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan(i * 2), (short)(Math.Sin(i * Math.PI / 24) * 8000));
        var output = new byte[960];
        async Task FeedAsync()
        {
            capture(input, null, 0, 480, 0);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (Volatile.Read(ref arrived) != Volatile.Read(ref sent)) await Task.Delay(1, timeout.Token);
            if (error is not null) throw new InvalidOperationException(error);
        }
        right.OnRtpPacketReceived += OnPacket;
        try
        {
            // Prime once; hold the virtual device cadence constant across switches.
            for (var i = 0; i < 4; i++) await FeedAsync();
            playout.Read(output);
            var before = playout.Details(); var beforeAudio = playout.Snapshot();
            foreach (var duration in Durations)
            {
                source.RequestFrameDuration(duration);
                for (var i = 0; i < 300; i++) { await FeedAsync(); playout.Read(output); }
            }
            var after = playout.Details(); var afterAudio = playout.Snapshot();
            if (after.InvalidPackets != 0 || after.LatePackets != 0 || after.ForwardTimestampGapMs != 0
                || after.PacketDurationChanges < 8 || after.LastPacketDurationMs != 10 || after.LastTimestampStepMs != 10
                || after.ConcealedMs != before.ConcealedMs
                || afterAudio.Underruns != beforeAudio.Underruns)
                throw new InvalidOperationException($"Capture/SRTP/playout transition failed: invalid={after.InvalidPackets}, late={after.LatePackets}, PLC={after.ConcealedMs - before.ConcealedMs}, underruns={afterAudio.Underruns - beforeAudio.Underruns}");
            Console.WriteLine($"PASS: 27s media-time actual capture/SRTP/playout, repeated 2.5/5/10ms and RTP wrap; packets={sent}, PLC/underfill=0. Deterministic pulls, not a device/network endurance test.");
        }
        finally { right.OnRtpPacketReceived -= OnPacket; await source.Close(); }
    }
}
