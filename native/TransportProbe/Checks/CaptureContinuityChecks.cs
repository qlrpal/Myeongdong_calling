using VoiceNative;
using System.Runtime.InteropServices;

internal static class CaptureContinuityChecks
{
    public static async Task RunAsync()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var empty = new CaptureContinuityTracker();
        empty.Observe(0, 480, 0, 0, 10_000_000, 0);
        empty.Observe(.001, 0, 0, 0, 0, 0);
        empty.Observe(.01, 480, 0, 480, 10_100_000, 0);
        Check(empty.Snapshot() is { ObservedPackets: 2, ComparablePositionBreaks: 0, RecentEvents.Length: 0 },
            "Empty notification invalidated a valid packet position or fabricated a glitch.");
        var tracker = new CaptureContinuityTracker();
        tracker.Observe(0, 256, 0, 0, 10_000_000, 0);
        tracker.Observe(.01, 224, 0, 256, 10_053_333, 0);
        tracker.Observe(.02, 256, 1, 480, 10_100_000, 128);
        tracker.Observe(.03, 256, 1, 2000, 10_153_333, 64);
        tracker.Observe(.04, 256, 0, 2256, 10_206_666, 0);
        tracker.Observe(.05, 256, 0, 2520, 10_260_000, 0);
        var a = tracker.Snapshot();
        Check(a.AppDiscardedSamples == 192 && a.AppDiscardedMs == 4 && a.ComparablePositionBreaks == 1,
            "Known app discard was conflated with raw device jumps.");
        Check(a.RecentEvents is [{ PositionComparable: false, RawPositionDeltaSamples: 0 },
            { PositionComparable: false, RawPositionDeltaSamples: 1264 },
            { PositionComparable: true, RawPositionDeltaSamples: 8 }], "Discontinuity epoch validity was lost.");
        tracker.Observe(.06, 256, 4, 99999, 10_300_000, 0);
        tracker.Observe(.07, 256, 0, 42, 10_350_000, 0);
        tracker.Observe(.08, 256, 0, 298, 10_400_000, 0);
        Check(tracker.Snapshot().ComparablePositionBreaks == 1, "TimestampError reused an invalid position anchor.");
        for (var i = 0; i < 40; i++) tracker.Observe(i + 1, 256, 1, i * 256, 20_000_000 + i * 53333, 0);
        var bounded = tracker.Snapshot();
        Check(bounded.RecentEvents.Length == 16 && bounded.OverwrittenEvents == 28, "Input event history is not bounded.");
        for (var i = 0; i < 1000; i++) tracker.Observe(i, 256, 1, i * 256, 30_000_000 + i * 53333, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) tracker.Observe(i, 256, 1, i * 256, 30_000_000 + i * 53333, 0);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Input packet observation allocated on the audio path.");

        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        var frames = new List<short[]>(); var errors = new List<string>();
        var device = new WindowsAudioDevice(encoder, disableSource: true, disableSink: true, frameMs: 10)
        { DiagnosticCaptureTransform = pcm => frames.Add(pcm.ToArray()) };
        device.OnAudioSourceError += errors.Add;
        static byte[] Pcm(int count, short value) => MemoryMarshal.AsBytes(Enumerable.Repeat(value, count).ToArray().AsSpan()).ToArray();
        try
        {
            device.InjectNativePacketForChecks(Pcm(256, 11), 0, 0, 10_000_000);
            device.InjectNativePacketForChecks(Pcm(256, 22), 1, 256, 10_053_333);
            device.InjectNativePacketForChecks(Pcm(224, 33), 0, 512, 10_106_666);
            device.InjectNativePacketForChecks(Pcm(480, 44), 0, 736, 10_153_333);
            device.InjectNativePacketForChecks(Pcm(480, 55), 1, 1216, 10_253_333);
            var snapshot = device.Snapshot();
            Check(errors.Count == 0 && frames.Count == 3, "Native input fixture failed to encode complete frames.");
            Check(frames[0].Take(256).All(v => v == 22) && frames[0].Skip(256).All(v => v == 33)
                && frames[1].All(v => v == 44) && frames[2].All(v => v == 55), "Existing discontinuity reset/PCM assembly changed.");
            Check(snapshot.CaptureDiscontinuities == 2 && snapshot.CaptureContinuity is
                { ObservedPackets: 5, AppDiscardedSamples: 256, ComparablePositionBreaks: 0 },
                "Exact partial PCM discard or zero-partial reset accounting failed.");
        }
        finally { await device.Close(); }
        Console.WriteLine("PASS: input position epochs/timestamp errors/raw jumps, bounded history, 0-byte observation and exact partial PCM reset accounting; original PCM assembly preserved.");
    }
}
