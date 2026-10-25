using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using SIPSorcery.Net;
using VoiceNative;

internal static class SdkReceiveRaceProbe
{
    // Test-only wrapper: serialize the SDK's check/set/start section.
    private sealed class SerializedBeginReceiver(Socket socket) : UdpReceiver(socket)
    {
        private readonly object beginSync = new();
        public override void BeginReceiveFrom() { lock(beginSync) base.BeginReceiveFrom(); }
    }
    public static async Task RunAsync(string report,string address,int count=5000)
    {
        if(count is <1000 or >100000) throw new ArgumentOutOfRangeException(nameof(count));
        var bind=IPAddress.Parse(address);
        if(!IPAddress.IsLoopback(bind)) MediaInterfaceSelection.Resolve(address);
        var results=new List<object>();
        foreach(var mode in new[]{"sdk-a1","serialized-b","sdk-a2"})
        {
            using var socket=new Socket(bind.AddressFamily,SocketType.Dgram,ProtocolType.Udp);
            socket.ReceiveBufferSize=1_000_000; socket.Bind(new IPEndPoint(bind,0));
            using var sender=new Socket(bind.AddressFamily,SocketType.Dgram,ProtocolType.Udp);
            sender.Bind(new IPEndPoint(bind,0));
            var target=(IPEndPoint)socket.LocalEndPoint!;
            var receiver=mode=="serialized-b" ? new SerializedBeginReceiver(socket) : new UdpReceiver(socket);
            var seen=new int[count]; var malformed=0; var callbacks=0; var recoveryAttempts=0;
            receiver.OnPacketReceived+=(_,_,_,packet)=>
            {
                Interlocked.Increment(ref callbacks);
                if(packet.Length!=64) {Interlocked.Increment(ref malformed);return;}
                var ordinal=BinaryPrimitives.ReadInt32LittleEndian(packet);
                var inverse=BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4));
                if(ordinal<0 || ordinal>=count || inverse!=~ordinal)
                {Interlocked.Increment(ref malformed);return;}
                Interlocked.Increment(ref seen[ordinal]);
            };
            receiver.BeginReceiveFrom();
            using var stop=new CancellationTokenSource();
            var recoveries=Enumerable.Range(0,3).Select(_=>new Thread(()=>
            {
                while(!stop.IsCancellationRequested)
                {
                    Thread.MemoryBarrier();
                    if(!receiver.IsRunningReceive && !receiver.IsClosed)
                    {Interlocked.Increment(ref recoveryAttempts);receiver.BeginReceiveFrom();}
                    Thread.SpinWait(32);
                }
            }) {IsBackground=true}).ToArray();
            foreach(var t in recoveries)t.Start();
            var packet=new byte[64]; var pacing=System.Diagnostics.Stopwatch.StartNew(); var nextBurst=0.0; var sent=0; string? error=null;
            try
            {
                for(var n=0;n<count;n++)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(packet,n);
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4),~n);
                    sender.SendTo(packet,target);sent++;
                    if(n%2==1)
                    {
                        nextBurst=Math.Max(nextBurst+1,pacing.Elapsed.TotalMilliseconds);
                        while(pacing.Elapsed.TotalMilliseconds<nextBurst) Thread.SpinWait(32);
                    }
                }
                await Task.Delay(500);
            }
            catch(Exception ex){error=ex.ToString();}
            finally
            {
                stop.Cancel(); foreach(var t in recoveries) if(!t.Join(2000)) error ??= "Recovery caller failed to stop";
                receiver.Close("test complete");
            }
            await Task.Delay(100);
            var duplicates=seen.Sum(n=>Math.Max(0,n-1));var missing=seen.Count(n=>n==0);
            results.Add(new {mode,sent,callbacks,duplicates,missing,malformed,recoveryAttempts,error,
                sdkReceiveDiagnostics=SdkReceiveDiagnostics.Snapshot()});
            Console.WriteLine($"SDK RECEIVE {mode}: sent={sent}, callbacks={callbacks}, recoveryCalls={recoveryAttempts}, duplicates={duplicates}, missing={missing}, malformed={malformed}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new {address,sdk=typeof(UdpReceiver).Assembly.GetName().Version?.ToString(),results,
            limits="Stress of concurrent public SDK BeginReceiveFrom recovery callers, matching the SDK send/rearm call topology. Synchronous fresh ordinal UDP sends, no RTP/SRTP/audio. Not a network latency or product frequency measurement. Serialized wrapper is test-only, not installed into WebRTC."},DiagnosticJson.Readable));
    }
}
