using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceNative;

internal static partial class RenderLatencyProbe
{
    private readonly record struct PresentationPoint(long Written, double Before, double After, ulong Position, ulong Qpc, ulong Frequency);
    private sealed class PresentationTap : IRenderProbe
    {
        public float[] Pcm { get; } = new float[Rate * (Seconds + 5)];
        public List<PresentationPoint> Points { get; } = new(20000);
        public List<RenderSubmissionObservation> Submissions { get; } = new(20000);
        public int Count { get; private set; }
        public void Submitted(RenderSubmissionObservation observation)
        {
            if (Submissions.Count >= 20000) throw new InvalidOperationException("Presentation submission limit.");
            Submissions.Add(observation);
        }
        public TaskCompletionSource<double> Origin { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Samples(ReadOnlySpan<byte> data, WaveFormat format)
        {
            if (format.SampleRate != Rate || format.Channels is < 1 or > 2 || format.BitsPerSample is not (16 or 32))
                throw new NotSupportedException("Presentation probe requires native 48kHz PCM16/float32 mono/stereo.");
            var frames = data.Length / format.BlockAlign;
            if (Count + frames > Pcm.Length) throw new InvalidOperationException("Presentation PCM limit.");
            for (var i = 0; i < frames; i++)
            {
                double value = 0;
                for (var c = 0; c < format.Channels; c++)
                    value += format.BitsPerSample == 16 ? MemoryMarshal.Cast<byte, short>(data)[i*format.Channels+c] / 32768.0
                        : MemoryMarshal.Cast<byte, float>(data)[i*format.Channels+c];
                Pcm[Count++] = (float)(value / format.Channels);
            }
        }
        public void Clock(long writtenFrames, double before, double after, ulong position, ulong qpc, ulong frequency)
        {
            if (Points.Count >= 20000) throw new InvalidOperationException("Presentation clock limit.");
            Points.Add(new(writtenFrames, before, after, position, qpc, frequency));
            if (frequency > 0 && position > 0 && qpc > 0 && qpc/10000000.0 <= after+.001 && before-qpc/10000000.0 < .1)
                Origin.TrySetResult(qpc/10000000.0-position/(double)frequency);
        }
    }

    // Interpolate only between observed device positions; never extrapolate a future marker.
    private static double? PresentationAt(PresentationPoint[] points, long sample, int rate)
    {
        var seconds = sample / (double)rate;
        var left = points.LastOrDefault(p => p.Position / (double)p.Frequency <= seconds);
        var right = points.FirstOrDefault(p => p.Position / (double)p.Frequency >= seconds);
        if (left.Frequency == 0 || right.Frequency != left.Frequency || right.Position <= left.Position
            || right.Qpc <= left.Qpc || right.After-left.Before > .1) return null;
        var fraction = (seconds-left.Position/(double)left.Frequency) / ((right.Position-left.Position)/(double)left.Frequency);
        return (left.Qpc + fraction*(right.Qpc-left.Qpc)) / 10000000.0;
    }

    private static bool OrderedPresentationClock(PresentationPoint[] points) => points.Length >= 2
        && points.All(p => p.Frequency > 0 && double.IsFinite(p.Before) && double.IsFinite(p.After)
            && p.Before <= p.After && p.After-p.Before <= .005 && p.Qpc/10000000.0 <= p.After+.001
            && p.Before-p.Qpc/10000000.0 < .1 && p.Position/(double)p.Frequency <= p.Written/(double)Rate+.001)
        && points.Zip(points.Skip(1)).All(pair => pair.First.Frequency == pair.Second.Frequency
            && pair.Second.Position >= pair.First.Position && pair.Second.Qpc >= pair.First.Qpc)
        && points[^1].Position > points[0].Position && points[^1].Qpc > points[0].Qpc
        && (points[^1].Position-points[0].Position)/(double)points[0].Frequency
            / ((points[^1].Qpc-points[0].Qpc)/10000000.0) is > .995 and < 1.005;

