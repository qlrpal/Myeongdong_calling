using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace VoiceNative;

internal static class IcmpProfile
{
    public static async Task RunAsync(string target, string gateway, string path)
    {
        var addresses = new[] { IPAddress.Parse(target), IPAddress.Parse(gateway), IPAddress.Loopback };
        var heartbeat = new List<object>();
        var thread = new Thread(() =>
        {
            var clock = Stopwatch.StartNew(); var previous = ProbeClock.Now;
            while (clock.Elapsed.TotalSeconds < 125)
            {
                Thread.Sleep(1);
                var now = ProbeClock.Now;
                heartbeat.Add(new { at = now, intervalMs = (now - previous) * 1000 });
                previous = now;
            }
        })
        { IsBackground = true, Name = "Voice diagnostic scheduling control" };
        thread.Start();
        var rows = await Task.WhenAll(addresses.Select(async address =>
        {
            using var ping = new Ping();
            var samples = new List<object>(); var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 125)
            {
                var start = ProbeClock.Now;
                try
                {
                    var reply = await ping.SendPingAsync(address, 1000, new byte[32]);
                    samples.Add(new
                    {
                        startedAt = start,
                        completedAt = ProbeClock.Now,
                        status = reply.Status.ToString(),
                        kernelRttMs = reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null
                    });
                }
                catch (PingException error) { samples.Add(new { startedAt = start, completedAt = ProbeClock.Now, error = error.Message }); }
                await Task.Delay(20);
            }
            return new { target = address.ToString(), samples };
        }));
        thread.Join();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now,
            rows,
            heartbeat,
            note = "ICMP control only. Kernel RTT and managed completion differ; ICMP is not RTP and concurrent probing may change network behavior. Loopback avoids the physical network. Dedicated thread Sleep(1) intervals include Windows timer resolution and scheduling, not solely CPU stalls."
        }, DiagnosticJson.Readable));
    }
}
