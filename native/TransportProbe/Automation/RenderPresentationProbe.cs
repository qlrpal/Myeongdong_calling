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
        public int Count { get; private set; }
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
        Console.WriteLine("PASS: device-position interpolation and missing-bracket rejection.");
    }

    public static async Task PresentationAsync(string output)
    {
        if (File.Exists(output)) throw new IOException("Select a new presentation report path.");
        PresentationCheck();
        var markerFrames = Enumerable.Range(0,14).Select(i => 500+i*200).ToHashSet();
        var plan = BuildRenderPlan(markerFrames, OpusProfile.LowDelay64, false);
        var rounds = new List<object>(); var complete = true;
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
                await Task.Delay(400); await sink.Close(); trace.Stop();
                var points = tap.Points.Where(p => p.Frequency > 0 && p.Position > 0 && p.Qpc > 0
                    && p.Before <= p.After && p.After-p.Before <= .005
                    && p.Qpc/10000000.0 <= p.After+.001 && p.Before-p.Qpc/10000000.0 < .1).ToArray();
                var ordered = points.Length >= 100 && OrderedPresentationClock(points) && tap.Points[^1].Written == tap.Count;
                var measurements = new List<object>(); var accepted = 0;
                foreach (var frame in markerFrames.Order())
                {
                    var center = (int)((admitted[frame]-began)*Rate);
                    var start = Math.Max(0,center-Rate/3); var end = Math.Min(tap.Count,center+Rate/3);
                    var match = Find(tap.Pcm.AsSpan(start,end-start), plan.Reference.AsSpan(frame*Frame,2048));
                    var sample = start+match.Offset;
                    var at = ordered ? PresentationAt(points, sample, Rate) : null;
                    var valid = match.Correlation >= .7 && at is not null && at >= admitted[frame] && at-admitted[frame] < .35;
                    if (valid) accepted++;
                    measurements.Add(new { frame, match.Correlation, sample, admittedAt=admitted[frame],
                        presentationAt=at, valid, admissionToDeviceClockMs=valid ? (at-admitted[frame])*1000 : null });
                }
                complete &= accepted >= 10 && snapshot.DeviceError is null && trace.Snapshot().EvictedRecords == 0;
                var continuityPassed = snapshot.Render?.EmptyEvents == 0 && snapshot.Render.SafetyFallbacks == 0
                    && snapshot.RtpPlayout?.ConcealedMs == 0 && snapshot.RtpPlayout.Rebuffers == 0 && snapshot.UnderfillMs == 0;
                rounds.Add(new { headroomMs=headroom, accepted, ordered, rejectedClockPoints=tap.Points.Count-points.Length,
                    continuityPassed, emitterOrigin=began, emitterBegin=begin, measurements, snapshot, clockPoints=tap.Points });
                Console.WriteLine($"Presentation headroom {headroom}ms: trusted markers {accepted}/14.");
            }
            finally
            {
                cancel.Cancel(); if (emitter is not null) try { await emitter; } catch (OperationCanceledException) { }
                await sink.Close(); trace.Stop();
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { complete, rounds,
            limits="Timing complete is separate from continuityPassed. Emitter starts at stream clock origin+1s. Same-host synthetic 10ms native LowDelay64 RTP admission to correlated renderer PCM position mapped by bracketed IAudioClock. Decoded PCM reference excludes codec content displacement. Not physical DAC/ear, microphone or network latency. Explicit observer copies PCM transiently and samples clocks each commit; observer overhead is present in all rounds. No loopback capture timestamp is used. Full clock observations are metadata; audio samples are never saved. Product defaults unchanged." }, DiagnosticJson.Readable));
        if (!complete) throw new InvalidOperationException("Unqualified presentation comparison; inspect rejected clocks/markers.");
    }
}
