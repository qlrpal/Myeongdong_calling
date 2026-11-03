using System.Text.Json;
using SIPSorceryMedia.Abstractions;
using VoiceNative;

internal static class WarmupTransferChecks
{
    private sealed record Point(double AtMs, double SourceQueueMs, double DevicePaddingMs,
        double TotalInventoryMs, double? FilteredQueueMs, int RequestedSamples, long ConsumedSamples);
    private sealed record Result(int Period, int Capacity, int Target, double MediaStartMs, double PhaseMs,
        bool Warmup, Point Before, Point After, double SourceShiftMs, double PaddingShiftMs,
        double InventoryShiftMs, double MeanSourceMs, double MeanInventoryMs,
        double ConcealedMs, long Rebuffers, double UnderfillMs, long Trims, long DeviceEmpty,
        Point[] Transition);
    public static async Task RunAsync(string output)
    {
        if (File.Exists(output)) throw new IOException("Choose a new warmup report path.");
        using var encoder = new OpusAudioEncoder(OpusProfile.LowDelay64);
        var pcm = Enumerable.Range(0,480).Select(i => (short)(3000*Math.Sin(i*.04))).ToArray();
        var payload = encoder.EncodeAudio(pcm, new AudioFormat(AudioCodecsEnum.OPUS,111,48000,1));
        var rows = new List<Result>();
        foreach (var device in new[] { (Period:128,Capacity:280,Target:280), (Period:256,Capacity:768,Target:400), (Period:480,Capacity:1056,Target:624) })
        foreach (var mediaStart in new[] { 1000d,3000d })
        foreach (var phase in new[] { 0d,2.5,5,7.5 })
        foreach (var warmup in new[] { true,false })
            rows.Add(Evaluate(device.Period,device.Capacity,device.Target,mediaStart,phase,warmup,payload));
        if (rows.Count != 48 || rows.Any(r => r.ConcealedMs != 0 || r.UnderfillMs != 0 || r.Rebuffers != 0 || r.Trims != 0 || r.DeviceEmpty != 0))
            throw new InvalidOperationException("Deterministic regular media manufactured a discontinuity.");
        var active = rows.Where(r => r.Warmup && r.MediaStartMs == 1000 && r.Capacity > r.Target).ToArray();
        if (active.Any(r => Math.Abs(r.SourceShiftMs+r.PaddingShiftMs-r.InventoryShiftMs) > 1e-6))
            throw new InvalidOperationException("Queue inventory accounting mismatch.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new { rows,
            limits="Actual RTP/Opus playout with deterministic device-padding arithmetic and regular arrivals. No actual hardware, CPU, scheduling, network, acoustic or mouth-to-ear model. TotalInventoryMs is queue accounting, not latency. Desired-from-start is a diagnostic control, not a validated product policy. Transition source change includes arrivals and consumption; do not attribute its raw difference solely to warmup." },DiagnosticJson.Readable));
        Console.WriteLine("PASS: 48 warmup/late-media/phase/device-period inventory and continuity conditions.");
    }
    private static Result Evaluate(int period,int capacity,int target,double start,double phase,bool warmup,byte[] payload)
    {
        using var buffer = new RtpOpusPlayoutBuffer(new(),targetReserveMs:20,adaptiveReserve:false,
            fixedReserveClockTarget:true,transientEmptyQueueHold:true,freshRestart:true);
        var bytes = new byte[capacity*2]; var pad = warmup ? capacity : target;
        buffer.Read(bytes.AsSpan(0,pad*2));
        var transition = new List<Point>(); Point? before = null,after = null;
        var nextArrival = start+phase; uint timestamp = 0; long empty = 0;
        double sourceSum = 0,totalSum = 0; var count = 0;
        for (var tick = 1; tick*period/48d < 60000; tick++)
        {
            var at = tick*period/48d;
            while (nextArrival <= at)
            { buffer.Add(7,timestamp,payload,nextArrival); timestamp += 480; nextArrival += 10; }
            pad -= period; if (pad <= 0) { empty++; pad = 0; }
            var desired = warmup && at < 2000 ? capacity : target;
            var read = Math.Max(0,Math.Min(capacity-pad,desired-pad));
            if (read > 0) { buffer.Read(bytes.AsSpan(0,read*2)); pad += read; }
            var s = buffer.Snapshot(); var d = buffer.Details();
            var p = new Point(at,s.QueueMs,pad/48d,s.QueueMs+pad/48d,d.ClockFilteredQueueMs,read,(long)Math.Round(s.ConsumedMs*48));
            if (at is >= 1900 and <= 2100) transition.Add(p);
            if (at < 2000) before = p;
            else after ??= p;
            if (at >= 30000) { sourceSum += s.QueueMs; totalSum += p.TotalInventoryMs; count++; }
        }
        var final = buffer.Snapshot(); var details = buffer.Details();
        return new(period,capacity,target,start,phase,warmup,before!,after!,after!.SourceQueueMs-before!.SourceQueueMs,
            after.DevicePaddingMs-before.DevicePaddingMs,after.TotalInventoryMs-before.TotalInventoryMs,
            sourceSum/count,totalSum/count,details.ConcealedMs,details.Rebuffers,final.UnderfillMs,final.Trims,empty,transition.ToArray());
    }
}
