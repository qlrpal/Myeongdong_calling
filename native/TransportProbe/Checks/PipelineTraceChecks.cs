using VoiceNative;
using System.Buffers.Binary;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

internal static class PipelineTraceChecks
{
    public static void Run()
    {
        var limited = new PipelineTrace(2); limited.Start(5);
        for (var i = 0; i < 3; i++) limited.Sent(new(1, (uint)i, i, 1, 2, 3, 4, null));
        var bounded = limited.Snapshot();
        if (bounded.Sends.Length != 2 || bounded.Sends[0].Timestamp != 1 || bounded.EvictedRecords != 1)
            throw new InvalidOperationException("Pipeline trace must expose eviction and retain bounded metadata.");
        limited.Stop(); limited.Sent(new(1, 99, 99, 1, 2, 3, 4, null));
        if (limited.Snapshot().Sends.Length != 2) throw new InvalidOperationException("Stopped trace accepted records.");
        limited.Start(5);
        if (limited.Snapshot().Sends.Length != 0 || limited.Snapshot().EvictedRecords != 0) throw new InvalidOperationException("Trace restart retained previous session.");
        var raw = new PipelineTrace(2); raw.Start(5);
        var header = new byte[12]; header[0] = 0x80; header[1] = 0xef;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 65535);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x10203040);
        raw.ObserveRtpChannel("test", header, 111, 8192);
        header[0] = 0; raw.ObserveRtpChannel("test", header, 111, 8192);
        header[0] = 0x8f; raw.ObserveRtpChannel("test", header, 111, 8192);
        header[0] = 0x80; header[1] = 200; raw.ObserveRtpChannel("test", header, 111, 8192);
        raw.ObserveRtpChannel("test", header.AsSpan(0, 8), 111, 8192);
        var observed = raw.Snapshot().RtpChannel!;
        if (observed.Length != 1 || observed[0].Ssrc != 0x10203040 || observed[0].Timestamp != uint.MaxValue
            || observed[0].Sequence != 65535 || observed[0].DatagramBytes != 12 || observed[0].ReceiveBufferBytes != 8192)
            throw new InvalidOperationException("RTP channel observer corrupted header identity or accepted non-RTP/short/CSRC metadata");
        raw.Stop(); header[1] = 111; raw.ObserveRtpChannel("test", header, 111, 8192);
        if (raw.Snapshot().RtpChannel!.Length != 1) throw new InvalidOperationException("Stopped observer accepted RTP");
        raw.Start(5);
        if (raw.Snapshot().RtpChannel!.Length != 0) throw new InvalidOperationException("Observer retained previous session");
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var payloads = Enumerable.Range(0, 3).Select(frame => encoder.EncodeAudio(
            Enumerable.Range(0, 480).Select(i => (short)(3000 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 440 / 48000))).ToArray(), format)).ToArray();
        var trace = new PipelineTrace(); trace.Start(10);
        using var recorded = new RtpOpusPlayoutBuffer(new(), adaptiveReserve: false, pipeline: trace, tracePeer: "test");
        using var plain = new RtpOpusPlayoutBuffer(new(), adaptiveReserve: false);
        for (var i = 0; i < 3; i++) { recorded.Add(7, 1000 + (uint)i * 480, payloads[i]); plain.Add(7, 1000 + (uint)i * 480, payloads[i]); }
        recorded.Add(7, 1480, payloads[1]);
        var left = new byte[960]; var right = new byte[960];
        for (var i = 0; i < 3; i++)
        {
            recorded.Read(left); plain.Read(right);
            if (!left.SequenceEqual(right)) throw new InvalidOperationException("Tracing changed PCM output.");
        }
        recorded.Add(7, 1000, payloads[0]); trace.Stop(); var snapshot = trace.Snapshot();
        if (snapshot.Admissions.Count(a => a.Outcome == "queued") != 3 || snapshot.Admissions.Count(a => a.Outcome == "duplicate") != 1
            || snapshot.Admissions.Count(a => a.Outcome == "late") != 1 || snapshot.Playout.Count(p => p.Outcome == "played") != 3)
            throw new InvalidOperationException("Trace confused admission, duplicates, late drops and actual playout.");
        if (snapshot.Reads.Length != 3 || snapshot.Reads.Any(r => r.ProvidedSamples != 480 || r.ReturnedAt < r.LockAt || r.LockAt < r.CalledAt)
            || snapshot.Playout.Any(p => p.DecodeStart > p.DecodeEnd || p.DecodeEnd > p.SubmittedAt))
            throw new InvalidOperationException("Trace stage chronology or render sample count failed.");
        Console.WriteLine("PASS: bounded opt-in metadata, stop/reset, packet identity/outcome, timing order and unchanged PCM output.");
    }
}