    private static bool ContinuousPresentation(RenderDiagnostics? render) => render is { EmptyEvents:0 }
        && (render.Execution?.EmptyDuringSupply is null or 0);

    private sealed record PresentationStages(double AdmissionApiMs, double QueueUntilDecodeMs,
        double DecodeAndBookkeepingMs, double DecodedToPcmReadyMs, double PcmReadyToReleaseMs,
        double ReleaseApiMs, double ReleaseReturnToDeviceMs, double TotalMs);

    private sealed record PresentationWait(double UntilFirstReadCallMs, double BetweenReadCallsMs,
        double ReadLockWaitMs, double ReadWorkBeforeDecodeMs, double TotalMs,
        int ObservedReads, double CompletedReadAudioMs);

    // Partition host elapsed time, not causal policy contributions. In particular,
    // gaps outside Read include WASAPI pacing, other render work and scheduling.
    private static PresentationWait? SplitWait(double admitted, double decode, PipelineRead[] reads)
    {
        if (!double.IsFinite(admitted) || !double.IsFinite(decode) || decode < admitted) return null;
        var selected = reads.Where(r => r.CalledAt < decode && r.ReturnedAt > admitted)
            .OrderBy(r => r.CalledAt).ToArray();
        if (selected.Length == 0 || selected.Any(r => !double.IsFinite(r.CalledAt)
                || !double.IsFinite(r.LockAt) || !double.IsFinite(r.ReturnedAt)
                || r.CalledAt > r.LockAt || r.LockAt > r.ReturnedAt)
            || !selected.Zip(selected.Skip(1)).All(p => p.First.ReturnedAt <= p.Second.CalledAt)
            || selected.Count(r => r.LockAt <= decode && decode <= r.ReturnedAt) != 1) return null;
        static double Overlap(double left, double right, double start, double end) =>
            Math.Max(0, Math.Min(right,end)-Math.Max(left,start))*1000;
        var first = Math.Max(0, selected[0].CalledAt-admitted)*1000;
        double locks = 0, work = 0, laterGaps = 0;
        var cursor = Math.Max(admitted, selected[0].CalledAt);
        foreach (var r in selected)
        {
            laterGaps += Math.Max(0, Math.Min(decode,r.CalledAt)-cursor)*1000;
            locks += Overlap(r.CalledAt,r.LockAt,admitted,decode);
            work += Overlap(r.LockAt,r.ReturnedAt,admitted,decode);
            cursor = Math.Max(cursor,Math.Min(decode,r.ReturnedAt));
        }
        laterGaps += Math.Max(0,decode-cursor)*1000;
        var total = (decode-admitted)*1000;
        if (Math.Abs(first+laterGaps+locks+work-total) > 1e-6) return null;
        return new(first,laterGaps,locks,work,total,selected.Length,
            selected.Where(r => r.CalledAt >= admitted && r.ReturnedAt <= decode).Sum(r => r.ProvidedSamples)/48.0);
    }

    private static PresentationStages? Stages(double admitted, double admissionReturned,
        double decodeStart, double decodeEnd, double supplied, RenderSubmissionObservation submission, double presented)
    {
        double[] times = [admitted, admissionReturned, decodeStart, decodeEnd, supplied,
            submission.PcmReadyAt, submission.BeforeRelease, submission.AfterRelease, presented];
        if (times.Any(t => !double.IsFinite(t))
            || !times.Zip(times.Skip(1)).All(pair => pair.First <= pair.Second)) return null;
        return new((admissionReturned-admitted)*1000, (decodeStart-admissionReturned)*1000,
            (decodeEnd-decodeStart)*1000, (submission.PcmReadyAt-decodeEnd)*1000,
            (submission.BeforeRelease-submission.PcmReadyAt)*1000,
            (submission.AfterRelease-submission.BeforeRelease)*1000,
            (presented-submission.AfterRelease)*1000, (presented-admitted)*1000);
    }

