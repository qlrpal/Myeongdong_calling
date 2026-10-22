using System.Buffers;

namespace VoiceNative;

internal static class PacketStorageChecks
{
    public static void Run()
    {
        foreach (var mode in new[] { "opus-adaptive", "opus-adaptive-5", "pcm-1" })
        {
            var options = LatencyExperiment.FromName(mode)!;
            using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64, options);
            var payload = encoder.EncodeAudio(Enumerable.Range(0, options.FrameSamples).Select(i => (short)(i * 100)).ToArray(), encoder.SupportedFormats.Single());
            var pool = new TrackingPool();
            var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options, packetPool: pool);
            using var reference = new RtpOpusPlayoutBuffer(new(), correctClock: false, experiment: options);
            var output = new byte[options.FrameSamples * 2]; var expected = new byte[output.Length];
            try
            {
                uint stamp = 0;
                var prime = (int)Math.Ceiling(options.ReserveMs / options.FrameMs) + 1;
                for (var i = 0; i < prime + 20; i++)
                {
                    var mutable = (byte[])payload.Clone();
                    buffer.Add(1, stamp, mutable); reference.Add(1, stamp, payload);
                    Array.Fill(mutable, (byte)0xff); // Caller can reuse its input immediately.
                    stamp += (uint)options.FrameSamples;
                    if (i < prime - 1) continue;
                    buffer.Read(output); reference.Read(expected);
                    Require(output.AsSpan().SequenceEqual(expected), "Packet alias or pooled tail entered decoder");
                }
                buffer.Add(1, stamp, payload);
                var rents = pool.Rents;
                buffer.Add(1, stamp, payload); // Duplicate must not acquire storage.
                Require(pool.Rents == rents, "Duplicate rented storage");
                buffer.Suspend(); Require(pool.Outstanding == 0, "Suspend leaked queued payloads");
                buffer.Resume();
                for (var i = 0; i < 300; i++) buffer.Add(2, (uint)(i * options.FrameSamples), payload);
                Require(buffer.Snapshot().Trims > 0, "Backlog fixture did not trim");
                buffer.Add(3, 0, payload); // SSRC replacement drains previous storage.
                Require(pool.Outstanding == 1, "SSRC change leaked old packets");
                for (var i = 1; i < prime; i++) buffer.Add(3, (uint)(i * options.FrameSamples), payload);
                buffer.Read(output); // Timestamp resync is defined only after playback has a position.
                buffer.Add(3, 48000, payload); // Timestamp jump drains queued packets.
                Require(pool.Outstanding == 1, "Resync leaked old packets");
            }
            finally { buffer.Dispose(); buffer.Dispose(); }
            Require(pool.Outstanding == 0 && pool.Rents == pool.Returns, "Dispose leaked or double-returned payload");
        }
        var malformedPool = new TrackingPool();
        using (var malformed = new RtpOpusPlayoutBuffer(new(), correctClock: false,
            experiment: LatencyExperiment.FromName("opus-zero"), packetPool: malformedPool))
        {
            // Two 2.5ms CELT frames with a truncated VBR length: duration metadata
            // is valid, but the actual decode must fail after storage admission.
            malformed.Add(1, 0, [0x82, 0xff]);
            Require(malformedPool.Rents == 1, "Decode-failure fixture not admitted");
            malformed.Read(new byte[480]);
            Require(malformed.Details().InvalidPackets > 0 && malformedPool.Outstanding == 0,
                "Decoder failure leaked pooled storage");
        }
        Console.WriteLine("PASS: pooled packet length, caller reuse, duplicate, decode, trim, suspend, SSRC, timestamp resync and repeated disposal ownership.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        private readonly HashSet<byte[]> outstanding = new();
        public int Rents { get; private set; }
        public int Returns { get; private set; }
        public int Outstanding => outstanding.Count;
        public override byte[] Rent(int minimumLength)
        {
            var bytes = new byte[minimumLength + 64];
            Array.Fill(bytes, (byte)0xff);
            outstanding.Add(bytes); Rents++;
            return bytes;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Require(outstanding.Remove(array), "Foreign buffer or double return");
            Array.Fill(array, (byte)0xdd); Returns++;
        }
    }
}
