using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VoiceNative;

// Diagnostic traffic only: no audio devices, codec, WebRTC, system timer-resolution change or payload storage.
internal static class PacedUdpProbe
{
    private sealed record Sent(int Sequence, double Start, double End, double Due, int Bytes);
    private sealed record Received(int Sequence, double At);

    public static async Task<int> RunAsync(string peer, int localPort, int peerPort, int bytes,
        int seconds, string role, string run, string path, string? planPath = null, bool uniform = false)
    {
        if (role is not ("pc" or "laptop") || bytes is < 32 or > 1200 || seconds is < 5 or > 120 ||
            localPort is < 1024 or > 65535 || peerPort is < 1024 or > 65535)
            throw new ArgumentException("Invalid bounded UDP probe settings.");
        var endpoint = new IPEndPoint(IPAddress.Parse(peer), peerPort);
        if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("IPv4 required.");
        var schedule = Enumerable.Range(0, seconds * 100).Select(i => (At: i * .010, Bytes: bytes)).ToArray();
        string? planHash = null;
        if (planPath is not null)
        {
            var data = await File.ReadAllBytesAsync(planPath);
            planHash = Convert.ToHexString(SHA256.HashData(data));
            using var plan = JsonDocument.Parse(data);
            if (plan.RootElement.GetProperty("role").GetString() != role || plan.RootElement.GetProperty("seconds").GetInt32() != seconds)
                throw new ArgumentException("Pattern role/duration mismatch.");
            schedule = plan.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => (At: i.GetProperty("At").GetDouble(), Bytes: i.GetProperty("Bytes").GetInt32())).ToArray();
            if (schedule.Length < seconds * 50 || schedule.Length > seconds * 200 || schedule[^1].At < seconds - 1 ||
                schedule.Any(s => !double.IsFinite(s.At) || s.At < 0 || s.At > seconds || s.Bytes is < 32 or > 1200) ||
                schedule.Zip(schedule.Skip(1)).Any(p => p.Second.At < p.First.At))
                throw new ArgumentException("Invalid bounded UDP pattern.");
            if (uniform)
            {
                var last = schedule[^1].At;
                for (var i = 0; i < schedule.Length; i++) schedule[i].At = i * last / (schedule.Length - 1);
            }
        }
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        udp.Client.ReceiveBufferSize = 1024 * 1024;
        var token = SHA256.HashData(Encoding.UTF8.GetBytes(run)).AsSpan(0, 8).ToArray();
        var ownRole = role == "pc" ? (byte)1 : (byte)2;
        var packets = schedule.Select(s => s.Bytes).Distinct().ToDictionary(n => n, n =>
        {
            var p = new byte[n]; token.CopyTo(p, 0); p[12] = ownRole; return p;
        });
        var sends = new List<Sent>(schedule.Length);
        var receives = new List<Received>(seconds * 100 + 1000);
        using var stop = new CancellationTokenSource();
        var ignored = 0; var overflow = 0; string? error = null;
        var receive = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var result = await udp.ReceiveAsync(stop.Token); var at = ProbeClock.Now;
                    if (!result.RemoteEndPoint.Equals(endpoint) || result.Buffer.Length is < 32 or > 1200 ||
                        !result.Buffer.AsSpan(0, 8).SequenceEqual(token) || result.Buffer[12] != (ownRole == 1 ? 2 : 1))
                    { ignored++; continue; }
                    if (receives.Count < 24000) receives.Add(new(BinaryPrimitives.ReadInt32LittleEndian(result.Buffer.AsSpan(8)), at));
                    else overflow++;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            await Task.Run(() =>
            {
                using var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x001F0003);
                if (timer.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var start = ProbeClock.Now;
                for (var sequence = 0; sequence < schedule.Length; sequence++)
                {
                    var due = start + schedule[sequence].At;
                    while (ProbeClock.Now < due)
                    {
                        var delay = -Math.Max(1L, (long)((due - ProbeClock.Now) * 10_000_000));
                        if (!SetWaitableTimer(timer, ref delay, 0, IntPtr.Zero, IntPtr.Zero, false)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (WaitForSingleObject(timer, 1000) != 0) throw new TimeoutException("Own pacing timer failed.");
                    }
                    var packet = packets[schedule[sequence].Bytes];
                    BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), sequence);
                    var before = ProbeClock.Now; udp.Send(packet, endpoint); sends.Add(new(sequence, before, ProbeClock.Now, due, packet.Length));
                }
            });
            await Task.Delay(3000); // Only the overlapping, trimmed window is used for loss/gap analysis.
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { stop.Cancel(); try { await receive; } catch (Exception ex) { error ??= ex.ToString(); } }
        if (overflow > 0 || receives.Count < seconds * 50) error ??= "Incomplete/overflowed UDP reception; do not analyze as a complete pair.";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            role, peer, localPort, peerPort, payloadBytes = planPath is null ? (int?)bytes : null, seconds, error, ignored, overflow,
            pattern = planPath is null ? "fixed" : uniform ? "uniform" : "replay", planHash,
            generatedAt = DateTimeOffset.Now, sends, receives,
            note = "Synthetic bytes discarded after header validation. Fixed or recorded high-resolution timer schedule; no global timeBeginPeriod. Due/actual times and size are stored. No audio/codec/WebRTC. QPC clocks are host-local; compare matched sequence intervals, not cross-host absolute times. Independent process starts do not preserve original cross-host flow phase."
        }, DiagnosticJson.Readable));
        Console.WriteLine($"UDP complete role={role} sends={sends.Count} receives={receives.Count} error={error is not null}");
        return error is null ? 0 : 1;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion, IntPtr state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
