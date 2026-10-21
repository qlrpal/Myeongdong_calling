using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Concentus;

namespace VoiceNative;

// Same-host receive-to-digital-render comparison. No microphone or network clock model.
internal static partial class RenderLatencyProbe
{
    private const int Seconds = 32, Rate = 48000, Frame = 480;
    private sealed record Chunk(double At, double CallbackAt, long Position, int Flags, float[] Samples);
    private sealed record Marker(int Frame, double AdmittedAt);
    private sealed record Match(int Offset, double Correlation);
    private sealed class ArraySamples(float[] data) : ISampleProvider
    {
        private int offset;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 1);
        public int Read(Span<float> destination)
        {
            var count = Math.Min(destination.Length, data.Length - offset);
            data.AsSpan(offset, count).CopyTo(destination); offset += count; return count;
        }
    }
    private interface IClockCapture : IDisposable
    {
        int SampleRate { get; }
        string? Error { get; }
        Chunk[] Stop();
    }
    private sealed class Capture : IClockCapture
    {
        private readonly MMDevice endpoint;
        private readonly WasapiRecorder recorder;
        private readonly object sync = new();
        private readonly List<Chunk> chunks = new();
        private int frames;
        public int SampleRate { get; }
        public string Device { get; }
        public string? Error { get; private set; }
        public Capture(string id, bool raw = false, int bufferMs = 20, bool process = false)
        {
            using var enumerator = new MMDeviceEnumerator(); endpoint = enumerator.GetDevice(id);
            Device = endpoint.FriendlyName;
            using var client = endpoint.CreateAudioClient(); var mix = client.MixFormat;
            SampleRate = mix.SampleRate;
            if (mix.BitsPerSample != 32 || !(mix.Encoding == WaveFormatEncoding.IeeeFloat
                || mix is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
                { endpoint.Dispose(); throw new NotSupportedException("Native float loopback is required for this comparison."); }
            try
            {
                var builder = new WasapiRecorderBuilder().WithSharedMode().WithEventSync()
                    .WithBufferLength(bufferMs).WithFormat(mix);
                if (process) builder.WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree);
                else builder.WithDevice(endpoint).WithLoopbackCapture();
                if (raw) builder.WithRawMode();
                recorder = process ? builder.BuildAsync().GetAwaiter().GetResult() : builder.Build();
            }
            catch { endpoint.Dispose(); throw; }
            recorder.RecordingStopped += (_, args) => { if (args.Exception is not null) Error = args.Exception.ToString(); };
            recorder.DataAvailable += (data, flags, position, qpc) =>
            {
                var callbackAt = ProbeClock.Now;
                var count = data.Length / mix.BlockAlign;
                lock (sync)
                {
                    if (frames + count > SampleRate * (Seconds + 5)) { Error = "Loopback memory limit exceeded."; return; }
                    var mono = new float[count];
                    if ((flags & AudioClientBufferFlags.Silent) == 0)
                    {
                        var input = MemoryMarshal.Cast<byte, float>(data);
                        for (var i = 0; i < count; i++)
                        {
                            double value = 0;
                            for (var c = 0; c < mix.Channels; c++) value += input[i * mix.Channels + c];
                            mono[i] = (float)(value / mix.Channels);
                        }
                    }
                    chunks.Add(new(qpc / 10000000.0, callbackAt, position, (int)flags, mono)); frames += count;
                }
            };
            try { recorder.StartRecording(); } catch { recorder.Dispose(); endpoint.Dispose(); throw; }
        }
        public Chunk[] Stop() { recorder.StopRecording(); lock (sync) return chunks.ToArray(); }
        public void Dispose() { recorder.StopRecording(); recorder.Dispose(); endpoint.Dispose(); }
    }

    private sealed class Deadline : WaitHandle
    {
        public Deadline()
        {
            SafeWaitHandle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x00100002);
            if (SafeWaitHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Until(double at, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var remaining = at - ProbeClock.Now; if (remaining <= 0) return;
                var due = -Math.Max(1, (long)Math.Ceiling(remaining * 10000000));
                if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WaitOne(1000)) throw new TimeoutException("Render test deadline timer timed out.");
            }
            token.ThrowIfCancellationRequested();
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateWaitableTimerExW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeWaitHandle CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
            IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);
    }

    private static Match Find(ReadOnlySpan<float> signal, ReadOnlySpan<float> template)
    {
        if (template.Length < 16 || signal.Length < template.Length) throw new ArgumentException("Insufficient correlation samples.");
        double templateEnergy = 0; foreach (var x in template) templateEnergy += x * (double)x;
        var prefix = new double[signal.Length + 1];
        for (var i=0;i<signal.Length;i++) prefix[i+1] = prefix[i] + signal[i] * (double)signal[i];
        var best = new Match(0,0);
        for (var offset=0;offset<=signal.Length-template.Length;offset+=4) Consider(signal,template,prefix,templateEnergy,offset,ref best);
        var center = best.Offset;
        for (var offset=Math.Max(0,center-4);offset<=Math.Min(signal.Length-template.Length,center+4);offset++)
            Consider(signal,template,prefix,templateEnergy,offset,ref best);
        return best;
    }
    private static void Consider(ReadOnlySpan<float> signal, ReadOnlySpan<float> template, double[] prefix,
        double templateEnergy, int offset, ref Match best)
    {
        var energy = prefix[offset+template.Length]-prefix[offset];
        if (energy < 1e-12 || templateEnergy < 1e-12) return;
        double dot=0; for (var i=0;i<template.Length;i++) dot += signal[offset+i]*(double)template[i];
        var score = Math.Abs(dot)/Math.Sqrt(energy*templateEnergy);
        if (score>best.Correlation) best = new(offset,score);
    }
    private static int TemplateOffset(int frame, int rate) => checked((int)((long)frame*Frame*rate/Rate));
    public static void SelfTest()
    {
        var template=Enumerable.Range(0,2048).Select(i=>(float)(Math.Sin(.08*i+.0001*i*i)*Math.Sin(Math.PI*i/2048))).ToArray();
        var signal=new float[7000]; for(var i=0;i<template.Length;i++) signal[i+1317]=template[i]*.3f;
        var found=Find(signal,template);
        if(found.Offset!=1317 || found.Correlation<.999 || Find(new float[7000],template).Correlation!=0
            || TemplateOffset(3100,48000)!=1488000 || TemplateOffset(3100,44100)!=1367100)
            throw new InvalidOperationException("Sub-frame correlation/attenuation/silence checks failed.");
        Console.WriteLine("PASS: sample-resolution template matching, attenuation and silence rejection.");
    }

    public static async Task RunAsync(string output, bool rawComparison = false, bool categoryComparison = false, bool combinedComparison = false, bool reserveComparison = false, bool codecComparison = false)
    {
        SelfTest();
        var markerFrames=Enumerable.Range(0,14).Select(i=>500+i*200).ToHashSet();
        var profiles = codecComparison ? new[] { OpusProfile.Baseline, OpusProfile.LowDelay64 } : new[] { OpusProfile.Baseline };
        var plans = profiles.ToDictionary(profile => profile, profile => BuildRenderPlan(markerFrames, profile, codecComparison));
        var rounds=new List<object>(); var complete=true;
        foreach(var mode in codecComparison ? new[]{"BASELINE-A1","LOWDELAY64","BASELINE-A2"} : reserveComparison ? new[]{"RESERVE20-A1","RESERVE10","RESERVE20-A2"} : categoryComparison ? new[]{"RAW1","GAMECHAT","RAW2"} : rawComparison ? new[]{"A1","RAW","A2"} : new[]{"A1","B","A2"})
        {
            var plan = plans[mode == "LOWDELAY64" ? OpusProfile.LowDelay64 : OpusProfile.Baseline];
            var packets = plan.Packets; var reference = plan.Reference;
            var trace=new PipelineTrace(); trace.Start(60);
            using var codec=new OpusAudioEncoder(OpusProfile.Baseline);
            var sink=new WindowsAudioDevice(codec,disableSource:true,frameMs:10,useRtpPlayout:true,
                adaptiveReserve:false,pipeline:trace,tracePeer:"synthetic",fixedReserveClockTarget:true,
                transientEmptyQueueHold:true,freshRestart:true,boundedRender:codecComparison || reserveComparison || mode=="B",rawRender:codecComparison || reserveComparison || mode=="RAW" || categoryComparison || (combinedComparison && mode=="B"),renderCategory:codecComparison || reserveComparison || mode=="GAMECHAT" || (combinedComparison && mode=="B") ? AudioStreamCategory.GameChat : AudioStreamCategory.Other,
                rtpReserveMs:mode=="RESERVE10" ? 10 : 20);
            using var cancel=new CancellationTokenSource(); Task? emitter=null;
            try
            {
                using var capture=new Capture(sink.OutputDeviceId);
                var markers=new List<Marker>(); var pace=new List<double>();
                var begin=ProbeClock.Now+.2;
                emitter=Task.Run(()=>
                {
                    using var timer=new Deadline();
                    for(var i=0;i<packets.Length;i++)
                    {
                        timer.Until(begin+i*.01,cancel.Token);
                        var at=ProbeClock.Now; pace.Add((at-begin-i*.01)*1000);
                        if(markerFrames.Contains(i)) markers.Add(new(i,at));
                        sink.GotRtpAudioPacket(1,(uint)i*Frame,packets[i]);
                    }
                });
                await sink.StartAudioSink(); await emitter; var steadySnapshot=sink.Snapshot(); await Task.Delay(500);
                var chunks=capture.Stop(); await sink.Close(); trace.Stop(); var pipeline=trace.Snapshot();
                var resampled=reference;
                if(capture.SampleRate!=Rate)
                {
                    var resampler=new WdlResamplingSampleProvider(new ArraySamples(reference),capture.SampleRate);
                    resampled=new float[(int)Math.Ceiling(reference.Length*(double)capture.SampleRate/Rate)];
                    var count=resampler.Read(resampled); Array.Resize(ref resampled,count);
                }
                var measurements=new List<object>(); var valid=0;
                foreach(var marker in markers)
                {
                    string? error=null; double? total=null,before=null,after=null,matchedAt=null; double? score=null; double? callbackReceiptTotalMs=null; bool? nativeQpcTrusted=null;
                    var played=pipeline.Playout.FirstOrDefault(p=>p.Ssrc==1 && p.Timestamp==(uint)marker.Frame*Frame && p.Outcome=="played");
                    var window=chunks.Where(c=>c.At+c.Samples.Length/(double)capture.SampleRate>marker.AdmittedAt-.02 && c.At<marker.AdmittedAt+.35).ToArray();
                    if(played?.SubmittedAt is null) error="Marker start packet was not submitted.";
                    else if(window.Length==0 || window.Any(c=>(c.Flags & (int)(AudioClientBufferFlags.TimestampError|AudioClientBufferFlags.DataDiscontinuity))!=0)) error="Missing/discontinuous loopback window.";
                    else if(window.Zip(window.Skip(1)).Any(pair=>pair.First.Position+pair.First.Samples.Length!=pair.Second.Position)) error="Loopback device position gap.";
                    else
                    {
                        var samples=window.SelectMany(c=>c.Samples).ToArray();
                        var start=TemplateOffset(marker.Frame,capture.SampleRate);
                        var template=resampled.AsSpan(start,2048).ToArray(); var match=Find(samples,template); score=match.Correlation;
                        matchedAt=window[0].At+match.Offset/(double)capture.SampleRate;
                        var index=match.Offset; var containing=window[0];
                        foreach(var chunk in window) { containing=chunk; if(index<chunk.Samples.Length) break; index-=chunk.Samples.Length; }
                        callbackReceiptTotalMs=(containing.CallbackAt-marker.AdmittedAt)*1000;
                        nativeQpcTrusted=containing.At<=containing.CallbackAt+.001 && containing.CallbackAt-containing.At<1;
                        if(match.Correlation<.7 || matchedAt<played.SubmittedAt-.001 || matchedAt-marker.AdmittedAt>.35) error="Ambiguous or incoherent correlation.";
                        else { total=(matchedAt-marker.AdmittedAt)*1000; before=(played.SubmittedAt-marker.AdmittedAt)*1000; after=(matchedAt-played.SubmittedAt)*1000; valid++; }
                    }
                    measurements.Add(new{marker.Frame,marker.AdmittedAt,submittedAt=played?.SubmittedAt,matchedAt,score,callbackReceiptTotalMs,nativeQpcTrusted,nativeQpcTotalMs=total,totalMs=nativeQpcTrusted==true ? total : null,beforeSubmitMs=before,afterSubmitMs=after,error});
                }
                complete &= valid >= 10 && capture.Error is null && pipeline.EvictedRecords == 0 && steadySnapshot.DeviceError is null;
                rounds.Add(new{mode,encoder=plan.Encoder,referenceKind=codecComparison ? "source-pcm" : "decoded-pcm",device=capture.Device,sampleRate=capture.SampleRate,valid,measurements,
                    pacingP95Ms=pace.Order().ElementAt((int)(pace.Count*.95)),maxPacingMs=pace.Max(),
                    snapshot=steadySnapshot,loopbackError=capture.Error,evicted=pipeline.EvictedRecords,
                    callbackAgeMinMs=chunks.Where(c=>c.At>0).Min(c=>(c.CallbackAt-c.At)*1000),callbackAgeMaxMs=chunks.Where(c=>c.At>0).Max(c=>(c.CallbackAt-c.At)*1000)});
                Console.WriteLine($"Render path {mode}: valid markers={valid}/{markers.Count}");
            }
            finally
            {
                cancel.Cancel();
                if(emitter is not null) try { await emitter; } catch(OperationCanceledException) { }
                await sink.Close(); trace.Stop();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output,JsonSerializer.Serialize(new{version=typeof(CallForm).Assembly.GetName().Version?.ToString(),rounds,
                complete,notes="Same-host synthetic RTP admission to native WASAPI digital loopback QPC, A/B/A. Includes receiver queue and output path. Packets are preencoded; capture, real-time encoding execution, remote clock, network and physical DAC/ear are not measured. referenceKind=source-pcm retains codec waveform displacement; decoded-pcm references decoded synthetic chirps. Mixed samples retained transiently in bounded memory only, never saved. Native QPC totals are invalid when nativeQpcTrusted=false; callbackReceiptTotalMs includes loopback delivery, scheduling and packet quantization, not physical presentation."},DiagnosticJson.Readable));
        }
        if (!complete) throw new InvalidOperationException("Incomplete render comparison; inspect rejected markers and device errors before interpreting latency.");
    }
}
