using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;

namespace SIPSorcery.Net;

// Compiled ONLY into an isolated diagnostic SDK. Never logs payload, endpoints or keys.
public static class MyeongdongBoundaryProbe
{
    public readonly record struct Header(bool Valid, uint Ssrc, uint Timestamp, ushort Sequence);
    public readonly record struct Entry(double At, string Stage, int Port, uint Ssrc, uint Timestamp,
        ushort Sequence, int Bytes, int Status);
    public sealed class SendToken
    {
        public readonly Socket Socket;
        public readonly Header Header;
        public readonly int Port;
        public SendToken(Socket socket, Header header, int port) { Socket = socket; Header = header; Port = port; }
    }
    private static readonly object Sync = new object();
    private static Entry[] records = Array.Empty<Entry>();
    private static int count, next;
    private static long until, skipped, overwritten;
    private static double started, stopped;
    public static bool Enabled => Volatile.Read(ref until) != 0 && Stopwatch.GetTimestamp() < Volatile.Read(ref until);
    public static void Start(int seconds, int capacity)
    {
        if (seconds < 1 || seconds > 120 || capacity < 4 || capacity > 64000) throw new ArgumentOutOfRangeException();
        lock (Sync)
        {
            Volatile.Write(ref until, 0); records = new Entry[capacity]; next = count = 0; skipped = overwritten = 0;
            started = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; stopped = 0;
            Volatile.Write(ref until, Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency);
        }
    }
    public static void Stop()
    { lock (Sync) { if (stopped == 0) stopped = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; Volatile.Write(ref until, 0); } }
    public static Header Read(ReadOnlySpan<byte> input)
    {
        if (!Enabled) return default;
        return Parse(input);
    }
    private static Header Parse(ReadOnlySpan<byte> input)
    {
        if (input.Length >= 4 && (input[0] & 0xc0) == 0x40)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(2));
            if (length < 12 || length > input.Length - 4) return default;
            input = input.Slice(4, length);
        }
        else if (input.Length >= 20 && input[0] == 0 && (input[1] == 0x16 || input[1] == 0x17)
            && BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4)) == 0x2112a442)
        {
            int end = 20 + BinaryPrimitives.ReadUInt16BigEndian(input.Slice(2));
            if (end > input.Length) return default;
            int at = 20; bool found = false;
            while (at + 4 <= end)
            {
                int type = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(at));
                int length = BinaryPrimitives.ReadUInt16BigEndian(input.Slice(at + 2)); at += 4;
                if (at + length > end) return default;
                if (type == 0x13) { input = input.Slice(at, length); found = true; break; }
                at += (length + 3) & ~3;
            }
            if (!found) return default;
        }
        if (input.Length < 12 || input[0] >> 6 != 2 || (input[1] & 127) != 111
            || input.Length < 12 + 4 * (input[0] & 15)) return default;
        return new Header(true, BinaryPrimitives.ReadUInt32BigEndian(input.Slice(8)),
            BinaryPrimitives.ReadUInt32BigEndian(input.Slice(4)), BinaryPrimitives.ReadUInt16BigEndian(input.Slice(2)));
    }
    public static void Observe(string stage, int port, ReadOnlySpan<byte> packet, int bytes, int status)
    { if (Enabled) Emit(stage, port, Parse(packet), bytes, status); }
    public static void Emit(string stage, int port, Header header, int bytes, int status)
    {
        if (!header.Valid || !Enabled) return;
        if (!Monitor.TryEnter(Sync)) { Interlocked.Increment(ref skipped); return; }
        try
        {
            if (!Enabled) return;
            if (count == records.Length) overwritten++; else count++;
            records[next] = new Entry(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, stage, port,
                header.Ssrc, header.Timestamp, header.Sequence, bytes, status);
            next = (next + 1) % records.Length;
        }
        finally { Monitor.Exit(Sync); }
    }
    public static SendToken Sending(Socket socket, int port, byte[] packet)
    {
        if (!Enabled) return null;
        var header = Parse(packet);
        if (!header.Valid) return null;
        Emit("socket-send-post", port, header, packet.Length, 0);
        return new SendToken(socket, header, port);
    }
    public static string SnapshotJson()
    {
        lock (Sync)
        {
            var ordered = new Entry[count]; int first = (next - count + records.Length) % Math.Max(1, records.Length);
            for (int i = 0; i < count; i++) ordered[i] = records[(first + i) % records.Length];
            return JsonSerializer.Serialize(new { DiagnosticOnly = true, StartedAt = started, StoppedAt = stopped,
                Capacity = records.Length, Skipped = Interlocked.Read(ref skipped), Overwritten = overwritten, Records = ordered,
                Limits = "APM EndSend/EndReceive observation is not physical wire time or OS arrival time. Only clear Opus PT111 header identity. SRTP status -3=authentication, -4=replay per pinned SDK. Callback scheduling and diagnostic overhead are included." });
        }
    }
    public static void SelfTest()
    {
        var rtp = new byte[12]; rtp[0] = 0x80; rtp[1] = 111; rtp[3] = 7; rtp[7] = 9; rtp[11] = 11;
        if (Parse(rtp) != new Header(true, 11, 9, 7)) throw new InvalidOperationException("RTP parser");
        var channel = new byte[16]; channel[0] = 0x40; channel[3] = 12; rtp.CopyTo(channel, 4);
        if (Parse(channel) != Parse(rtp)) throw new InvalidOperationException("ChannelData parser");
        var stun = new byte[36]; stun[1] = 0x17; stun[3] = 16; BinaryPrimitives.WriteUInt32BigEndian(stun.AsSpan(4), 0x2112a442);
        stun[21] = 0x13; stun[23] = 12; rtp.CopyTo(stun, 24);
        if (Parse(stun) != Parse(rtp)) throw new InvalidOperationException("TURN parser");
        channel[3] = 13; if (Parse(channel).Valid) throw new InvalidOperationException("Truncated TURN accepted");
        rtp[0] = 0x8f; if (Parse(rtp).Valid) throw new InvalidOperationException("Truncated CSRC accepted"); rtp[0] = 0x80;
        Start(5, 4); for (int i = 0; i < 8; i++) Observe("test", 1, rtp, 12, i); Stop();
        using (var doc = JsonDocument.Parse(SnapshotJson()))
            if (doc.RootElement.GetProperty("Records").GetArrayLength() != 4 || doc.RootElement.GetProperty("Overwritten").GetInt32() != 4)
                throw new InvalidOperationException("Bounded SDK recorder");
    }
}