    public static void PresentationCheck()
    {
        PresentationPoint[] points = [new(2000, 10.001, 10.002, 480, 100000000, 48000),
            new(2480, 10.011, 10.012, 960, 100100000, 48000)];
        if (Math.Abs(PresentationAt(points, 720, Rate)!.Value-10.005) > 1e-9
            || PresentationAt(points, 100, Rate) is not null || PresentationAt(points, 1200, Rate) is not null)
            throw new InvalidOperationException("Presentation interpolation/extrapolation checks failed.");
        if (!OrderedPresentationClock(points)
            || OrderedPresentationClock([points[1],points[0]])
            || OrderedPresentationClock([points[0],points[1] with { Frequency=0 }])
            || OrderedPresentationClock([points[0],points[1] with { Before=double.NaN }])
            || OrderedPresentationClock([points[0],points[1] with { Qpc=101000000 }]))
            throw new InvalidOperationException("Presentation clock trust checks failed.");
        var uninterrupted = new RenderDiagnostics(true,13,22,0);
        var supplyGap = new RenderExecutionDiagnostics(false,false,true,0,0,0,0,0,1,0,"running");
        if (!ContinuousPresentation(uninterrupted) || ContinuousPresentation(null)
            || ContinuousPresentation(uninterrupted with { EmptyEvents=1 })
            || ContinuousPresentation(uninterrupted with { Execution=supplyGap }))
            throw new InvalidOperationException("A render drain cannot retain a trusted presentation mapping.");
        var tap = new PresentationTap();
        short[] pcm = [16384,-16384,8192,8192];
        tap.Samples(MemoryMarshal.AsBytes(pcm.AsSpan()), new WaveFormat(Rate,16,2));
        float[] floating = [.25f,.75f];
        tap.Samples(MemoryMarshal.AsBytes(floating.AsSpan()), WaveFormat.CreateIeeeFloatWaveFormat(Rate,2));
        if (tap.Count != 3 || tap.Pcm[0] != 0 || tap.Pcm[1] != .25f || tap.Pcm[2] != .5f)
            throw new InvalidOperationException("Renderer PCM sample index/channel conversion failed.");
        using var unstarted = new SharedRenderPlayer(40,true,true);
        try { unstarted.ConfigureProbe(tap,3); throw new InvalidOperationException("Uninitialized probe accepted."); }
        catch (InvalidOperationException error) when (error.Message != "Uninitialized probe accepted.") { }
        var stages = Stages(1,1.001,1.020,1.0201,1.0202,new(480,128,1.0203,1.0204,1.0205),1.030)!;
        if (Math.Abs(stages.TotalMs - (stages.AdmissionApiMs + stages.QueueUntilDecodeMs
                + stages.DecodeAndBookkeepingMs + stages.DecodedToPcmReadyMs + stages.PcmReadyToReleaseMs
                + stages.ReleaseApiMs + stages.ReleaseReturnToDeviceMs)) > 1e-9
            || Stages(1,1.1,1.020,1.0201,1.0202,new(480,128,1.0203,1.0204,1.0205),1.030) is not null
            || Stages(1,1.001,1.020,double.NaN,1.0202,new(480,128,1.0203,1.0204,1.0205),1.030) is not null)
            throw new InvalidOperationException("Presentation stage accounting/order/finite checks failed.");
        PipelineRead Read(double call, double locked, double end) => new("",call,locked,end,128,128,0,20,20,true);
        var wait = SplitWait(1.001,1.007,[Read(1.002,1.003,1.004),Read(1.005,1.006,1.008)])!;
        if (Math.Abs(wait.UntilFirstReadCallMs-1)>1e-8 || Math.Abs(wait.BetweenReadCallsMs-1)>1e-8
            || Math.Abs(wait.ReadLockWaitMs-2)>1e-8 || Math.Abs(wait.ReadWorkBeforeDecodeMs-2)>1e-8
            || SplitWait(1.001,1.003,[Read(1,1.002,1.004)]) is not { UntilFirstReadCallMs:0 }
            || SplitWait(1.001,1.007,[]) is not null
            || SplitWait(1.001,1.007,[Read(1.002,1.003,1.006),Read(1.005,1.006,1.008)]) is not null
            || SplitWait(double.NaN,1.007,[Read(1.002,1.003,1.008)]) is not null)
            throw new InvalidOperationException("Presentation wait partition/clipping/missing/overlap checks failed.");
        Console.WriteLine("PASS: device-position interpolation and missing-bracket rejection.");
    }

