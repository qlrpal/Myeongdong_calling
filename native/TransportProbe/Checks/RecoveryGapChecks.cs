using System.Text.Json;

namespace VoiceNative;

// Controlled synthetic arrival schedules, not a replay of one-second hotspot diagnostics.
internal static class RecoveryGapChecks
{
    private sealed record Event(double At, uint Stamp, bool Read);
    private sealed record Result(string Scenario, int FrameMs, int PullMs, string Mode,
        double UnderfillMs, double PlcMs, long Rebuffers, double DiscardMs,
        double? RecoveryToFreshMs, double? SteadySourceAgeMedianMs, double? PostSourceAgeP95Ms);

    public static void Run(string output, bool comparePolicies = false)
    {
        if (comparePolicies)
        {
            var reserve = new AdaptivePlayoutReserve(720, true, responsive: true);
            if (reserve.TargetSamples != 960) throw new InvalidOperationException("Unsafe responsive startup.");
            for (var i = 0; i < 1200; i++) reserve.Advance(480, false);
            if (reserve.TargetSamples != 720 || !reserve.TryHold() || reserve.TargetSamples != 1200 || reserve.TryHold())
                throw new InvalidOperationException("Responsive reserve floor/ceiling failed.");
            reserve.Rebuffer();
            if (reserve.TargetSamples != 960) throw new InvalidOperationException("Unsafe responsive restart.");
            if (!reserve.TryHold() || reserve.TargetSamples != 1440 || reserve.TryHold())
                throw new InvalidOperationException("Responsive reserve ceiling failed.");
            for (var i = 0; i < 1200; i++) reserve.Advance(480, true);
            if (reserve.TargetSamples != 1440) throw new InvalidOperationException("Concealment incorrectly counted as quiet playback.");
            reserve.Reset();
            if (reserve.TargetSamples != 960) throw new InvalidOperationException("Responsive reset failed.");
            var feedback = new PlayoutClockController(720, true, true, responsive: true);
            for (var i = 0; i < 10000; i++)
            {
                feedback.Observe(i < 5000 ? 4800 : 0, 480);
                if (feedback.Rate < .998 - 1e-9 || feedback.Rate > 1.01 + 1e-9)
                    throw new InvalidOperationException("Responsive consumption rate exceeded safety limits.");
            }
            feedback.Reset();
            if (feedback.Rate != 1) throw new InvalidOperationException("Consumption feedback did not reset.");
        }
        var results = new List<Result>();
        foreach (var frameMs in new[] {10,20})
        foreach (var pullMs in new[] {10,22})
        foreach (var scenario in comparePolicies ? new[] {"steady","reorder","burst60","burst80","burst160","burst220","burst400","holes160","holes400","loss400"} : new[] {"steady","reorder","burst160","burst220","burst400","holes160","holes400","loss400"})
        {
            var pair = new List<Result>();
            foreach (var mode in comparePolicies ? new[] {"current","responsive"} : new[] {"current","continuous-tail","bounded-tail"})
            {
                var result = Simulate(scenario, frameMs, pullMs, mode);
                results.Add(result); pair.Add(result);
            }
            if (scenario is "steady" or "reorder" && pair.Where(x => !x.Mode.StartsWith("responsive", StringComparison.Ordinal)).Skip(1).Any(x=>pair[0] != x with {Mode="current"}))
                throw new InvalidOperationException("Restart candidate altered uninterrupted playback.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {
            notes=comparePolicies
                ? "Synthetic LowDelay64 with virtual 20ms transit. Identical events compare fixed20 and responsive15-30 (20ms startup/restart). 60ms PLC bound and fresh restart retained; no new audio discard. Source age is synthetic RTP generation to render request, not physical output. No devices/network; not exact hotspot replay."
                : "Synthetic LowDelay64 PCM/Opus with virtual 20ms transit and one 160/220/400ms arrival disruption. All three modes share identical events. Fixed 20ms reserve, 60ms PLC, fresh restart and transient hold; continuous-tail waits for a contiguous tail on restart, bounded-tail permits only one extra render pull before baseline fallback. Source age is generated PCM timestamp to render request, not physical output. RecoveryToFreshMs=null means no qualifying frame in the one-second observation window. No audio devices/network used. Not exact hotspot replay.", results
        },DiagnosticJson.Readable));
        foreach(var group in results.GroupBy(x=>(x.Scenario,x.Mode)))
            Console.WriteLine($"{group.Key}: underfill={group.Sum(x=>x.UnderfillMs):F1}ms rebuffer={group.Sum(x=>x.Rebuffers)}");
        Console.WriteLine("PASS: normal/reordered playback unchanged; recovery comparisons exported.");
    }

    private static Result Simulate(string scenario, int frameMs, int pullMs, string mode)
    {
        const int start=4000;
        var gap=scenario.EndsWith("60",StringComparison.Ordinal) && !scenario.EndsWith("160",StringComparison.Ordinal)?60:scenario.EndsWith("80",StringComparison.Ordinal)?80:scenario.EndsWith("160",StringComparison.Ordinal)?160:scenario.EndsWith("220",StringComparison.Ordinal)?220:400;
        var end=start+gap;
        var events=new List<Event>();
        using var encoder=new OpusAudioEncoder(OpusProfile.LowDelay64);
        var format=encoder.SupportedFormats.Single(x=>x.Codec==SIPSorceryMedia.Abstractions.AudioCodecsEnum.OPUS);
        var payload=encoder.EncodeAudio(Enumerable.Range(0,frameMs*48).Select(i=>(short)(5000*Math.Sin(i*2*Math.PI*220/48000))).ToArray(),format);
        for(var sent=0;sent<12000;sent+=frameMs)
        {
            double arrival=sent+20;
            if(scenario=="reorder" && sent/frameMs%7==0) arrival+=15;
            if(scenario is not ("steady" or "reorder") && arrival>=start && arrival<end)
            {
                if(scenario=="loss400")continue;
                arrival=end+(sent/frameMs%3)*.1;
                if(scenario.StartsWith("holes",StringComparison.Ordinal) && sent/frameMs%2==1)arrival+=80;
            }
            events.Add(new(arrival,(uint)(sent*48),false));
        }
        for(var at=0;at<12000;at+=pullMs)events.Add(new(at,0,true));
        var trace=new PipelineTrace(); trace.Start(180);
        using var buffer=new RtpOpusPlayoutBuffer(new(),adaptiveReserve:false,pipeline:trace,tracePeer:"gap",
            fixedReserveClockTarget:true,transientEmptyQueueHold:true,freshRestart:true,contiguousRestart:mode is "continuous-tail" or "bounded-tail",boundedRestartWait:mode=="bounded-tail",
            targetReserveMs:mode.StartsWith("responsive",StringComparison.Ordinal)?15:20,
            responsiveReserve:mode.StartsWith("responsive",StringComparison.Ordinal));
        var readTimes=new List<double>(); var destination=new byte[pullMs*96];
        foreach(var e in events.OrderBy(x=>x.At).ThenBy(x=>x.Read))
        {
            if(e.Read){buffer.Read(destination);readTimes.Add(e.At);}
            else buffer.Add(1,e.Stamp,payload,e.At);
            if(buffer.Snapshot().QueueMs>120)throw new InvalidOperationException("Intermediate recovery queue bound failed.");
        }
        trace.Stop(); var recorded=trace.Snapshot();
        if(recorded.EvictedRecords!=0 || recorded.Reads.Length!=readTimes.Count)throw new InvalidOperationException("Incomplete synthetic trace.");
        var locks=recorded.Reads.Select(x=>x.LockAt).ToArray();
        var played=new List<(double At,double Age)>();
        foreach(var frame in recorded.Playout.Where(x=>x.Outcome=="played"))
        {
            var index=Array.BinarySearch(locks,frame.At);if(index<0)index=~index-1;
            if(index<0)throw new InvalidOperationException("No render request for decoded frame.");
            played.Add((readTimes[index],readTimes[index]-frame.Timestamp/48.0));
        }
        var steady=played.Where(x=>x.At>=1000 && x.At<3500).Select(x=>x.Age).Order().ToArray();
        var steadyMedian=Percentile(steady,.5);
        var post=played.Where(x=>x.At>=end && x.At<end+1000).ToArray();
        var fresh=post.Where(x=>x.Age<=(steadyMedian??0)+frameMs).Select(x=>(double?)(x.At-end)).FirstOrDefault();
        var state=buffer.Snapshot();var detail=buffer.Details();
        if(detail.MaxConsecutivePlcMs>60 || state.QueueMs>120)throw new InvalidOperationException("Recovery bounds failed.");
        if(scenario=="steady" && (state.UnderfillMs!=0 || detail.Rebuffers!=0 || detail.ConcealedMs!=0))throw new InvalidOperationException("Steady simulation failed.");
        return new(scenario,frameMs,pullMs,mode,state.UnderfillMs,detail.ConcealedMs,detail.Rebuffers,detail.RecoveryDiscardedMs,fresh,steadyMedian,Percentile(post.Select(x=>x.Age).Order().ToArray(),.95));
    }
    private static double? Percentile(double[] sorted,double p)=>sorted.Length==0?null:sorted[(int)Math.Ceiling(sorted.Length*p)-1];
}
