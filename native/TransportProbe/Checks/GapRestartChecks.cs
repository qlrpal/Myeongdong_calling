using System.Security.Cryptography;
using VoiceNative;

internal static class GapRestartChecks
{
    public static void Run()
    {
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        var format = encoder.SupportedFormats.Single(f => f.Codec == SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS);
        var payload = encoder.EncodeAudio(Enumerable.Range(0, 480).Select(i => (short)(5000 * Math.Sin(i * 2 * Math.PI * 220 / 48000))).ToArray(), format);
        if (!Steady(payload, false).SequenceEqual(Steady(payload, true)))
            throw new InvalidOperationException("Gap recovery changed uninterrupted PCM.");
        Exercise(payload, 0);
        Exercise(payload, uint.MaxValue - 2000);
        Console.WriteLine("PASS: repeated-gap gating, initial/isolated/reset reserve, pressure expiry/RTP wrap, unchanged steady PCM and 60ms PLC bound.");
    }
    private static void Exercise(byte[] payload, uint origin)
    {
        using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false,
            freshRestart: true, recoveryReserveMs: 10, repeatedGapRecovery: true);
        var output = new byte[960];
        uint next = origin;
        void Add() { buffer.Add(1, next, payload); next = unchecked(next + 480); }
        Add(); Add(); buffer.Read(output);
        if (buffer.Details().DecodedPackets != 0) throw new InvalidOperationException("Initial reserve was reduced.");
        Add();
        for (var episode = 0; episode < 3; episode++)
        {
            Gap(buffer, output);
            next = Expected(buffer, origin);
            var before = buffer.Details().DecodedPackets;
            Add(); Add(); buffer.Read(output);
            if (episode < 2)
            {
                if (buffer.Details().DecodedPackets != before) throw new InvalidOperationException("An isolated gap enabled early restart.");
                Add(); buffer.Read(output);
            }
            else if (buffer.Details().DecodedPackets == before || buffer.Details().ShortReserveRestarts != 1)
                throw new InvalidOperationException("Repeated starvation did not enable short restart.");
        }
        if (buffer.Details().MaxConsecutivePlcMs > 60 || buffer.Details().TargetReserveMs != 20
            || buffer.Details().RecoveryDiscardedMs != 0)
            throw new InvalidOperationException("Recovery changed PLC/reserve/discard bounds.");
        // Four source-clock seconds of normal packets expire the pressure window.
        for (var i = 0; i < 400; i++) { Add(); buffer.Read(output); }
        Gap(buffer, output); next = Expected(buffer, origin);
        var decoded = buffer.Details().DecodedPackets;
        Add(); Add(); buffer.Read(output);
        if (buffer.Details().DecodedPackets != decoded || buffer.Details().ShortReserveRestarts != 1)
            throw new InvalidOperationException("Old recovery pressure did not expire.");
        buffer.Suspend(); buffer.Resume(); next = origin;
        Add(); Add(); buffer.Read(output);
        if (buffer.Details().DecodedPackets != decoded)
            throw new InvalidOperationException("Device reset inherited short startup reserve.");
        try
        {
            using var invalid = new RtpOpusPlayoutBuffer(new(), recoveryReserveMs: 5);
            throw new InvalidOperationException("Invalid recovery reserve was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    }
    private static uint Expected(RtpOpusPlayoutBuffer buffer, uint origin)
        => unchecked(origin + checked((uint)Math.Round((buffer.Snapshot().DecodedMs + buffer.Details().ConcealedMs - buffer.Details().HeldMs) * 48)));
    private static void Gap(RtpOpusPlayoutBuffer buffer, byte[] output)
    {
        var before = buffer.Details().Rebuffers;
        for (var i = 0; i < 30 && buffer.Details().Rebuffers == before; i++) buffer.Read(output);
        if (buffer.Details().Rebuffers != before + 1) throw new InvalidOperationException("Test did not reach bounded PLC starvation.");
    }
    private static byte[] Steady(byte[] payload, bool recovery)
    {
        using var buffer = new RtpOpusPlayoutBuffer(new(), adaptiveReserve: false, fixedReserveClockTarget: true,
            freshRestart: true, recoveryReserveMs: recovery ? 10 : 0, repeatedGapRecovery: recovery);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var output = new byte[960];
        for (uint i = 0; i < 3; i++) buffer.Add(1, i * 480, payload);
        for (uint i = 3; i < 1003; i++) { buffer.Read(output); hash.AppendData(output); buffer.Add(1, i * 480, payload); }
        if (buffer.Details().ConcealedMs != 0 || buffer.Snapshot().UnderfillMs != 0 || buffer.Details().ShortReserveRestarts != 0)
            throw new InvalidOperationException("Steady gap-recovery test lost audio.");
        return hash.GetHashAndReset();
    }
}
