using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceNative;

// Independent diagnostic streams: explicitly selected interface, LAN and two STUN paths.
internal static class PathIsolationProbe
{
    public static async Task RunAsync(string source, string peer, string server, string reference, int seconds, string path, int loadHz = 0, bool loadLan = false, bool voiceQos = false, int portBase = 0)
    {
        if (seconds is < 2 or > 180) throw new ArgumentException("Duration must be 2..180 seconds.");
        if (loadHz is < 0 or > 400) throw new ArgumentException("Load must be 0..400 packets/s.");
        if (portBase != 0 && (portBase < 1024 || portBase > 65532))
            throw new ArgumentException("Port base must be 0 or 1024..65532 (four consecutive ports).");
        var choice = MediaInterfaceSelection.Resolve(source) ?? throw new ArgumentException("Explicit interface required.");
        var lanPort = portBase != 0 && IPAddress.Parse(peer).Equals(IPAddress.Parse(source)) ? portBase : 42000;
        var endpoints = new[] { new IPEndPoint(IPAddress.Parse(peer), lanPort),
            new IPEndPoint(IPAddress.Parse(server), 3478), new IPEndPoint(IPAddress.Parse(reference), 19302),
            new IPEndPoint(IPAddress.Parse(loadLan ? peer : server), loadLan ? 42000 : 3478) };
        var names = new[] { "lan", "oci", "reference", "load" };
        var token = RandomNumberGenerator.GetBytes(8);
        using var stop = new CancellationTokenSource();
        var opened = new List<UdpClient>();
        try
        {
            for (var i = 0; i < endpoints.Length; i++)
            {
                var client = new UdpClient(new IPEndPoint(IPAddress.Parse(source), portBase != 0 ? portBase + i : i == 0 ? 42000 : 0));
                opened.Add(client);
                MediaInterfaceSelection.Apply(client.Client, choice);
                client.Client.ReceiveBufferSize = 1024 * 1024;
            }
        }
        catch { foreach (var client in opened) client.Dispose(); throw; }
        var clients = opened.ToArray();
        ProbeVoiceQos? qos = null;
        try { if (voiceQos) qos = ProbeVoiceQos.Create(clients, endpoints); }
        catch { foreach (var client in clients) client.Dispose(); throw; }
        var sent = endpoints.Select(_ => new List<object>()).ToArray();
        var received = endpoints.Select(_ => new List<object>()).ToArray();
        var ignored = new int[4];
        var errors = new string?[4];
        var readers = clients.Select((client, i) => Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var result = await client.ReceiveAsync(stop.Token);
                    var at = ProbeClock.Now;
                    var b = result.Buffer;
                    if (i == 0 && result.RemoteEndPoint.Address.Equals(endpoints[0].Address) &&
                        b.Length == 32 && b[0] == 0x56 && b[1] == 0x50 && b[2] == 1)
                    { b[2] = 2; await client.SendAsync(b, result.RemoteEndPoint, stop.Token); continue; }
                    if (!result.RemoteEndPoint.Equals(endpoints[i])) { ignored[i]++; continue; }
                    if ((i == 0 || i == 3 && loadLan) && b.Length == 32 && b[0] == 0x56 && b[1] == 0x50)
                    {
                        if (b[2] == 1) { b[2] = 2; await client.SendAsync(b, result.RemoteEndPoint, stop.Token); }
                        else if (b[2] == 2 && b.AsSpan(4, 8).SequenceEqual(token))
                            received[i].Add(new { sequence = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(12)), at });
                        else ignored[i]++;
                    }
                    else if (i != 0 && b.Length >= 20 && b[0] == 1 && b[1] == 1 &&
                        BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(4)) == 0x2112A442 && b.AsSpan(8, 8).SequenceEqual(token))
                        received[i].Add(new { sequence = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), at });
                    else ignored[i]++;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (SocketException error) { errors[i] = error.Message; }
        })).ToArray();
        var load = Task.Run(() =>
        {
            if (loadHz == 0) return;
            using var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x001F0003);
            if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var start = ProbeClock.Now;
            for (var sequence = 0; !stop.IsCancellationRequested && ProbeClock.Now - start < seconds; sequence++)
            {
                var b = new byte[loadLan ? 32 : 20];
                if (loadLan)
                {
                    b[0] = 0x56; b[1] = 0x50; b[2] = 1; token.CopyTo(b, 4);
                    BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), sequence);
                }
                else
                {
                    b[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x2112A442);
                    token.CopyTo(b, 8); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), sequence + 1_000_000);
                }
                var before = ProbeClock.Now;
                clients[3].Send(b, endpoints[3]);
                sent[3].Add(new { sequence = loadLan ? sequence : sequence + 1_000_000, at = before, completedAt = ProbeClock.Now });
                var delay = -(10_000_000L / loadHz);
                if (!SetWaitableTimer(timer, ref delay, 0, IntPtr.Zero, IntPtr.Zero, false) || WaitForSingleObject(timer, 1000) != 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        });
        try
        {
            var start = ProbeClock.Now;
            for (var sequence = 0; ProbeClock.Now - start < seconds; sequence++)
            {
                for (var i = 0; i < 3; i++)
                {
                    var b = new byte[i == 0 ? 32 : 20];
                    if (i == 0)
                    {
                        b[0] = 0x56; b[1] = 0x50; b[2] = 1;
                        token.CopyTo(b, 4); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(12), sequence);
                    }
                    else
                    {
                        b[1] = 1; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 0x2112A442);
                        token.CopyTo(b, 8); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), sequence);
                    }
                    var before = ProbeClock.Now;
                    await clients[i].SendAsync(b, endpoints[i], stop.Token);
                    sent[i].Add(new { sequence, at = before, completedAt = ProbeClock.Now });
                }
                await Task.Delay(20);
            }
            await load;
            await Task.Delay(1500);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(readers.Append(load)); }
            finally { qos?.Dispose(); foreach (var client in clients) client.Dispose(); }
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId, generatedAt = DateTimeOffset.Now, runtime = Environment.Version.ToString(),
            source, mediaInterface = choice, seconds, loadHz, loadLan, portBase, token = Convert.ToHexString(token),
            qos = new { requested = voiceQos, flows = qos?.Flows, closeError = qos?.CloseError },
            streams = names.Select((name, i) => new { name, endpoint = endpoints[i].ToString(), sentPerSecond = sent[i].Count / (double)seconds, sent = sent[i], received = received[i], ignored = ignored[i], error = errors[i] }),
            note = "Bounded synthetic LAN echo and STUN Binding controls, explicit selected interface. A local Ethernet interface does not prove an entirely wired path beyond the first hop. RTT includes both directions and endpoint handling. Task.Delay control pacing is observed, not a precise packet clock; optional load uses a bounded high-resolution wait timer without changing global timer resolution. No audio, system route changes or clock synchronization."
        }, DiagnosticJson.Readable));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion, IntPtr state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
