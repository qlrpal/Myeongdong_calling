using System.Runtime.InteropServices;
using NAudio.Wave;

namespace VoiceNative;

internal static class SharedOutputChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    public static async Task RunAsync()
    {
        using (var mixer = new SharedPlaybackMixer())
        {
            var a = new ConstantBuffer(1000); var b = new ConstantBuffer(-200);
            var gain = new PlaybackGain(b);
            await using var sa = mixer.Add(a, a); await using var sb = mixer.Add(gain, b);
            var output = new byte[480]; mixer.Read(output);
            Require(output.All(v => v == 0) && a.Samples == 0 && b.Samples == 0, "Inactive sources advanced");
            sa.Activate(); sb.Activate(); mixer.Read(output);
            Require(MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == 800), "Mix added a queue or wrong sum");
            gain.Gain = 0; mixer.Read(output);
            Require(b.Samples == 480 && MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == 1000), "Mute stopped peer clock");
            await sb.DisposeAsync(); var before = b.Samples; mixer.Read(output);
            Require(b.Samples == before && mixer.SourceCount == 1, "Detached source was read");
            var broken = new ConstantBuffer(99) { ThrowOnRead = true };
            await using var fault = mixer.Add(broken, broken); fault.Activate(); mixer.Read(output);
            Require(fault.Error is not null && MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == 1000), "One faulty peer stopped healthy output");
            broken.ThrowOnRead = false; mixer.Resume(); mixer.Read(output);
            Require(fault.Error is null && mixer.ActiveSources == 2, "Output restart did not reset source error");
        }
        using (var mixer = new SharedPlaybackMixer())
        {
            var a = new ConstantBuffer(30000); var b = new ConstantBuffer(30000);
            await using var sa = mixer.Add(a, a); await using var sb = mixer.Add(b, b);
            sa.Activate(); sb.Activate(); var output = new byte[4800]; mixer.Read(output);
            Require(MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == short.MaxValue) && mixer.ClippedSamples == 2400, "Mix overflow or chunk boundary error");
            a.Value = -30000; b.Value = -30000; mixer.Read(output);
            Require(MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == short.MinValue), "Negative mix overflow");
            b.ShortRead = true; a.Value = 100; b.Value = 200; mixer.Read(output);
            Require(MemoryMarshal.Cast<byte, short>(output).ToArray().All(v => v == 100), "Short peer read leaked stale scratch");
            for (var i = 0; i < 2000; i++) mixer.Read(output);
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5000; i++) mixer.Read(output);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(allocated < 1024, "Steady mixer read allocated managed storage");
            Console.WriteLine($"Mixer 5000 pulls managed allocation: {allocated} bytes (DSP only).");
        }
        using (var mixer = new SharedPlaybackMixer())
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var blocked = new ConstantBuffer(10) { Entered = entered, Release = release };
            var source = mixer.Add(blocked, blocked); source.Activate();
            var rendering = Task.Run(() => mixer.Read(new byte[480]));
            try
            {
                Require(entered.Wait(TimeSpan.FromSeconds(3)), "Read lease fixture did not enter");
                var remove = source.DisposeAsync().AsTask();
                Require(!remove.IsCompleted && mixer.SourceCount == 0, "Detach disposed an in-flight source");
                var other = new ConstantBuffer(20);
                await using var added = mixer.Add(other, other); // No membership lock held during peer Read.
                release.Set(); await Task.WhenAll(remove, rendering).WaitAsync(TimeSpan.FromSeconds(3));
                await source.DisposeAsync();
                Require(!blocked.Disposed, "Mixer took ownership of peer buffer");
            }
            finally { release.Set(); await rendering.WaitAsync(TimeSpan.FromSeconds(3)); }
        }
        IndependentTimelines();
        IndependentTimelines(largePulls: true);
        Console.WriteLine("PASS: shared mix gain/mute, clipping, starvation/fault isolation, zero FIFO, allocation and detach/read lifetime.");
    }

    private static void IndependentTimelines(bool largePulls = false)
    {
        var aOptions = LatencyExperiment.FromName("opus-adaptive")!;
        var bOptions = LatencyExperiment.FromName("opus-adaptive-5")!;
        using var encoderA = new OpusAudioEncoder(OpusProfile.LowDelay64, aOptions);
        using var encoderB = new OpusAudioEncoder(OpusProfile.LowDelay64, bOptions);
        var packetA = encoderA.EncodeAudio(Enumerable.Range(0, 120).Select(i => (short)(2000 * Math.Sin(i * .1))).ToArray(), encoderA.SupportedFormats.Single());
        var packetB = encoderB.EncodeAudio(Enumerable.Range(0, 240).Select(i => (short)(1000 * Math.Sin(i * .2))).ToArray(), encoderB.SupportedFormats.Single());
        using var a = new RtpOpusPlayoutBuffer(new(), experiment: aOptions);
        using var b = new RtpOpusPlayoutBuffer(new(), experiment: bOptions);
        using var referenceA = new RtpOpusPlayoutBuffer(new(), experiment: aOptions);
        using var referenceB = new RtpOpusPlayoutBuffer(new(), experiment: bOptions);
        using var mixer = new SharedPlaybackMixer();
        var sa = mixer.Add(a, a); var sb = mixer.Add(b, b); sa.Activate(); sb.Activate();
        long outputTime = 0; var nextA = 0; var nextB = 0;
        int[] pulls = largePulls ? [120, 240, 480, 144, 1920, 2880] : [120, 240, 480, 144];
        for (var step = 0; outputTime < 4 * 48000; step++)
        {
            while (nextA * 120 <= outputTime * 1.0002)
            {
                // A temporarily has no packets; B keeps its own sender clock.
                if (nextA is < 400 or >= 480)
                { a.Add(1, (uint)(nextA * 120), packetA, outputTime / 48.0); referenceA.Add(1, (uint)(nextA * 120), packetA, outputTime / 48.0); }
                nextA++;
            }
            while (nextB * 240 <= outputTime * .9998)
            { b.Add(2, (uint)(nextB * 240), packetB, outputTime / 48.0); referenceB.Add(2, (uint)(nextB * 240), packetB, outputTime / 48.0); nextB++; }
            var length = pulls[step % pulls.Length] * 2;
            var actual = new byte[length]; var aa = new byte[length]; var bb = new byte[length];
            mixer.Read(actual); referenceA.Read(aa); referenceB.Read(bb);
            var pcm = MemoryMarshal.Cast<byte, short>(actual); var pa = MemoryMarshal.Cast<byte, short>(aa); var pb = MemoryMarshal.Cast<byte, short>(bb);
            for (var i = 0; i < pcm.Length; i++) Require(pcm[i] == Math.Clamp(pa[i] + pb[i], short.MinValue, short.MaxValue), "Mixer altered an independent RTP timeline");
            outputTime += length / 2;
        }
        Require(a.Details().ConcealedMs > 0 && b.Details().DecodedPackets > (largePulls ? 100 : 500),
            $"Independent clock/starvation fixture ineffective: largePulls={largePulls}, A PLC={a.Details().ConcealedMs}, B decoded={b.Details().DecodedPackets}");
        Require(a.Details().DecodedPackets == referenceA.Details().DecodedPackets && b.Details().DecodedPackets == referenceB.Details().DecodedPackets, "Mixer changed peer decode admission");
        Require(a.Details().ConcealedMs == referenceA.Details().ConcealedMs && b.Details().ConcealedMs == referenceB.Details().ConcealedMs, "Mixer changed peer PLC policy");
    }

    private sealed class ConstantBuffer(short value) : IPlayoutBuffer, IDisposable
    {
        public short Value = value;
        public long Samples;
        public bool ShortRead, ThrowOnRead, Disposed;
        public ManualResetEventSlim? Entered, Release;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
        public Action<string, double, bool>? OnFrameSubmitted { get; set; }
        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (ThrowOnRead) throw new InvalidOperationException("Synthetic peer read failure");
            Entered?.Set(); Release?.Wait(TimeSpan.FromSeconds(5));
            if (ShortRead) return 0;
            MemoryMarshal.Cast<byte, short>(buffer).Fill(Value); Samples += buffer.Length / 2;
            return buffer.Length;
        }
        public void ConfigureForOutput(bool lowLatency, int periodMs) { }
        public void Suspend() { }
        public void Resume() { }
        public (double, long, long, double, double, double, double, double) Snapshot() => (0, 0, 0, 0, 0, 0, 0, 0);
        public void Dispose() => Disposed = true;
    }
}
