using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SIPSorcery.Net.SharpSRTP.SRTP;
using VoiceNative;

internal static class SrtpProtectionChecks
{
    private static void Require(bool ok, string message)
    { if (!ok) throw new InvalidOperationException(message); }
    private static SrtpContext Create(int bits, SrtpContextType type = SrtpContextType.RTP) => new(type,
        new(bits == 128 ? SrtpCiphers.AEAD_AES_128_GCM : SrtpCiphers.AEAD_AES_256_GCM,
            bits, 96, int.MaxValue, SrtpAuth.NONE, 0, 128),
        Enumerable.Range(0,bits/8).Select(i=>(byte)i).ToArray(),
        Enumerable.Range(0,12).Select(i=>(byte)(i+32)).ToArray());
    private static void Packet(Span<byte> input, uint index, uint ssrc)
    {
        input.Clear(); input[0]=0x80; input[1]=111;
        BinaryPrimitives.WriteUInt16BigEndian(input[2..], unchecked((ushort)index));
        BinaryPrimitives.WriteUInt32BigEndian(input[4..], index*120);
        BinaryPrimitives.WriteUInt32BigEndian(input[8..],ssrc);
        for(var i=12;i<input.Length;i++) input[i]=(byte)(i+index);
    }
    private static void Oracle(SrtpContext context, ReadOnlySpan<byte> input, ulong index, Span<byte> expected)
    {
        Span<byte> nonce=stackalloc byte[12];context.K_s.AsSpan(0,12).CopyTo(nonce);
        var ssrc=BinaryPrimitives.ReadUInt32BigEndian(input[8..]);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[2..],BinaryPrimitives.ReadUInt32BigEndian(nonce[2..])^ssrc);
        for(var i=0;i<6;i++) nonce[11-i]^=(byte)(index>>(8*i));
        input[..12].CopyTo(expected);
        using var aes=new AesGcm(context.K_e,16);
        aes.Encrypt(nonce,input[12..],expected.Slice(12,input.Length-12),expected.Slice(input.Length,16),input[..12]);
    }
    internal static void Run(string report)
    {
        using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var cases=new List<object>();
        foreach(var bits in new[]{128,256})
        {
            var send=Create(bits);var receive=Create(bits);
            var input=new byte[52];var wire=new byte[68];var decoded=new byte[68];var expected=new byte[68];
            const uint ssrc=0x01020304;
            foreach(var index in new uint[]{65533,65534,65535,65536,65537,65538,65539})
            {
                Packet(input,index,ssrc);
                Require(send.ProtectRtp(input,wire,out var size)==0 && size==wire.Length,"SRTP encryption failed");
                Oracle(send,input,index,expected);
                // Initial ROC is zero, then increments at the 16-bit wrap.
                Require(wire.AsSpan().SequenceEqual(expected),"SRTP ciphertext/tag differs from independent .NET AES-GCM oracle");
                var forged=(byte[])wire.Clone();forged[^1]^=1;
                Require(receive.UnprotectRtp(forged,decoded,out var badSize)==SrtpContext.ERROR_HMAC_CHECK_FAILED && badSize==0,
                    "Forged authentication tag accepted");
                Require(receive.UnprotectRtp(wire,decoded,out var n)==0 && decoded.AsSpan(0,n).SequenceEqual(input),
                    "Valid packet rejected after forged packet");
                Require(receive.UnprotectRtp(wire,decoded,out _)==SrtpContext.ERROR_REPLAY_CHECK_FAILED,"Replay accepted");
                digest.AppendData(wire);
            }
            for(uint index=65540;index<65544;index++)
            {
                if(index==65540) {send.K_e[0]^=0x80;receive.K_e[0]^=0x80;}
                if(index is 65542 or 65543) {send.DeriveSessionKeys();receive.DeriveSessionKeys();}
                Packet(input,index,ssrc);
                Require(send.ProtectRtp(input,wire,out _)==0,"Key change encryption failed");
                Oracle(send,input,index,expected);Require(wire.AsSpan().SequenceEqual(expected),"Mutable key/rederived engine cache mismatch");
                Require(receive.UnprotectRtp(wire,decoded,out var n)==0 && decoded.AsSpan(0,n).SequenceEqual(input),"Rekey decrypt failed");
                digest.AppendData(wire);
            }
            var reusedNonceRejected=false;
            try { send.ProtectRtp(input,wire,out _); }
            catch(ArgumentException) { reusedNonceRejected=true; }
            Require(reusedNonceRejected,"Repeated GCM encryption nonce accepted");
            var concurrent=new ThreadSafeSrtpSessionContext(new SrtpSessionContext(Create(bits),Create(bits),Create(bits,SrtpContextType.RTCP),Create(bits,SrtpContextType.RTCP)));
            var concurrentWire=new byte[4*100][];
            Parallel.For(0,4,worker=>
            {
                var plain=new byte[52];var encrypted=new byte[68];
                for(uint i=0;i<100;i++)
                {
                    Packet(plain,i,(uint)(ssrc+worker));
                    Require(concurrent.ProtectRtp(plain,encrypted,out _)==0,"Concurrent wrapper protection failed");
                    concurrentWire[worker*100+i]=(byte[])encrypted.Clone();
                }
            });
            foreach(var encrypted in concurrentWire)
            {
                Require(concurrent.UnprotectRtp(encrypted,decoded,out _)==0,"Concurrent SSRC context decryption failed");
                digest.AppendData(encrypted);
            }
            var firstSend=Create(bits);var firstReceive=Create(bits);
            Packet(input,40000,ssrc);Require(firstSend.ProtectRtp(input,wire,out _)==0,"Initial forgery fixture failed");
            wire[^1]^=1;
            Require(firstReceive.UnprotectRtp(wire,decoded,out _)==SrtpContext.ERROR_HMAC_CHECK_FAILED,"Initial forgery accepted");
            Require(!firstReceive.ReplayProtection[ssrc].S_l_set,"Unauthenticated first packet seeded replay state");
            firstSend=Create(bits);Packet(input,0,ssrc);
            Require(firstSend.ProtectRtp(input,wire,out _)==0 && firstReceive.UnprotectRtp(wire,decoded,out _)==0,"Valid initial sequence zero rejected after forgery");
            var unorderedSend=Create(bits);var unorderedReceive=Create(bits);
            var packets=new List<byte[]>();
            for(uint i=10;i<15;i++){Packet(input,i,ssrc);Require(unorderedSend.ProtectRtp(input,wire,out _)==0,"Order fixture failed");packets.Add((byte[])wire.Clone());}
            foreach(var i in new[]{0,2,1,4,3}) Require(unorderedReceive.UnprotectRtp(packets[i],decoded,out _)==0,"Within-window reordered packet rejected");
            var rtcpSend=Create(bits,SrtpContextType.RTCP);var rtcpReceive=Create(bits,SrtpContextType.RTCP);
            var control=new byte[28];control[0]=0x80;control[1]=200;control[3]=6;
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4),ssrc);
            var protectedControl=new byte[48];
            for(var i=0;i<40;i++)
            {
                control[12]=(byte)i;
                Require(rtcpSend.ProtectRtcp(control,protectedControl,out var n)==0,"SRTCP encrypt failed");
                var forged=(byte[])protectedControl.Clone();forged[12]^=1;
                Require(rtcpReceive.UnprotectRtcp(forged.AsSpan(0,n),decoded,out _)==SrtpContext.ERROR_HMAC_CHECK_FAILED,"SRTCP forgery accepted");
                Require(rtcpReceive.UnprotectRtcp(protectedControl.AsSpan(0,n),decoded,out var plain)==0 && decoded.AsSpan(0,plain).SequenceEqual(control),"SRTCP valid/replay-state recovery failed");
                Require(rtcpReceive.UnprotectRtcp(protectedControl.AsSpan(0,n),decoded,out _)==SrtpContext.ERROR_REPLAY_CHECK_FAILED,"SRTCP replay accepted");
                digest.AppendData(protectedControl.AsSpan(0,n));
            }
            var benchmark=Create(bits);const int count=100000;
            for(uint i=0;i<1000;i++){Packet(input,i,ssrc);Require(benchmark.ProtectRtp(input,wire,out _)==0,"Warmup failed");}
            var before=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();
            for(uint i=1000;i<count+1000;i++){Packet(input,i,ssrc);Require(benchmark.ProtectRtp(input,wire,out _)==0,"Benchmark encryption failed");}
            watch.Stop();var allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            cases.Add(new{bits,packets=count,bytesPerPacket=allocated/(double)count,usPerPacket=watch.Elapsed.TotalMicroseconds/count});
        }
        foreach (var bits in new[] {128,256})
        {
            SrtpContext DoubleContext() => new(SrtpContextType.RTP,
                new(bits==128 ? SrtpCiphers.DOUBLE_AEAD_AES_128_GCM_AEAD_AES_128_GCM : SrtpCiphers.DOUBLE_AEAD_AES_256_GCM_AEAD_AES_256_GCM,
                    bits*2,192,int.MaxValue,SrtpAuth.NONE,0,256),
                Enumerable.Range(0,bits/4).Select(i=>(byte)i).ToArray(),
                Enumerable.Range(0,24).Select(i=>(byte)(i+32)).ToArray());
            var send=DoubleContext();var receive=DoubleContext();
            var input=new byte[52];var wire=new byte[128];var decoded=new byte[128];const uint ssrc=999;
            Packet(input,40000,ssrc);
            Require(send.ProtectRtp(input,wire,out var size)==0,"Double GCM fixture failed");
            wire[size-1]^=1;
            Require(receive.UnprotectRtp(wire.AsSpan(0,size),decoded,out _)!=0 && !receive.ReplayProtection[ssrc].S_l_set,
                "Double GCM unauthenticated initial state changed");
            send=DoubleContext();
            foreach(var index in new uint[]{0,1,2})
            {
                Packet(input,index,ssrc);
                Require(send.ProtectRtp(input,wire,out size)==0 && receive.UnprotectRtp(wire.AsSpan(0,size),decoded,out var n)==0 && decoded.AsSpan(0,n).SequenceEqual(input),
                    "Double GCM initial sequence zero/recovery failed");
            }
            send=DoubleContext();receive=DoubleContext();
            foreach(var index in new uint[]{65534,65535,65536,65537})
            {
                Packet(input,index,ssrc);
                Require(send.ProtectRtp(input,wire,out size)==0 && receive.UnprotectRtp(wire.AsSpan(0,size),decoded,out var n)==0 && decoded.AsSpan(0,n).SequenceEqual(input),"Double GCM rollover failed");
                Require(receive.UnprotectRtp(wire.AsSpan(0,size),decoded,out _) == SrtpContext.ERROR_REPLAY_CHECK_FAILED,"Double GCM replay accepted");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
        File.WriteAllText(report,JsonSerializer.Serialize(new{sdk=SdkBuildIdentity.Read(),
            wireDigest=Convert.ToHexString(digest.GetHashAndReset()),cases,
            limits="Public deterministic test keys only. .NET AES-GCM oracle for RTP; baseline digest comparison for RTP/SRTCP. Warm synchronous encryption benchmark excludes network/devices. Not a mouth-to-ear measurement."},DiagnosticJson.Readable));
        Console.WriteLine("PASS: AES-128/256-GCM oracle, rollover, forged tag rejection without replay advancement, replay/reorder, key mutation/rederive and SRTCP");
    }
}
