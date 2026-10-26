using System.Diagnostics;
using System.Text.Json;
using VoiceNative;

// Several real engines per host: shared capture/render, no virtual audio devices.
// This concentrates CPU and device load; it is not six independent computers.
internal static class TwoHostGroupProbe
{
    private sealed record Phase(string Name, int Seconds, int[] Members);
    private sealed record Sample(string Phase, int Slot, CallDiagnostics Call);
    private static readonly Phase[] Phases = [
        new("three", 60, [0,3,4]), new("six", 120, [0,1,2,3,4,5]),
        new("leave-one", 30, [0,1,2,3,5]), new("rejoin", 60, [0,1,2,3,4,5]),
        new("reconnect", 60, [0,1,2,3,4,5]), new("empty", 20, []),
        new("soak-six", 600, [0,1,2,3,4,5])];

    public static async Task RunAsync(string server, string room, string report, string role, string wave, long epoch, bool quick = false, string? mediaAddress = null, bool soakOnly = false, int soakSeconds = 600, string? senderMode = null, bool captureTrace = true)
    {
        if (senderMode is not (null or "baseline" or "inline" or "dedicated")) throw new ArgumentException("Expected baseline/inline/dedicated sender");
        var options = senderMode == "baseline" ? null : LatencyExperiment.FromName("opus-auto-frames")!;
        if (senderMode is "inline" or "dedicated") options = options! with { InlineSend = senderMode == "inline", DedicatedSend = senderMode == "dedicated", AdaptiveFrames = false };
        options?.Validate();
        if (soakSeconds is not (90 or 600)) throw new ArgumentException("Boundary/soak duration must be 90/600s");
        if (role is not ("pc" or "laptop")) throw new ArgumentException("Expected pc/laptop role");
        using var sdkLog = new TurnSignalingLog();
        SIPSorcery.LogFactory.Set(sdkLog);
        var start = DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (start - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(120) || start < DateTimeOffset.UtcNow.AddSeconds(-5))
            throw new ArgumentException("Start must be within the next 120s");
        var slots = role == "pc" ? new[] {0,1,2} : new[] {3,4,5};
        var engines = new Dictionary<int,WebRtcCallEngine>();
        var joined = new HashSet<int>();
        var samples = new List<Sample>(); var events = new List<string>(); var sync = new object();
        var membership = new List<object>(); var phaseResources = new List<object>(); var traces = new Dictionary<int,PipelineTraceSnapshot>();
        var phaseTraces = new Dictionary<string,Dictionary<int,PipelineTraceSnapshot>>();
        var failures = new HashSet<string>(); var cleanupOk = false;
        using var process = Process.GetCurrentProcess();
        var beforeHandles = process.HandleCount; var beforeBytes = process.PrivateMemorySize64;
        var cpuStart = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
        string? error = null;
        async Task Leave(int slot)
        { if (engines.TryGetValue(slot, out var engine)) { await engine.LeaveAsync(); joined.Remove(slot); } }
        async Task Join(int slot)
        {
            if (joined.Contains(slot)) return;
            await engines[slot].JoinAsync(new Uri(server), room, $"group-{role}-{slot}"); joined.Add(slot);
        }
        async Task WaitMembers(int[] members, DateTimeOffset deadline)
        {
            var timer = Stopwatch.StartNew();
            while (true)
            {
                var ready = true;
                foreach (var slot in slots.Where(members.Contains))
                {
                    var d = engines[slot].GetDiagnostics();
                    foreach (var peer in d.Peers) engines[slot].SetPeerVolume(peer.Id, .015f);
                    ready &= d.InCall && d.Peers.Length == members.Length-1 && d.Peers.All(p => p.State == "connected");
                }
                if (ready) return;
                if (timer.Elapsed.TotalSeconds > 40 || DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("Group membership did not converge");
                await Task.Delay(200);
            }
        }
        try
        {
            foreach (var slot in slots)
            {
                var fixture = SpeechFixture.Load(wave);
                var engine = new WebRtcCallEngine { DirectOnly = true, DiagnosticCaptureTransform = fixture.Fill };
                engine.SetMediaInterface(mediaAddress);
                engine.SetPlayoutPolicy(PlayoutPolicy.LowLatency);
                engine.SetLatencyExperiment(options);
                engine.Status += line => { lock(sync) events.Add($"{elapsed.Elapsed.TotalSeconds:F3}s {slot}: {line}"); };
                engines.Add(slot,engine);
            }
            if (start > DateTimeOffset.UtcNow) await Task.Delay(start-DateTimeOffset.UtcNow);
            var phaseStart = start;
            var phases = senderMode is not null && soakOnly ? new[] {new Phase("soak-six",soakSeconds,[0,1,2,3,4,5])} : senderMode == "baseline" ? new[] {new Phase("pair",45,[0,3])} : senderMode is not null ? new[] { new Phase("pair",45,[0,3]), new Phase("three",60,[0,3,4]), new Phase("six",90,[0,1,2,3,4,5]) } : quick ? new[] { new Phase("three", 60, [0,3,4]), new Phase("six", 90, [0,1,2,3,4,5]) } : soakOnly ? new[] { new Phase("soak-six", soakSeconds, [0,1,2,3,4,5]) } : Phases;
            foreach (var phase in phases)
            {
                sdkLog.Phase = phase.Name;
                var deadline = phaseStart.AddSeconds(phase.Seconds);
                foreach (var slot in slots.Where(s=>!phase.Members.Contains(s))) await Leave(slot);
                if (phase.Name == "reconnect")
                {
                    if (slots.Contains(0)) await Leave(0);
                    await WaitMembers([1,2,3,4,5], deadline);
                    var rejoinAt = phaseStart.AddSeconds(10);
                    if (rejoinAt > DateTimeOffset.UtcNow) await Task.Delay(rejoinAt-DateTimeOffset.UtcNow);
                }
                foreach (var slot in slots.Where(phase.Members.Contains)) await Join(slot);
                await WaitMembers(phase.Members,deadline);
                membership.Add(new { phase.Name, at=DateTimeOffset.UtcNow, members=phase.Members });
                Console.WriteLine($"READY {role} {phase.Name} members={phase.Members.Length}");
                process.Refresh(); var phaseCpu = process.TotalProcessorTime;
                var phaseWatch = Stopwatch.StartNew();
                var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                var allocatedBefore = GC.GetTotalAllocatedBytes();
                var pauseBefore = GC.GetTotalPauseDuration();
                var traced = false; var nextProgress = DateTimeOffset.UtcNow.AddSeconds(30);
                while (DateTimeOffset.UtcNow < deadline.AddSeconds(-5))
                {
                    foreach (var slot in joined)
                    {
                        var d=engines[slot].GetDiagnostics();
                        foreach(var peer in d.Peers) engines[slot].SetPeerVolume(peer.Id, .015f);
                        samples.Add(new(phase.Name,slot,d));
                        if (d.Peers.Length!=phase.Members.Length-1 || d.Peers.Any(p=>p.State!="connected"))
                            throw new InvalidOperationException($"{phase.Name}/{slot}: membership changed during stable window");
                        if (d.Microphone?.LastFrameAgeMs is null or > 1000 || d.Peers.Any(p=>p.Audio.DeviceError is not null))
                            throw new InvalidOperationException($"{phase.Name}/{slot}: capture/render failed");
                        if (d.Peers.Any(p=>p.Audio.RtpPlayout is {InvalidPackets:>0}))
                            throw new InvalidOperationException("Invalid decoded media in group");
                        if (d.Peers.Length>1 && d.FrameAdaptation?.AllPeersReady == true)
                            failures.Add("Multi-peer frame control unexpectedly enabled");
                    }
                    if (captureTrace && (phase.Name=="soak-six" || senderMode is not null) && !traced && (deadline-DateTimeOffset.UtcNow).TotalSeconds<=11)
                    { foreach(var engine in engines.Values) engine.StartPipelineTrace(5); traced=true; }
                    if(DateTimeOffset.UtcNow>=nextProgress)
                    { Console.WriteLine($"PROGRESS {role} {phase.Name} remaining={(deadline-DateTimeOffset.UtcNow).TotalSeconds:F0}s"); nextProgress=DateTimeOffset.UtcNow.AddSeconds(30); }
                    await Task.Delay(1000);
                }
                phaseWatch.Stop(); process.Refresh();
                phaseResources.Add(new { phase.Name, seconds=phaseWatch.Elapsed.TotalSeconds,
                    cpuCores=(process.TotalProcessorTime-phaseCpu).TotalSeconds/phaseWatch.Elapsed.TotalSeconds,
                    memoryBytes=process.PrivateMemorySize64,handles=process.HandleCount,threads=process.Threads.Count,
                    allocatedBytes=GC.GetTotalAllocatedBytes()-allocatedBefore,
                    gcPauseMs=(GC.GetTotalPauseDuration()-pauseBefore).TotalMilliseconds,
                    gcCollections=new[] {GC.CollectionCount(0)-collections[0],GC.CollectionCount(1)-collections[1],GC.CollectionCount(2)-collections[2]} });
                if (traced)
                {
                    var recorded = new Dictionary<int,PipelineTraceSnapshot>();
                    foreach(var slot in joined) recorded[slot]=engines[slot].StopPipelineTrace();
                    foreach(var pair in recorded) traces[pair.Key]=pair.Value;
                    if (senderMode is not null) phaseTraces[phase.Name]=recorded;
                }
                if (deadline>DateTimeOffset.UtcNow) await Task.Delay(deadline-DateTimeOffset.UtcNow);
                phaseStart=deadline;
            }
        }
        catch(Exception ex) { error=ex.ToString(); }
        finally
        {
            foreach(var slot in slots)
            {
                try { await Leave(slot); await engines[slot].DisposeAsync(); }
                catch(Exception ex) { failures.Add("Cleanup: "+ex.Message); }
            }
            cleanupOk=engines.Values.All(e=> { var d=e.GetDiagnostics(); return !d.InCall && d.Peers.Length==0 && d.Microphone is null; });
        }
        elapsed.Stop();process.Refresh();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        string[] logged;lock(sync) logged=events.ToArray();
        await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {
            role,epoch,quick,soakOnly,soakSeconds,mediaAddress,senderMode,options,captureTrace,engineBuild=typeof(WebRtcCallEngine).Assembly.ManifestModule.ModuleVersionId,
            sdkBuild=SdkBuildIdentity.Read(),sdkIceLog=sdkLog.Snapshot(),
            opusBackend=OpusBackend.Snapshot(),
            sdkReceiveDiagnostics=SdkReceiveDiagnostics.Snapshot(),
            error,failures,cleanupOk,membership,phaseResources,samples,traces,phaseTraces,events=logged,
            resources=new {cpuCores=(process.TotalProcessorTime-cpuStart).TotalSeconds/elapsed.Elapsed.TotalSeconds,
                beforeHandles,afterHandles=process.HandleCount,beforeBytes,afterBytes=process.PrivateMemorySize64},
            notes="Three real engines on each host, actual shared capture/render with synthetic microphone replacement; per-test-peer gain .015. Host load is concentrated, not six independent PCs. Same-host links do not cross the LAN. Manual engine leave/rejoin is not a real ISP outage. No physical mouth-to-ear measurement."
        },DiagnosticJson.Readable));
        if(error is not null || failures.Count>0 || !cleanupOk) throw new InvalidOperationException(error ?? string.Join(";",failures));
        Console.WriteLine($"FUNCTIONAL PASS {role}: group suite quick={quick}; quality requires report review");
    }
}
