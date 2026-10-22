using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceNative;

internal static class SenderSchedulingBenchmark
{
    public static async Task RunAsync(string output)
    {
        var results = new List<object>();
        foreach (var load in new[] { false, true })
        {
            using var busyStop = new CancellationTokenSource();
            var busy = load ? Enumerable.Range(0, Math.Min(2, Math.Max(1, Environment.ProcessorCount - 1)))
                .Select(_ => Task.Factory.StartNew(() => { while (!busyStop.IsCancellationRequested) Thread.SpinWait(1000); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray() : [];
            try
            {
                foreach (var mode in new[] { "inline-a1", "dedicated", "inline-a2" })
                    results.Add(await TrialAsync(mode, load));
            }
            finally { busyStop.Cancel(); await Task.WhenAll(busy); }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { results,
            note = "Same-host loopback UDP, 1000 producer frames, requested 2.5ms absolute high-resolution waitable timer pacing; actual rate recorded. A/B/A with zero or up to two synthetic busy threads. No global timer-resolution change, Opus, SRTP, physical audio, game or RF; not an end-to-end latency or production default qualification." }, DiagnosticJson.Readable));
        Console.WriteLine("PASS: inline/dedicated/inline loopback scheduling comparison: " + output);
    }

    private static async Task<object> TrialAsync(string mode, bool load)
    {
        const int count = 1000;
        using var receive = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receive.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var transmit = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = receive.LocalEndPoint!;
        var queue = new AudioSendQueue(1);
        var callback = new long[count]; var wait = new long[count]; var arrival = new long[count];
        var received = 0; var sent = 0;
        var packet = new byte[16]; var incoming = new byte[16];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reader = Task.Run(async () =>
        {
            try
            {
                while (received < count)
                {
                    var length = await receive.ReceiveAsync(incoming, SocketFlags.None, timeout.Token);
                    if (length != 16) throw new InvalidOperationException("Scheduling datagram length");
                    var created = BinaryPrimitives.ReadInt64LittleEndian(incoming.AsSpan(8));
                    arrival[received++] = Stopwatch.GetTimestamp() - created;
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        void Send(OutgoingAudio frame)
        {
            wait[sent] = Stopwatch.GetTimestamp() - frame.Created;
            BinaryPrimitives.WriteInt64LittleEndian(packet, frame.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(8), frame.Created);
            transmit.SendTo(packet, endpoint); sent++;
        }
        var dedicated = mode == "dedicated" ? new DedicatedAudioSender(queue, Send) : null;
        var elapsed = Stopwatch.StartNew();
        try
        {
            // A dedicated producer avoids measuring the benchmark's own async continuations.
            await Task.Factory.StartNew(() =>
            {
                using var deadline = new Deadline();
                var next = ProbeClock.Now;
                for (var i = 0; i < count; i++)
                {
                    var begin = Stopwatch.GetTimestamp();
                    if (dedicated is null) { var frame = queue.CreateFrame(packet); queue.Observe(frame); Send(frame); }
                    else { queue.Enqueue(packet); dedicated.Notify(); }
                    callback[i] = Stopwatch.GetTimestamp() - begin;
                    next += .0025; deadline.Until(next);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var producerSeconds = elapsed.Elapsed.TotalSeconds;
            for (var i = 0; i < 100 && Volatile.Read(ref received) < count; i++) await Task.Delay(10);
            dedicated?.Stop();
            if (dedicated is not null) await dedicated.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            timeout.Cancel(); await reader;
            if (dedicated?.Failure is not null) throw new InvalidOperationException(dedicated.Failure);
            return new { mode, load, requestedFrames = count, sent, received, actualFramesPerSecond = count / producerSeconds,
                dropped = queue.Snapshot().DroppedFrames, pending = queue.Snapshot().PendingFrames, mmcss = dedicated?.MmcssActive,
                producer = Summary(callback), queueWait = Summary(wait[..sent]), delivery = Summary(arrival[..received]) };
        }
        finally
        {
            dedicated?.Dispose(); timeout.Cancel(); await reader;
        }
    }

    private static object Summary(long[] values)
    {
        if (values.Length == 0) return new { count = 0 };
        Array.Sort(values);
        double Ms(long value) => value * 1000.0 / Stopwatch.Frequency;
        return new { count = values.Length, p50Ms = Ms(values[values.Length / 2]), p95Ms = Ms(values[(int)(values.Length * .95)]),
            p99Ms = Ms(values[(int)(values.Length * .99)]), maxMs = Ms(values[^1]) };
    }

    private sealed class Deadline : WaitHandle
    {
        public Deadline()
        {
            SafeWaitHandle = CreateWaitableTimerExW(IntPtr.Zero, null, 2, 0x00100002);
            if (SafeWaitHandle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Until(double at)
        {
            var remaining = at - ProbeClock.Now;
            if (remaining <= 0) return;
            var due = -Math.Max(1, (long)Math.Ceiling(remaining * 10000000));
            if (!SetWaitableTimer(SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) || !WaitOne(1000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion,
            IntPtr state, [MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
