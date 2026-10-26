using VoiceNative;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

internal static class RtpPlayoutChecks
{
    public static void Run()
    {
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var payloads = Enumerable.Range(0, 20).Select(frame => encoder.EncodeAudio(
            Enumerable.Range(0, 480).Select(i => (short)(6000 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 440 / 48000))).ToArray(), format)).ToArray();
        var timeline = new AudioFrameTimeline(); timeline.EnableFor(60);
        using var buffer = new RtpOpusPlayoutBuffer(timeline);
        var submitted = new List<string>();
        buffer.OnFrameSubmitted = (hash, _, discarded) => { if (!discarded) submitted.Add(hash); };
        var output = new byte[960];
        buffer.Read(output);
        Check(buffer.Snapshot().Underruns == 0, "Startup must not count as an underrun.");
        buffer.Add(1, 1480, payloads[1]); buffer.Add(1, 1000, payloads[0]); buffer.Add(1, 1960, payloads[2]);
        buffer.Add(1, 1480, payloads[1]);
        for (var i = 0; i < 3; i++) buffer.Read(output);
        Check(submitted.SequenceEqual(payloads.Take(3).Select(AudioFrameTimeline.Hash)), "RTP reordering failed.");
        buffer.Add(1, 2920, payloads[4]);
        buffer.Read(output);
        Check(output.Any(b => b != 0), "PLC unexpectedly returned silence for a tonal signal.");
        buffer.Read(output); buffer.Add(1, 2440, payloads[3]);
        Check(timeline.Find(AudioFrameTimeline.Hash(payloads[3])) is { Discarded: true }, "Late never-played packets must be marked discarded.");
        var playedHash = AudioFrameTimeline.Hash(payloads[0]);
        buffer.Add(1, 1000, payloads[0]);
        Check(timeline.Find(playedHash) is { Discarded: false, DecodeStart: not null }, "Late duplicates must preserve already-decoded traces.");
        var details = buffer.Details();
        Check(details.DecodedPackets == 4 && details.PlcFrames == 1 && details.ConcealedMs == 10 && details.DuplicatePackets == 1 && details.LatePackets == 2, "Loss/duplicate/deadline accounting failed.");
        Check(buffer.Snapshot().DecodedMs == 40 && buffer.Snapshot().ConsumedMs == 50, "Decoded and concealed duration must remain separate.");

        CheckDemandBoundedHold(payloads);