    public static async Task PresentationAsync(string output)
    {
        if (File.Exists(output)) throw new IOException("Select a new presentation report path.");
        PresentationCheck();
        var markerFrames = Enumerable.Range(0,14).Select(i => 500+i*200).ToHashSet();
        var plan = BuildRenderPlan(markerFrames, OpusProfile.LowDelay64, false);
        var rounds = new List<object>(); var complete = true;
        async Task Save(bool finished)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { complete=finished && complete, rounds,
                limits="Same-host preencoded RTP admission to bracketed device stream clock, not physical DAC/ear, capture, encoder execution or network. Decoded reference excludes codec content displacement. Submission API calls are timed directly; decode endpoint includes existing trace bookkeeping. PcmReadyToRelease includes observer work. Partial/error rounds are not valid timing. Observer overhead is present. No raw audio saved; product defaults unchanged." }, DiagnosticJson.Readable));
        }
        foreach (var headroom in new[] { 5, 3, 5 })
        {
            using var codec = new OpusAudioEncoder(OpusProfile.LowDelay64);
            var trace = new PipelineTrace(); trace.Start(60);
            var sink = new WindowsAudioDevice(codec, disableSource:true, frameMs:10, useRtpPlayout:true,
                adaptiveReserve:false, fixedReserveClockTarget:true, transientEmptyQueueHold:true, freshRestart:true,
                boundedRender:true, rawRender:true, renderCategory:AudioStreamCategory.GameChat, rtpReserveMs:20, pipeline:trace);
            var tap = new PresentationTap();
            using var cancel = new CancellationTokenSource(); Task? emitter = null;
            try
            {
                sink.ConfigureRenderProbe(tap, headroom);
                var admitted = new Dictionary<int,double>();
                await sink.StartAudioSink();
                var began = await tap.Origin.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var begin = began+1;
                if (ProbeClock.Now > begin-.1) throw new InvalidOperationException("Missed phase-aligned emitter start.");
                emitter = Task.Run(() =>
                {
                    using var timer = new Deadline();
                    for (var i = 0; i < plan.Packets.Length; i++)
                    {
                        timer.Until(begin+i*.01, cancel.Token);
                        if (markerFrames.Contains(i)) admitted.Add(i, ProbeClock.Now);
                        sink.GotRtpAudioPacket(1, (uint)i*Frame, plan.Packets[i]);
                    }
                });
                await emitter; var snapshot = sink.Snapshot();
                await Task.Delay(400); var finalRender = sink.Snapshot().Render; await sink.Close(); trace.Stop();
                var points = tap.Points.Where(p => p.Frequency > 0 && p.Position > 0 && p.Qpc > 0
                    && p.Before <= p.After && p.After-p.Before <= .005
                    && p.Qpc/10000000.0 <= p.After+.001 && p.Before-p.Qpc/10000000.0 < .1).ToArray();
                var mappingValid = ContinuousPresentation(finalRender);
                var ordered = mappingValid && points.Length >= 100 && OrderedPresentationClock(points) && tap.Points[^1].Written == tap.Count;
                var boundaries = trace.Snapshot();
                var measurements = new List<object>(); var accepted = 0; var stagesAccepted = 0; var waitAccepted = 0;
                foreach (var frame in markerFrames.Order())
                {
                    var center = (int)((admitted[frame]-began)*Rate);
                    var start = Math.Max(0,center-Rate/3); var end = Math.Min(tap.Count,center+Rate/3);
                    var match = Find(tap.Pcm.AsSpan(start,end-start), plan.Reference.AsSpan(frame*Frame,2048));
                    var sample = start+match.Offset;
                    var at = ordered ? PresentationAt(points, sample, Rate) : null;
                    var valid = match.Correlation >= .7 && at is not null && at >= admitted[frame] && at-admitted[frame] < .35;
                    if (valid) accepted++;
                    var timestamp = (uint)(frame * Frame);
                    var entries = boundaries.Admissions.Where(a => a.Ssrc == 1 && a.Timestamp == timestamp && a.Outcome == "queued").ToArray();
                    var plays = boundaries.Playout.Where(p => p.Ssrc == 1 && p.Timestamp == timestamp && p.Outcome == "played").ToArray();
                    var commits = tap.Submissions.Where(s => s.FirstFrame <= sample && sample < s.FirstFrame+s.Frames).ToArray();
                    PresentationStages? stages = null;
                    if (valid && entries.Length == 1 && plays.Length == 1 && commits.Length == 1
                        && plays[0].DecodeStart is { } decodeStart && plays[0].DecodeEnd is { } decodeEnd
                        && plays[0].SubmittedAt is { } supplied)
                        stages = Stages(admitted[frame], entries[0].ReturnedAt, decodeStart, decodeEnd, supplied, commits[0], at!.Value);
                    if (stages is not null) stagesAccepted++;
                    PresentationWait? wait = null;
                    if (stages is not null)
                        wait = SplitWait(entries[0].ReturnedAt, plays[0].DecodeStart!.Value, boundaries.Reads);
                    if (wait is not null) waitAccepted++;
                    measurements.Add(new { frame, match.Correlation, sample, admittedAt=admitted[frame],
                        presentationAt=at, valid, admissionToDeviceClockMs=valid ? (at-admitted[frame])*1000 : null,
                        stages, wait,
                        audioAheadAtAdmissionMs=entries.Length == 1 && entries[0].FrameSamples is { } samples
                            ? (double?)(entries[0].QueueMs-samples/48.0) : null,
                        reads=wait is not null ? boundaries.Reads.Where(r => r.CalledAt < plays[0].DecodeStart
                            && r.ReturnedAt > entries[0].ReturnedAt).ToArray() : null,
                        admission=entries.Length == 1 ? entries[0] : null,
                        playout=plays.Length == 1 ? plays[0] : null,
                        submission=commits.Length == 1 ? (RenderSubmissionObservation?)commits[0] : null });
                }
                complete &= accepted >= 10 && stagesAccepted == accepted && waitAccepted == accepted
                    && snapshot.DeviceError is null && boundaries.EvictedRecords == 0;
                var continuityPassed = snapshot.Render?.EmptyEvents == 0 && snapshot.Render.SafetyFallbacks == 0
                    && snapshot.RtpPlayout?.ConcealedMs == 0 && snapshot.RtpPlayout.Rebuffers == 0 && snapshot.UnderfillMs == 0;
                rounds.Add(new { headroomMs=headroom, accepted, stagesAccepted, waitAccepted, ordered, mappingValid, rejectedClockPoints=tap.Points.Count-points.Length,
                    continuityPassed, emitterOrigin=began, emitterBegin=begin, measurements, snapshot, clockPoints=tap.Points });
                Console.WriteLine($"Presentation headroom {headroom}ms: trusted markers {accepted}/14, staged boundaries {stagesAccepted}/{accepted}, wait partitions {waitAccepted}/{accepted}.");
                await Save(false);
            }
            catch (Exception error)
            {
                complete = false;
                rounds.Add(new { headroomMs=headroom, error=error.ToString(), snapshot=sink.Snapshot(), boundaries=trace.Snapshot() });
                await Save(false);
                throw;
            }
            finally
            {
                // The main await above records emitter failures. Cleanup must not
                // throw the same task exception again and bypass device cleanup.
                cancel.Cancel(); if (emitter is not null) try { await emitter; } catch (Exception) { }
                await sink.Close(); trace.Stop();
            }
        }
        await Save(true);
        if (!complete) throw new InvalidOperationException("Unqualified presentation comparison; inspect rejected clocks/markers.");
    }
}
