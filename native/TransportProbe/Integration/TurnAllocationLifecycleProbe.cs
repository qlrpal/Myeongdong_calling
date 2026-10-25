using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SIPSorcery.Net;
using VoiceNative;

internal static class TurnAllocationLifecycleProbe
{
    internal static async Task RunAsync(string server, string report, string address)
    {
        using var log = new TurnSignalingLog(); SIPSorcery.LogFactory.Set(log);
        using var http = new HttpClient();
        var config = await http.GetFromJsonAsync<JsonElement>(new Uri(new Uri(server), "api/config"));
        var servers = new List<RTCIceServer>();
        foreach (var item in config.GetProperty("iceServers").EnumerateArray())
        {
            var urls=item.GetProperty("urls");
            foreach(var url in urls.ValueKind==JsonValueKind.Array ? urls.EnumerateArray().Select(v=>v.GetString()!) : new[]{urls.GetString()!})
                if(url.StartsWith("turn:",StringComparison.OrdinalIgnoreCase))
                    servers.Add(new(){urls=url,username=item.GetProperty("username").GetString(),credential=item.GetProperty("credential").GetString()});
        }
        if(servers.Count==0)throw new InvalidOperationException("UDP TURN required");
        MediaInterfaceSelection.Resolve(address);
        var bind=IPAddress.Parse(address); var rows=new List<object>();
        var usedPorts=new HashSet<int>();
        var originalRelease=Environment.GetEnvironmentVariable("VOICE_SDK_TURN_RELEASE");
        int FreshPort()
        {
            int port;
            do {port=System.Security.Cryptography.RandomNumberGenerator.GetInt32(24576,32768)*2;}
            while(!usedPorts.Add(port));
            return port;
        }
        async Task<bool> Gather(string phase,int port)
        {
            log.Phase=phase; var clock=Stopwatch.StartNew();
            using var pc=new RTCPeerConnection(new RTCConfiguration {
                iceServers=servers,iceTransportPolicy=RTCIceTransportPolicy.relay,X_BindAddress=bind },port);
            while(pc.iceGatheringState!=RTCIceGatheringState.complete && clock.Elapsed.TotalSeconds<8) await Task.Delay(20);
            var channel=pc.GetRtpChannel();
            usedPorts.Add(((IPEndPoint)channel.RtpSocket.LocalEndPoint!).Port);
            var relay=channel.Candidates.FirstOrDefault(c=>c.type==RTCIceCandidateType.relay);
            rows.Add(new {phase,port,local=channel.RtpSocket.LocalEndPoint?.ToString(),
                relay=relay is null?null:relay.address+":"+relay.port,mapped=relay?.IceServer?.ServerReflexiveEndPoint?.ToString(),
                success=relay is not null,seconds=clock.Elapsed.TotalSeconds,state=pc.iceGatheringState.ToString()});
            pc.Close("allocation lifecycle test");
            // Coturn can defer deletion to its next allocation timer tick. Same delay in A and B.
            await Task.Delay(2000);
            return relay is not null;
        }
        try
        {
            foreach(var mode in new[]{"original-a1","release-b","original-a2"})
            {
                Environment.SetEnvironmentVariable("VOICE_SDK_TURN_RELEASE",mode=="release-b"?"1":"0");
                var port=FreshPort();
                if(!await Gather(mode+"-seed",port)) throw new InvalidOperationException("Fresh seed allocation failed");
                var reused=await Gather(mode+"-same-port",port);
                if(mode=="release-b" && !reused) throw new InvalidOperationException("Refresh(0) did not permit same-port reuse");
                if(!reused)
                {
                    // RFC 8656 section 7.4: a different transport address is the 437 recovery.
                    if(!await Gather(mode+"-new-port",FreshPort()))throw new InvalidOperationException("Different-port retry failed");
                }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOICE_SDK_TURN_RELEASE",originalRelease);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {
                rows,sdkIceLog=log.Snapshot(),sdkReceiveDiagnostics=SdkReceiveDiagnostics.Snapshot(),
                notes="Real UDP TURN allocation only, no audio or remote peer. A/B/A close policy; same explicit local port followed by fresh-port recovery when needed. Release is best effort. Original allocations can remain until server expiry; bounded seed count. No auth material is saved."},DiagnosticJson.Readable));
        }
    }
}
