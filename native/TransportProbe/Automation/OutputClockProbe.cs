using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;
using NAudio.CoreAudioApi;

namespace VoiceNative;

internal static partial class RenderLatencyProbe
{
    private sealed record ClockPoint(double ObservedAt, ulong Position, ulong Qpc, long Written, int Padding);
    private sealed record ClockResult(object Report, int Valid);

    // Direct native-format samples: excludes RTP, decoder, channel conversion and resampler.
    public static async Task ClockAsync(string output, bool focused = false, bool acoustic = false)
    {
        var results = new List<object>();
        var complete = true;
        var modes = new[] {
            ("normal20-A1", false, 20, false), ("raw20", true, 20, false),
            ("normal5", false, 5, false), ("process20", false, 20, true),
            ("native-no-convert", false, 20, false), ("effects-off", false, 20, false), ("normal20-A2", false, 20, false),
            ("acoustic-raw", false, 20, false), ("acoustic-standard", false, 20, false) }
            .Where(mode => acoustic ? mode.Item1.StartsWith("acoustic-", StringComparison.Ordinal)
                : !mode.Item1.StartsWith("acoustic-", StringComparison.Ordinal) && (!focused || mode.Item1 is "native-no-convert" or "effects-off" or "normal20-A2")).ToArray();
        foreach (var (name, rawCapture, bufferMs, process) in modes)
        {
            try
            {
                var result = await Task.Run(() => ClockRound(name, rawCapture, bufferMs, process));
                results.Add(result.Report);
                complete &= process || result.Valid >= 4;
                Console.WriteLine($"Output clock {name}: valid markers={result.Valid}/6");
            }
            catch (Exception error) when ((rawCapture || process) && error is ArgumentException or NotSupportedException or COMException)
            {
                results.Add(new { name, unsupported = error.ToString() });
                Console.WriteLine($"Output clock {name}: unsupported");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                results, comparisonComplete = complete && results.Count == modes.Length,
                notes = "Native float shared RAW GameChat render; no RTP/codec/provider conversion. IAudioClock position/frequency mapped to QPC, not physical DAC time. Acoustic variants include microphone/room/input processing, not a one-way output measurement. Capture variants change this probe only. Audio samples transient bounded memory only, never saved. Unsupported RAW capture is recorded, not silently replaced. Optional process-loopback failures are retained but excluded from conclusions. An empty effects-service list is not proof of no endpoint APOs."
            }, DiagnosticJson.Readable));
        }
        if (!complete) throw new InvalidOperationException("Insufficient valid markers; this is not a successful latency measurement.");
    }

    private static ClockResult ClockRound(string name, bool rawCapture, int bufferMs, bool process)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var client = endpoint.CreateAudioClient();
        client.SetClientProperties(AudioStreamCategory.GameChat, AudioClientStreamOptions.Raw);
        var format = client.MixFormat;
        using IClockCapture capture = name == "native-no-convert" || name.StartsWith("acoustic-", StringComparison.Ordinal)
            ? new NativeLoopback(endpoint.ID, name.StartsWith("acoustic-", StringComparison.Ordinal), name == "acoustic-raw")
            : new Capture(endpoint.ID, rawCapture, bufferMs, process);
        if (capture.SampleRate != format.SampleRate) throw new NotSupportedException("Clock comparison requires equal native sample rates.");
        if (format.BitsPerSample != 32 || format.Channels is < 1 or > 2)
            throw new NotSupportedException("Native float mono/stereo endpoint required.");
        var rate = format.SampleRate;
        var period = client.GetSharedModeEnginePeriod(format).ChooseLowestLatencyPeriod();
        client.InitializeSharedAudioStream(AudioClientStreamFlags.EventCallback, period, format, Guid.Empty);
        var effects = InspectEffects(client, name == "effects-off");
        using var ready = new AutoResetEvent(false);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var clock = client.AudioClockClient;
        var frequency = clock.Frequency;
        if (frequency == 0) throw new InvalidOperationException("Invalid render clock frequency.");
        var signal = new float[rate * 9];
        var markers = Enumerable.Range(2, 6).Select(second => second * rate).ToArray();
        foreach (var marker in markers)
            for (var i = 0; i < rate / 10; i++)
            {
                var t = i / (double)rate;
                var envelope = Math.Min(1, Math.Min(i / 96.0, (rate / 10 - i) / 192.0));
                signal[marker + i] = (float)(.12 * envelope * Math.Sin(2 * Math.PI * (500 * t + 15000 * t * t)));
            }
        long written = 0;
        var points = new List<ClockPoint>();
        var commits = new List<object>();
        var empty = 0;
        var render = client.AudioRenderClient;
        void Fill(int frames)
        {
            using var lease = render.GetBufferLease(frames, format.BlockAlign);
            var floats = MemoryMarshal.Cast<byte, float>(lease.Buffer);
            for (var i = 0; i < frames; i++)
                for (var channel = 0; channel < format.Channels; channel++)
                    floats[i * format.Channels + channel] = written + i < signal.Length ? signal[written + i] : 0;
            var first = written;
            lease.Release(frames); written += frames;
            foreach (var marker in markers.Where(marker => marker >= first && marker < written))
                commits.Add(new { marker, releasedAt = ProbeClock.Now, first, frames });
        }
        Fill(client.BufferSize);
        var startedAt = ProbeClock.Now;
        try
        {
            client.Start();
            while (ProbeClock.Now - startedAt < 9.3)
            {
                if (!ready.WaitOne(1000)) throw new TimeoutException("Render event timeout.");
                clock.GetPosition(out var position, out var qpc);
                var padding = client.CurrentPadding;
                points.Add(new(ProbeClock.Now, position, qpc, written, padding));
                if (padding == 0) empty++;
                var count = client.BufferSize - padding;
                if (count > 0) Fill(count);
            }
        }
        finally { client.Stop(); }
        var chunks = capture.Stop();
        var measurements = new List<object>(); var valid = 0;
        foreach (var marker in markers)
        {
            var near = points.Where(p => p.Position > 0 && p.Qpc > 0)
                .MinBy(p => Math.Abs(p.Position / (double)frequency - marker / (double)rate))
                ?? throw new InvalidOperationException("No valid render clock sample.");
            var predicted = near.Qpc / 10000000.0 + marker / (double)rate - near.Position / (double)frequency;
            var window = chunks.Where(c => c.At + c.Samples.Length / (double)rate > predicted - .05 && c.At < predicted + .4).ToArray();
            var samples = window.SelectMany(c => c.Samples).ToArray();
            var match = Find(samples, signal.AsSpan(marker, 2048));
            var index = match.Offset;
            var containing = window[0];
            foreach (var chunk in window) { containing = chunk; if (index < chunk.Samples.Length) break; index -= chunk.Samples.Length; }
            var matchedAt = containing.At + index / (double)rate;
            var discontinuous = window.Any(c => (c.Flags & (int)(AudioClientBufferFlags.DataDiscontinuity | AudioClientBufferFlags.TimestampError)) != 0)
                || window.Zip(window.Skip(1)).Any(pair => pair.First.Position + pair.First.Samples.Length != pair.Second.Position);
            var accepted = match.Correlation >= .7 && !discontinuous && capture.Error is null;
            if (accepted) valid++;
            measurements.Add(new { marker, predicted, near, match.Correlation, matchedAt,
                callbackAt = containing.CallbackAt,
                renderClockToLoopbackQpcMs = (matchedAt - predicted) * 1000,
                renderClockToCallbackMs = (containing.CallbackAt - predicted) * 1000,
                captureQpcAgeMs = (containing.CallbackAt - containing.At) * 1000,
                discontinuous, valid = accepted,
                absoluteTimingTrusted = accepted && containing.At <= containing.CallbackAt + .001 && predicted <= containing.CallbackAt + .001 });
        }
        var firstPoint = points.First(p => p.Position > 0); var lastPoint = points[^1];
        return new(new { name, validMarkers = valid, rawCapture, bufferMs, process, endpointId = endpoint.ID, effects, device = endpoint.FriendlyName, format = format.ToString(),
            rate, period, capacity = client.BufferSize, frequency, streamLatency100ns = client.StreamLatency,
            startedAt, empty, captureError = capture.Error, commits, measurements,
            captureChunks = chunks.Select(c => new { c.At, c.CallbackAt, c.Position, c.Flags, samples = c.Samples.Length, peak = c.Samples.Select(x => Math.Abs(x)).DefaultIfEmpty().Max() }).ToArray(),
            clockRateRatio = (lastPoint.Position - firstPoint.Position) / (double)frequency / ((lastPoint.Qpc - firstPoint.Qpc) / 10000000.0),
            points }, valid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEffect { public Guid Id; public int CanSetState; public int State; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEffects(IntPtr self, out IntPtr effects, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetEffect(IntPtr self, Guid effect, int state);
    private static object InspectEffects(AudioClient client, bool disable)
    {
        // Diagnostic-only bridge for the pinned NAudio version, whose wrapper has no effects service API.
        // The Windows interface and vtable are verified against SDK 10.0.26100 audioclient.h.
        var field = typeof(AudioClient).GetField("audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(client) is not { } native || field.FieldType.GetMethod("GetService") is not { } service)
            return new { unsupported = "NAudio service bridge unavailable" };
        object?[] arguments = [new Guid("4460B3AE-4B44-4527-8676-7548A8ACD260"), IntPtr.Zero];
        var hr = (int)service.Invoke(native, arguments)!;
        var manager = (IntPtr)arguments[1]!;
        if (hr < 0) return new { unsupported = $"IAudioEffectsManager HRESULT 0x{hr:X8}" };
        IntPtr array = IntPtr.Zero;
        try
        {
            var vtable = Marshal.ReadIntPtr(manager);
            var get = Marshal.GetDelegateForFunctionPointer<GetEffects>(Marshal.ReadIntPtr(vtable, 5 * IntPtr.Size));
            var set = Marshal.GetDelegateForFunctionPointer<SetEffect>(Marshal.ReadIntPtr(vtable, 6 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(get(manager, out array, out var count));
            if (count > 128) throw new InvalidOperationException("Unexpected effects list length.");
            var list = new List<object>();
            for (var i = 0; i < count; i++)
            {
                var effect = Marshal.PtrToStructure<NativeEffect>(array + i * Marshal.SizeOf<NativeEffect>());
                int? disableHr = disable && effect.CanSetState != 0 && effect.State != 0 ? set(manager, effect.Id, 0) : null;
                list.Add(new { effect.Id, effect.CanSetState, effect.State, disableHr });
            }
            return new { disableRequested = disable, effects = list };
        }
        finally { if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array); Marshal.Release(manager); }
    }

    // Same device mix format, with no AUTOCONVERTPCM/SRC_DEFAULT_QUALITY flags.
    private sealed class NativeLoopback : IClockCapture
    {
        private readonly MMDevice endpoint;
        private readonly AudioClient client;
        private readonly AutoResetEvent ready = new(false);
        private readonly ManualResetEvent stop = new(false);
        private readonly Task worker;
        private readonly List<Chunk> chunks = new();
        public int SampleRate { get; }
        public string? Error { get; private set; }
        public NativeLoopback(string id, bool microphone = false, bool raw = false)
        {
            using var enumerator = new MMDeviceEnumerator();
            endpoint = microphone ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : enumerator.GetDevice(id);
            client = endpoint.CreateAudioClient();
            try
            {
                var mix = client.MixFormat; SampleRate = mix.SampleRate;
                if (mix.BitsPerSample != 32 || !(mix.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                    || mix is NAudio.Wave.WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")))
                    throw new NotSupportedException("Native float capture required.");
                if (raw) client.SetClientProperties(AudioStreamCategory.Other, AudioClientStreamOptions.Raw);
                client.Initialize(AudioClientShareMode.Shared, (microphone ? AudioClientStreamFlags.None : AudioClientStreamFlags.Loopback) | AudioClientStreamFlags.EventCallback,
                    200000, 0, mix, Guid.Empty);
                client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
                worker = Task.Run(() =>
                {
                    try
                    {
                        var input = client.AudioCaptureClient; var frames = 0;
                        client.Start();
                        WaitHandle[] signals = [stop, ready];
                        while (WaitHandle.WaitAny(signals, 60) != 0)
                            while (input.GetNextPacketSize() > 0)
                            {
                                using var lease = input.GetBufferLease(mix.BlockAlign);
                                var at = ProbeClock.Now; var count = lease.Buffer.Length / mix.BlockAlign;
                                if (frames + count > SampleRate * 15) throw new InvalidOperationException("Native loopback memory limit.");
                                var mono = new float[count];
                                if ((lease.Flags & AudioClientBufferFlags.Silent) == 0)
                                {
                                    var data = MemoryMarshal.Cast<byte, float>(lease.Buffer);
                                    for (var i = 0; i < count; i++)
                                        for (var c = 0; c < mix.Channels; c++) mono[i] += data[i * mix.Channels + c] / mix.Channels;
                                }
                                chunks.Add(new(lease.QPCPosition / 10000000.0, at, lease.DevicePosition, (int)lease.Flags, mono)); frames += count;
                            }
                    }
                    catch (Exception error) { Error = error.ToString(); }
                    finally { try { client.Stop(); } catch (COMException) { } }
                });
            }
            catch
            {
                client.Dispose(); endpoint.Dispose(); ready.Dispose(); stop.Dispose(); throw;
            }
        }
        public Chunk[] Stop() { stop.Set(); worker.GetAwaiter().GetResult(); return chunks.ToArray(); }
        public void Dispose() { Stop(); client.Dispose(); endpoint.Dispose(); ready.Dispose(); stop.Dispose(); }
    }
}