        // Experimental initial trimming must preserve reserve + current render demand.
        // Default construction keeps the original initial backlog policy.
        using var initialBaseline = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false);
        using var initialFresh = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false, freshInitialStart: true);
        for (var i = 0; i < 10; i++)
        {
            initialBaseline.Add(1, (uint)i * 480, payloads[i]);
            initialFresh.Add(1, (uint)i * 480, payloads[i]);
        }
        initialBaseline.Read(output); initialFresh.Read(output);
        Check(initialBaseline.Snapshot().QueueMs == 90 && initialBaseline.Details().RecoveryDiscardedMs == 0,
            "Default initial backlog policy changed.");
        Check(initialFresh.Snapshot().QueueMs == 20 && initialFresh.Details().RecoveryDiscardedMs == 70
            && initialFresh.Details().ConcealedMs == 0, "Initial trim must count speech omissions, retain reserve and avoid PLC for discarded packets.");

        using var wrap = new RtpOpusPlayoutBuffer(new());
        const uint basis = uint.MaxValue - 959;
        wrap.Add(1, unchecked(basis + 480), payloads[1]); wrap.Add(1, basis, payloads[0]); wrap.Add(1, unchecked(basis + 960), payloads[2]);
        for (var i = 0; i < 10; i++) wrap.Read(output);
        Check(wrap.Details().DecodedPackets == 3 && wrap.Details().ConcealedMs == 60 && wrap.Details().Rebuffers == 1 && wrap.Details().Resyncs == 0, "Timestamp wrap or bounded PLC failed.");
        for (var i = 0; i < 100; i++) wrap.Read(output);
        Check(wrap.Details().ConcealedMs == 60, "PLC must stop after prolonged loss.");
        for (var i = 9; i < 12; i++) wrap.Add(1, unchecked(basis + (uint)i * 480), payloads[i]);
        wrap.Read(output);
        Check(wrap.Details().DecodedPackets == 4, "Playback must recover after rebuffering.");

        using var burst = new RtpOpusPlayoutBuffer(new());
        for (var i = 0; i < 200; i++) burst.Add(1, 1000 + (uint)i * 480, payloads[i % payloads.Length]);
        Check(burst.Snapshot().QueueMs <= 120 && burst.Snapshot().Trims > 0, "Burst backlog exceeded its bound.");
        burst.Add(1, 1000, payloads[0]);
        Check(burst.Details().LatePackets == 1, "Discarded backlog must not re-enter playback.");
        burst.Suspend(); burst.Add(1, 99000, payloads[0]);
        Check(burst.Snapshot().QueueMs == 0, "Suspended output accumulated stale packets.");
        burst.Resume();
        for (var i = 0; i < 3; i++) burst.Add(2, 2000 + (uint)i * 480, payloads[i]);
        burst.Read(output); burst.Add(1, 100000, payloads[0]);
        Check(burst.Details().SourceChanges == 1 && burst.Details().LatePackets == 2, "SSRC replacement accepted stale audio.");
        burst.Add(2, 1000000, payloads[0]); var resyncs = burst.Details().Resyncs;
        burst.Add(2, 2480, payloads[1]);
        Check(burst.Details().Resyncs == resyncs, "Old packets undid a forward discontinuity.");

        using var legacy = new RtpOpusPlayoutBuffer(new());
        var twenty = encoder.EncodeAudio(new short[960], format);
        legacy.Add(1, 100, twenty); legacy.Add(1, 1060, twenty);
        for (var i = 0; i < 4; i++) legacy.Read(output);
        Check(legacy.Snapshot().DecodedMs == 40 && legacy.Details().DecodedPackets == 2, "20ms packet compatibility failed.");
        using var malformed = new RtpOpusPlayoutBuffer(new());
        var random = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var bytes = new byte[random.Next(20)]; random.NextBytes(bytes);
            malformed.Add(1, (uint)i * 480, bytes); malformed.Read(output);
            Check(malformed.Snapshot().QueueMs >= 0, "Malformed input retained a negative PCM length.");
        }
        Check(malformed.Snapshot().QueueMs <= 120, "Malformed packet backlog exceeded its bound.");
        CheckClockAndSamples(payloads);
        CheckAdaptiveReserve(payloads);
        CheckLowDelayLoss();
        Console.WriteLine("PASS: RTP order, PLC/deadlines, duplicates, wrap, bounded loss/backlog, SSRC, recovery, 20ms packets, low-delay CELT loss recovery and malformed input.");
    }
    private static void CheckAdaptiveReserve(byte[][] payloads)
    {
        var reserve = new AdaptivePlayoutReserve(960, true);
        for (var i = 0; i < 512; i++) reserve.Arrival(i * 960, i * 20.0 + 3);
        reserve.Late(); reserve.Late(); reserve.Late();
        Check(reserve.JitterP95Ms == 0 && reserve.TargetSamples == 960, "RTP loss gaps alone must not be measured as jitter.");
        Check(reserve.TryHold() && reserve.TargetSamples == 1440 && reserve.TryHold() && reserve.TargetSamples == 1920 && !reserve.TryHold(),
            "Empty-queue holds must be bounded to two 10ms increases.");
        for (var i = 0; i < 30; i++) reserve.Advance(48000, false);
        Check(reserve.TargetSamples == 1920, "Reserve must not decay before the quiet interval.");
        reserve.Advance(48000, false);
        Check(reserve.TargetSamples == 1872 && !reserve.TryHold(), "A 10ms hold must not fit into only 1ms of remaining headroom.");
        for (var i = 0; i < 25; i++) reserve.Advance(48000, false);
        Check(reserve.TargetSamples == 960, "Stable playout must return to the latency floor.");

        using var waiting = new RtpOpusPlayoutBuffer(new());
        var pcm = new byte[960];
        for (var i = 0; i < 3; i++) waiting.Add(1, (uint)i * 480, payloads[i], i * 10.0);
        for (var i = 0; i < 4; i++) waiting.Read(pcm);
        waiting.Add(1, 1440, payloads[3], 40);
        waiting.Read(pcm);
        Check(waiting.Details() is { HeldMs: 10, DecodedPackets: 4, LatePackets: 0, TargetReserveMs: 30 },
            "A held deadline must accept the original packet exactly once instead of advancing and rejecting it.");
        waiting.Suspend(); waiting.Resume();
        Check(waiting.Details().TargetReserveMs == 20 && waiting.Snapshot().QueueMs == 0, "Device reset must clear adaptive waiting state.");
    }
    private static void CheckDemandBoundedHold(byte[][] payloads)
    {
        foreach(var bounded in new[]{false,true})
        foreach(var correctClock in new[]{false,true})
        {
            using var buffer=new RtpOpusPlayoutBuffer(new(),correctClock:correctClock,adaptiveReserve:false,
                transientEmptyQueueHold:true,demandBoundedHold:bounded);
            var half=new byte[480];
            for(var i=0;i<3;i++)buffer.Add(1,(uint)i*480,payloads[i]);
            for(var i=0;i<6;i++)buffer.Read(half);
            buffer.Read(half);
            Check(buffer.Details().HeldMs==(bounded?5:10),"Short render demand overproduced held PLC");
            buffer.Add(1,1440,payloads[3]);buffer.Read(half);
            Check(buffer.Details().DecodedPackets==(bounded?4:3),"Arriving packet waited behind speculative held PLC");
            Check(buffer.Details().LatePackets==0 && buffer.Details().Rebuffers==0,"Short hold changed deadline/rebuffer behavior");
        }
        using var outage=new RtpOpusPlayoutBuffer(new(),correctClock:false,adaptiveReserve:false,transientEmptyQueueHold:true);
        var output=new byte[480];
        for(var i=0;i<3;i++)outage.Add(1,(uint)i*480,payloads[i]);
        for(var i=0;i<40;i++)outage.Read(output);
        Check(outage.Details() is {HeldMs:10,ConcealedMs:60,Rebuffers:1},"Split holds extended the existing outage budget");
        Console.WriteLine("PASS: demand-sized hold submits arriving RTP 5ms earlier on a 5ms pull without extending the hold/outage budget.");
    }
    private static void CheckClockAndSamples(byte[][] payloads)
    {
        var control = new PlayoutClockController(960, true);
        for (var i = 0; i < 201; i++) control.Observe(960, 480);
        Check(control.Rate == 1, "Nominal queue must not change the consumption rate.");
        for (var i = 0; i < 2000; i++) control.Observe(48000, 480);
        Check(control.Rate is > 1 and <= 1.01, "Backlog catch-up rate exceeded its limit.");
        for (var i = 0; i < 2000; i++) control.Observe(0, 480);
        Check(control.Rate is >= 0.998 and < 1, "Slow-clock correction exceeded its limit.");
        control.Reset(); Check(control.Rate == 1, "Device/timeline resets must reset the clock controller.");
        var learned = new PlayoutClockController(960, true);
        var fixedTarget = new PlayoutClockController(960, true, fixedReserveTarget: true);
        for (var i = 0; i < 200; i++) { learned.Observe(1440, 480); fixedTarget.Observe(1440, 480); }
        Check(learned.TargetSamples == 1440 && fixedTarget.TargetSamples == 960, "Comparison must isolate startup target learning.");
        for (var i = 0; i < 300; i++) { learned.Observe(1440, 480); fixedTarget.Observe(1440, 480); }
        Check(learned.Rate == 1 && fixedTarget.Rate is > 1 and <= 1.01, "Fixed target must drain excess gently without changing the rate bound.");
        fixedTarget.SetReserve(1920); Check(fixedTarget.TargetSamples == 1920, "Fixed target must follow explicit reserve changes.");
        using var normal = new RtpOpusPlayoutBuffer(new(), correctClock: false);
        using var elastic = new RtpOpusPlayoutBuffer(new());
        for (var i = 0; i < 3; i++) { normal.Add(1, (uint)i * 480, payloads[i]); elastic.Add(1, (uint)i * 480, payloads[i]); }
        var plain = new byte[960]; var adjusted = new byte[960]; short previous = 0;
        for (var frame = 0; frame < 100; frame++)
        {
            normal.Read(plain); elastic.Read(adjusted);
            if (frame > 0)
            {
                for (var sample = 0; sample < 480; sample++)
                {
                    var expected = sample == 0 ? previous : System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan((sample - 1) * 2, 2));
                    Check(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(adjusted.AsSpan(sample * 2, 2)) == expected,
                        "Neutral interpolation must preserve PCM across packet and render boundaries with one sample of delay.");
                }
            }
            previous = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(958, 2));
            normal.Add(1, (uint)(frame + 3) * 480, payloads[(frame + 3) % payloads.Length]);
            elastic.Add(1, (uint)(frame + 3) * 480, payloads[(frame + 3) % payloads.Length]);
        }
        Check(elastic.Details().RateAdjustedSamples == 0, "Neutral rate must not drop or repeat samples.");
    }
    private static void CheckLowDelayLoss()
    {
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var packets = Enumerable.Range(0, 5).Select(frame => encoder.EncodeAudio(
            Enumerable.Range(0, 480).Select(i => (short)(6000 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 440 / 48000))).ToArray(), format)).ToArray();
        using var buffer = new RtpOpusPlayoutBuffer(new(), correctClock: false, adaptiveReserve: false);
        foreach (var frame in new[] { 0, 1, 3, 4 }) buffer.Add(1, (uint)frame * 480, packets[frame]);
        var output = new byte[960];
        for (var frame = 0; frame < 5; frame++)
        {
            buffer.Read(output);
            Check(output.Any(value => value != 0), "Low-delay media, including the missing frame, must continue with decoded audio or PLC.");
        }
        Check(buffer.Details() is { DecodedPackets: 4, PlcFrames: 1, ConcealedMs: 10, Rebuffers: 0 }
            && buffer.Snapshot().UnderfillMs == 0, "Low-delay packet loss/recovery accounting failed.");
        buffer.Add(1, 960, packets[2]);
        Check(buffer.Details().LatePackets == 1, "Concealed low-delay packets must not be replayed when late.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public static async Task DeviceAsync()
    {
        using var encoder = new AudioEncoder(includeOpus: true);
        var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
        var sink = new WindowsAudioDevice(encoder, disableSource: true, frameMs: 10, useRtpPlayout: true);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sink.OnAudioSinkError += errors.Enqueue;
        try
        {
            await sink.StartAudioSink();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var frame = 0; frame < 800; frame++)
            {
                // Quiet synthetic source; no microphone or recording is opened.
                var pcm = Enumerable.Range(0, 480).Select(i => (short)(300 * Math.Sin((frame * 480 + i) * 2 * Math.PI * 440 / 48000))).ToArray();
                sink.GotRtpAudioPacket(1, 1000 + (uint)frame * 480, encoder.EncodeAudio(pcm, format));
                while (clock.Elapsed.TotalMilliseconds < (frame + 1) * 10) await Task.Delay(1);
            }
            var state = sink.Snapshot();
            Check(errors.IsEmpty && state.RtpPlayout is { DecodedPackets: > 700 } && state.QueueMs <= 120, "Actual WASAPI RTP playback failed: " + string.Join("; ", errors));
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(state, DiagnosticJson.Readable));
            Console.WriteLine("PASS: actual WASAPI render consumed RTP-driven Opus for eight seconds.");
        }
        finally { await sink.Close(); }
    }
}
