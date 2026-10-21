using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;

namespace VoiceNative;

// Common external measurement path: physical PC microphone -> app -> receiver loopback.
// Synthetic signal only. SSH carries clock/control metadata, never audio.
internal static class CrossAppProbe
{
    public static async Task Calibrate(string output)
    {
        var captureId=Environment.GetEnvironmentVariable("VOICE_COMPARE_CAPTURE_ID") ?? throw new InvalidOperationException("Select the explicit capture endpoint first.");
        using var encoder=new OpusAudioEncoder(OpusProfile.LowDelay64);
        await using var cleanup=new DeviceCleanup(new WindowsAudioDevice(encoder,disableSink:true,frameMs:10,intermediateCapture:false,captureDeviceId:captureId));
        using var enumerator=new MMDeviceEnumerator();
        var points=new List<object>();
        var signal=AcousticCaptureProbe.DiagnosticSignal();var bytes=new byte[signal.Length*2];Buffer.BlockCopy(signal,0,bytes,0,bytes.Length);
        await cleanup.Device.StartAudio();await Task.Delay(500);
        foreach(var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active).Where(e=>e.FriendlyName.Contains("Realtek",StringComparison.OrdinalIgnoreCase) && !e.FriendlyName.Contains("Digital",StringComparison.OrdinalIgnoreCase)))
        {
            using(endpoint)
            for(var trial=0;trial<3;trial++)
            {
                var detected=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                cleanup.Device.Acoustic.Arm("calibrate",at=>detected.TrySetResult(at));
                using var stream=new RawSourceWaveStream(new MemoryStream(bytes,false),new WaveFormat(48000,16,1));
                using var player=new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync().WithLatency(40).Build();
                player.Init(stream);player.Play();
                double? at=null;try{at=await detected.Task.WaitAsync(TimeSpan.FromSeconds(1.3));}catch(TimeoutException){}
                await Task.Delay(450);player.Stop();cleanup.Device.Acoustic.Cancel();
                points.Add(new{endpointId=endpoint.ID,device=endpoint.FriendlyName,trial,detectedAt=at});
                Console.WriteLine($"calibration {endpoint.FriendlyName} trial={trial} detected={at is not null}");
            }
        }
        Save(output,new{capture=cleanup.Device.Snapshot(),points,notes="Routing validation only, not app latency. Synthetic signal; defaults and volumes unchanged."});
    }
    private sealed record Message(string Kind, string Id, double A=0,double B=0,double C=0);
    private sealed class Wire(TextReader input,TextWriter output,Process? process=null):IDisposable
    {
        private readonly object sync=new();
        public Task Send(Message message)
        {
            lock(sync){output.WriteLine(JsonSerializer.Serialize(message));output.Flush();}
            return Task.CompletedTask;
        }
        public async Task<Message> Read(CancellationToken token)
        {
            var line=await input.ReadLineAsync(token) ?? throw new EndOfStreamException("Measurement control closed.");
            return JsonSerializer.Deserialize<Message>(line) ?? throw new InvalidDataException("Invalid control message.");
        }
        public void Dispose()
        {
            if(process is null)return;
            process.StandardInput.Close();
            if(!process.WaitForExit(5000))process.Kill();
            process.Dispose();
        }
    }
    private static async Task<Message> Receive(Wire socket,string kind,string id,CancellationToken token)
    {
        while(true)
        {
            var message=await socket.Read(token);
            if(message is not null && message.Kind==kind && message.Id==id)return message;
        }
    }
    public static async Task Receiver(string output)
    {
        using var socket=new Wire(Console.In,Console.Out);
        using var enumerator=new MMDeviceEnumerator();
        using var endpoint=enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia);
        using var capture=new LoopbackLatencyCapture(endpoint.ID);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var points=new List<object>();var sync=new object();string? error=null;
        object[] OutputOwners()
        {
            var owners=new List<object>();
            endpoint.AudioSessionManager.RefreshSessions();
            var sessions=endpoint.AudioSessionManager.Sessions;
            for(var index=0;index<sessions.Count;index++)
            {
                using var session=sessions[index];
                try
                {
                    using var owner=Process.GetProcessById((int)session.GetProcessID);
                    if(new[]{"mumble","TransportProbe","VoiceNative"}.Contains(owner.ProcessName,StringComparer.OrdinalIgnoreCase))
                        owners.Add(new{owner.ProcessName,owner.Id,state=session.State.ToString()});
                }
                catch(ArgumentException){}
            }
            return owners.ToArray();
        }
        await socket.Send(new("ready","ready"));
        try
        {
            while(true)
            {
                var message=await socket.Read(timeout.Token);var at=ProbeClock.Now;
                if(message is null)continue;
                if(message.Kind=="stop")break;
                if(message.Kind=="sync")await socket.Send(new("sync",message.Id,message.A,at,ProbeClock.Now));
                if(message.Kind=="arm")
                {
                    var id=message.Id;
                    capture.Arm(id,detected=>
                    {
                        var owners=OutputOwners();
                        lock(sync)points.Add(new {id,detectedAt=detected,callbackAt=ProbeClock.Now,outputSessions=owners});
                        _=socket.Send(new("detected",id,detected));
                    });
                    await socket.Send(new("armed",id));
                }
            }
        }
        catch(Exception ex){error=ex.ToString();}
        object[] captured;lock(sync)captured=points.ToArray();
        Save(output,new{device=capture.Device,endpointId=endpoint.ID,outputSessions=OutputOwners(),error,points=captured,notes="Receiver digital loopback. Not physical speaker/ear output. No audio saved."});
        if(error is not null)throw new InvalidOperationException("Common receiver did not complete; see report.");
    }
    public static async Task Sender(string output)
    {
        var password=Environment.GetEnvironmentVariable("VOICE_COMPARE_SSH_PASSWORD") ?? throw new InvalidOperationException("Set the transient SSH password environment variable.");
        var sshTarget=Environment.GetEnvironmentVariable("VOICE_COMPARE_SSH_TARGET") ?? throw new InvalidOperationException("Set the SSH target.");
        var hostKey=Environment.GetEnvironmentVariable("VOICE_COMPARE_SSH_HOSTKEY") ?? throw new InvalidOperationException("Set the pinned SSH host key.");
        var start=new ProcessStartInfo("C:/Program Files/PuTTY/plink.exe") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
        foreach(var argument in new[]{"-batch","-hostkey",hostKey,"-pw",password,sshTarget,"powershell.exe -NoProfile -Command \"& ./voice-pipeline-passive-054/TransportProbe.exe --crossapp-receive ./voice-pipeline-passive-054/crossapp-current-receiver.json\""})start.ArgumentList.Add(argument);
        var child=Process.Start(start) ?? throw new InvalidOperationException("SSH control did not start.");
        using var socket=new Wire(child.StandardOutput,child.StandardInput,child);
        var samples=new List<object>();string? error=null;
        var validMarkers=0;
        using var encoder=new OpusAudioEncoder(OpusProfile.LowDelay64);
        await using var cleanup=new DeviceCleanup(new WindowsAudioDevice(encoder,disableSink:true,frameMs:10,intermediateCapture:false,captureDeviceId:Environment.GetEnvironmentVariable("VOICE_COMPARE_CAPTURE_ID")));
        var microphone=cleanup.Device;
        using var enumerator=new MMDeviceEnumerator();
        var renderMatch=Environment.GetEnvironmentVariable("VOICE_COMPARE_RENDER_MATCH");
        using var renderEndpoint=renderMatch is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia)
            : enumerator.EnumerateAudioEndPoints(DataFlow.Render,DeviceState.Active).Single(e=>e.FriendlyName.Contains(renderMatch,StringComparison.OrdinalIgnoreCase));
        var signal=AcousticCaptureProbe.DiagnosticSignal();var bytes=new byte[signal.Length*2];Buffer.BlockCopy(signal,0,bytes,0,bytes.Length);
        try
        {
            using(var ready=new CancellationTokenSource(15000))await Receive(socket,"ready","ready",ready.Token);
            await microphone.StartAudio();await Task.Delay(1000);
            for(var trial=0;trial<8;trial++)
            {
                (double Low,double High)? best=null;
                for(var ping=0;ping<12;ping++)
                {
                    var id=$"sync-{trial}-{ping}";var a=ProbeClock.Now;
                    await socket.Send(new("sync",id,a));
                    using var limit=new CancellationTokenSource(2000);
                    var reply=await Receive(socket,"sync",id,limit.Token);var end=ProbeClock.Now;
                    var bounds=ProbeClock.Bounds(a,reply.B,reply.C,end);
                    if(bounds is {} b && (best is null || b.High-b.Low<best.Value.High-best.Value.Low))best=b;
                }
                if(best is null)throw new InvalidOperationException("No valid clock bounds.");
                var marker=$"marker-{trial}";
                var local=new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                microphone.Acoustic.Arm(marker,at=>local.TrySetResult(at));
                await socket.Send(new("arm",marker));
                using(var limit=new CancellationTokenSource(2000))await Receive(socket,"armed",marker,limit.Token);
                using var stream=new RawSourceWaveStream(new MemoryStream(bytes,false),new WaveFormat(48000,16,1));
                using var player=new WasapiPlayerBuilder().WithDevice(renderEndpoint).WithSharedMode().WithEventSync().WithLatency(40).Build();
                player.Init(stream);player.Play();
                double? source=null,target=null;string? excluded=null;
                try
                {
                    using var limit=new CancellationTokenSource(2500);
                    var detected=await Receive(socket,"detected",marker,limit.Token);target=detected.A;
                    source=await local.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch(Exception ex) when(ex is OperationCanceledException or TimeoutException){excluded="Source or receiver signal was not detected.";}
                finally
                {
                    if(local.Task.IsCompletedSuccessfully)source=local.Task.Result;
                    player.Stop();microphone.Acoustic.Cancel(marker);
                }
                double? low=source is {} a0 && target is {} b0 ? (b0-a0-best.Value.High)*1000:null;
                double? high=source is {} a1 && target is {} b1 ? (b1-a1-best.Value.Low)*1000:null;
                if(high is <0)excluded="Negative corrected interval; invalid timestamp or detection.";
                if(excluded is null && low is not null && high is not null)validMarkers++;
                samples.Add(new{trial,sourceAt=source,targetAt=target,clockLow=best.Value.Low,clockHigh=best.Value.High,clockWidthMs=(best.Value.High-best.Value.Low)*1000,minimumMs=low,maximumMs=high,detectorResolutionMs=20,excluded});
                Console.WriteLine($"trial={trial} interval={low:F1}..{high:F1}ms excluded={excluded}");
                await Task.Delay(1300);
            }
            if(validMarkers<6)error=$"Insufficient valid markers: {validMarkers}/8; do not infer app latency.";
        }
        catch(Exception ex){error=ex.ToString();}
        finally{await socket.Send(new("stop","done"));}
        Save(output,new{signalOutputDevice=renderEndpoint.FriendlyName,microphone=microphone.Snapshot(),error,validMarkers,samples,notes="Same external detector for both apps: PC selected capture endpoint onset to laptop digital loopback. Excludes source playback delay before the capture reference; includes processing after that reference, app, network and loopback delivery. The microphone field identifies the actual capture endpoint, which may be Stereo Mix. 20ms detector quantization, clock bounds not full error bounds. Not physical mouth-to-ear. No raw audio saved."});
        if(error is not null)throw new InvalidOperationException("Common sender did not complete; see report.");
    }
    private sealed class DeviceCleanup(WindowsAudioDevice device):IAsyncDisposable
    {
        public WindowsAudioDevice Device {get;}=device;
        public ValueTask DisposeAsync()=>new(Device.Close());
    }
    private static void Save(string path,object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path,JsonSerializer.Serialize(data,DiagnosticJson.Readable));
    }
}
