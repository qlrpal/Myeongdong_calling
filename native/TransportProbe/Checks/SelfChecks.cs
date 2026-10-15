using System.Text.Json;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

internal static class SelfChecks
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args is ["--opus-comparison", var codecReport])
        {
            OpusComparison.Run(codecReport);
            return true;
        }
        if (args is ["--codec-api-inspect"])
        {
            using var codec = new AudioEncoder(includeOpus: true);
            codec.EncodeAudio(new short[480], codec.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS));
            foreach (var field in typeof(AudioEncoder).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (field.GetValue(codec) is Concentus.IOpusEncoder opus)
                    Console.WriteLine($"Baseline {field.Name}: type={opus.GetType().Name}, application={opus.Application}, bitrate={opus.Bitrate}, lookahead={opus.Lookahead}, complexity={opus.Complexity}, VBR={opus.UseVBR}");
            }
            foreach (var method in typeof(Concentus.IOpusEncoder).GetMethods().Where(m => m.Name == "Encode")) Console.WriteLine(method);
            foreach (var property in typeof(IAudioEncoder).GetProperties()) Console.WriteLine(property);
            return true;
        }
        if (args is ["--native-converter-test"])
        {
            var converter = new VoiceNative.NativeCaptureConverter(NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2));
            var floats = new[] { 1f, -1f, -0.5f, -0.5f, 1f, 1f, float.NaN, float.NaN };
            var raw = new byte[floats.Length * 4]; Buffer.BlockCopy(floats, 0, raw, 0, raw.Length);
            var converted = new byte[8]; converter.Convert(raw, converted);
            var samples = new short[4]; Buffer.BlockCopy(converted, 0, samples, 0, 8);
            if (!samples.SequenceEqual(new short[] { 0, -16384, 32767, 0 })) throw new InvalidOperationException("Native float mix/clipping/nonfinite conversion failed.");
            try { _ = new VoiceNative.NativeCaptureConverter(new NAudio.Wave.WaveFormat(44100, 16, 1)); throw new InvalidOperationException("Unsupported rate must request fallback."); }
            catch (NotSupportedException) { }
            Console.WriteLine("PASS: native stereo float downmix, signed PCM scale, clipping, nonfinite input and unsupported-rate fallback.");
            return true;
        }
        if (args is ["--codec-delay-test"])
        {
            using var encode = new AudioEncoder(includeOpus: true); using var decode = new AudioEncoder(includeOpus: true);
            var codecFormat = encode.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
            var random = new Random(3241); var original = new List<short>(); var decoded = new List<short>();
            for (var frame = 0; frame < 12; frame++)
            {
                var pcm = Enumerable.Range(0, 960).Select(_ => frame < 8 ? (short)random.Next(-6000, 6001) : (short)0).ToArray();
                original.AddRange(pcm); decoded.AddRange(decode.DecodeAudio(encode.EncodeAudio(pcm, codecFormat), codecFormat));
            }
            var bestLag = 0; double best = -1;
            for (var lag = 0; lag <= 960; lag++)
            {
                double sum = 0, leftEnergy = 0, rightEnergy = 0;
                for (var i = 960; i < 7 * 960; i++)
                { double originalSample = original[i], decodedSample = decoded[i + lag]; sum += originalSample * decodedSample; leftEnergy += originalSample * originalSample; rightEnergy += decodedSample * decodedSample; }
                var score = sum / Math.Sqrt(leftEnergy * rightEnergy);
                if (score > best) { best = score; bestLag = lag; }
            }
            if (best < 0.2) throw new InvalidOperationException("No reliable codec alignment peak.");
            Console.WriteLine($"Measured codec sample displacement: {bestLag} samples / {bestLag / 48.0:F3}ms; normalized correlation {best:F3}. Synthetic signal only, not physical device latency.");
            return true;
        }
        if (args is ["--frame-timeline-test"])
        {
            if (VoiceNative.AudioFrameTimeline.IsDetectionCoherent(100, 99.98, true)
                || VoiceNative.AudioFrameTimeline.IsDetectionCoherent(100, 102.9, true)
                || !VoiceNative.AudioFrameTimeline.IsDetectionCoherent(100, 100.06, true))
                throw new InvalidOperationException("Detection must correspond to an already submitted probe, within the supported loopback window.");
            var epoch = new VoiceNative.AudioFrameTimeline(); epoch.EnableFor(8);
            epoch.Update("same", old => old with { Receipts = 2 }); epoch.EnableFor(8);
            if (epoch.Find("same") is not null) throw new InvalidOperationException("Probe history must reset between measurements.");
            var source = new VoiceNative.AudioFrameStages("A", CaptureAt: 10, EncodeStart: 10.02, EncodeEnd: 10.021, SentAt: 10.022);
            var target = new VoiceNative.AudioFrameStages("A", ReceivedAt: 30.024, DecodeStart: 30.025, DecodeEnd: 30.026,
                QueuedAt: 30.027, OutputSubmittedAt: 30.067, Receipts: 1);
            var split = VoiceNative.AudioFrameTimeline.Breakdown(source, target, 19.999, 20.001, 30.077);
            if (Math.Abs(split.PcmQueueMs!.Value - 40) > 0.001 || Math.Abs(split.NetworkMinimumMs!.Value - 1) > 0.001
                || Math.Abs(split.NetworkMaximumMs!.Value - 3) > 0.001) throw new InvalidOperationException("Frame stage breakdown clock math is incorrect.");
            var buffer = new VoiceNative.PcmPlayoutBuffer();
            var submitted = new List<(string Hash, bool Discarded)>();
            buffer.OnFrameSubmitted = (hash, at, discarded) => submitted.Add((hash, discarded));
            buffer.AddSamples(new byte[1920], "first"); buffer.AddSamples(new byte[1920], "second");
            buffer.Read(new byte[1920]);
            if (submitted.Count != 1 || submitted[0] != ("first", false)) throw new InvalidOperationException("PCM markers must follow the submitted samples.");
            buffer.Suspend();
            if (submitted.Count != 2 || submitted[1] != ("second", true)) throw new InvalidOperationException("Suspended samples must be marked discarded.");
            Console.WriteLine("PASS: cross-clock stage bounds, exact PCM frame submission and discarded-frame markers.");
            return true;
        }
        if (args is ["--latency-summary-test"])
        {
            double[] outliers = [187.0, 100.0, 97.0, 101.0, 104.0];
            double[] even = [100.0, 98.0, 104.0, 102.0];
            if (VoiceNative.LocalLatencyProbe.Median(outliers) != 101
                || VoiceNative.LocalLatencyProbe.Median(even) != 101)
                throw new InvalidOperationException("Median must handle outliers and even sample counts.");
            Console.WriteLine("PASS: latency summary median for outliers and even sample counts.");
            return true;
        }
        if (args is ["--local-latency-test"])
        {
            var trials = new List<VoiceNative.LocalLatencyTrial>();
            var events = new List<string>();
            await VoiceNative.LocalLatencyProbe.RunAsync(message => { events.Add(message); Console.WriteLine(message); }, trials.Add, CancellationToken.None);
            var directory = Path.Combine(Environment.CurrentDirectory, "native", ".local");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "local-latency-latest.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.Now, trials, events }, VoiceNative.DiagnosticJson.Readable));
            if (trials.Count == 0) throw new InvalidOperationException("No acoustic detection; physical speaker/microphone path must be checked. No latency value fabricated.");
            Console.WriteLine($"Measured {trials.Count}/5 trials; saved {path}");
            return true;
        }
        if (args is ["--capture-clock-test"])
        {
            using var clockEncoder = new AudioEncoder(includeOpus: true);
            var clockCapture = new VoiceNative.WindowsAudioDevice(clockEncoder, disableSink: true);
            try
            {
                await clockCapture.StartAudio();
                var stamp = await clockCapture.Acoustic.Inject().WaitAsync(TimeSpan.FromSeconds(2));
                var age = (VoiceNative.ProbeClock.Now - stamp) * 1000;
                if (age is < 0 or > 500) throw new InvalidOperationException("WASAPI QPC sample time does not align with the probe clock.");
                Console.WriteLine($"PASS: actual microphone QPC sample clock aligns with diagnostic monotonic clock; observed sample age {age:F2}ms. No audio was played or saved.");
            }
            finally { await clockCapture.Close(); }
            return true;
        }
        if (args is ["--acoustic-probe-test"] or ["--ten-ms-acoustic-probe-test"])
        {
            var bounds = VoiceNative.ProbeClock.Bounds(100, 120.003, 120.004, 100.009);
            if (bounds is null || Math.Abs(bounds.Value.Low - 19.995) > 0.000001 || Math.Abs(bounds.Value.High - 20.003) > 0.000001)
                throw new InvalidOperationException("Clock offset bounds are incorrect.");
            if (VoiceNative.ProbeClock.Bounds(100, 120, 119, 101) is not null) throw new InvalidOperationException("Invalid remote time ordering was accepted.");
            var acousticSamples = args[0] == "--ten-ms-acoustic-probe-test" ? 480 : 960;
            var generator = new VoiceNative.AcousticCaptureProbe(); var detector = new VoiceNative.AcousticCaptureProbe();
            var detected = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
            detector.Arm(Guid.NewGuid().ToString("N"), stamp => detected.TrySetResult(stamp));
            using var toneEncoder = new AudioEncoder(includeOpus: true); using var toneDecoder = new AudioEncoder(includeOpus: true);
            var opus = toneEncoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
            var created = generator.Inject();
            var random = new Random(17);
            for (var frameIndex = 0; frameIndex < 19200 / acousticSamples; frameIndex++)
            {
                var pcm = new short[acousticSamples]; generator.Process(pcm, 100 + frameIndex * acousticSamples / 48000.0);
                var decodedTone = toneDecoder.DecodeAudio(toneEncoder.EncodeAudio(pcm, opus), opus);
                for (var i = 0; i < decodedTone.Length; i++) decodedTone[i] = (short)(decodedTone[i] * 0.02 + random.Next(-2, 3));
                detector.Process(decodedTone, 100.075 + frameIndex * acousticSamples / 48000.0);
            }
            var start = await created; var arrival = await detected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (arrival - start is < 0.070 or > 0.095) throw new InvalidOperationException("Tone detection timing is outside the stated resolution.");
            var noiseDetector = new VoiceNative.AcousticCaptureProbe(); var falseDetection = false;
            noiseDetector.Arm(Guid.NewGuid().ToString("N"), _ => falseDetection = true);
            for (var i = 0; i < 10; i++) noiseDetector.Process(Enumerable.Range(0, 960).Select(_ => (short)random.Next(-1000, 1001)).ToArray(), 200 + i * 0.020);
            await Task.Delay(50);
            if (falseDetection) throw new InvalidOperationException("Broadband noise falsely triggered the probe.");
            Console.WriteLine($"PASS: clock bounds, Opus tone detection under attenuation/noise, timing resolution and broadband-noise rejection; detected {(arrival - start) * 1000:F1}ms.");
            return true;
        }
        if (args is ["--latency-model-test"])
        {
            if (VoiceNative.TelemetryState.Model(20, 1, 2, 10, 40, 30, 1) != 119 || VoiceNative.TelemetryState.Model(null, 1, 2, 10, 40, 30, 1) is not null)
                throw new InvalidOperationException("Latency model must keep missing values unknown.");
            var state = new VoiceNative.TelemetryState();
            if (state.Receive(new byte[3000]) || state.Receive(System.Text.Encoding.UTF8.GetBytes("invalid"))) throw new InvalidOperationException("Malformed telemetry was accepted.");
            var report = new VoiceNative.LatencyTelemetry(1, 20, 1, 2, 40, 30, 1, "재생");
            if (!state.Receive(JsonSerializer.SerializeToUtf8Bytes(report)) || state.Snapshot().Remote != report)
                throw new InvalidOperationException("Telemetry roundtrip failed.");
            if (state.Receive(JsonSerializer.SerializeToUtf8Bytes(report with { InputApiMs = -1 }))) throw new InvalidOperationException("Invalid latency was accepted.");
            Console.WriteLine("PASS: labelled latency model, missing observations, telemetry limits, roundtrip and negative-value rejection.");
            return true;
        }
        if (args is ["--send-queue-test"])
        {
            var queue = new VoiceNative.AudioSendQueue();
            for (var i = 0; i < 6; i++) queue.Enqueue(new byte[] { (byte)i });
            queue.Complete(); var sequences = new List<long>();
            await foreach (var frame in queue.Read(CancellationToken.None)) sequences.Add(frame.Sequence);
            if (!sequences.SequenceEqual(new long[] { 4, 5, 6 }) || queue.Snapshot().DroppedFrames != 3 || queue.Snapshot().PendingFrames != 0)
                throw new InvalidOperationException("Bounded sender did not retain the newest frames.");
            if (VoiceNative.AudioSendQueue.Timestamp(100, 1, 4) != 2980 || VoiceNative.AudioSendQueue.Timestamp(uint.MaxValue - 959, 1, 2) != 0)
                throw new InvalidOperationException("Dropped frame timestamp accounting is incorrect.");
            Console.WriteLine("PASS: bounded nonblocking sender, latest-frame retention, drop counters, completion and RTP skip accounting.");
            return true;
        }
        if (args is ["--capture-test"] or ["--ten-ms-capture-test"] or ["--opus-capture-test", _])
        {
            var tenMsCapture = args[0] != "--capture-test";
            var captureProfile = args.Length == 2 ? Enum.Parse<VoiceNative.OpusProfile>(args[1]) : VoiceNative.OpusProfile.Baseline;
            using var captureEncoder = new VoiceNative.OpusAudioEncoder(captureProfile);
            var capture = new VoiceNative.WindowsAudioDevice(captureEncoder, disableSink: true, frameMs: tenMsCapture ? 10 : 20);
            var frames = 0; string? error = null;
            capture.OnAudioSourceEncodedSample += (_, _) => Interlocked.Increment(ref frames);
            capture.OnAudioSourceError += message => error = message;
            try
            {
                await capture.StartAudio();
                for (var i = 0; i < 100; i++) { capture.Snapshot(); await Task.Delay(30); }
                var stats = capture.Snapshot();
                if (error is not null || frames < (tenMsCapture ? 250 : 100)) throw new InvalidOperationException(error ?? $"Insufficient microphone frames: {frames}");
                Console.WriteLine("Capture codec: " + JsonSerializer.Serialize(captureEncoder.Snapshot()));
                Console.WriteLine($"PASS: actual WASAPI microphone generated {frames} Opus frames in 3s; callback P95={stats.CaptureGapP95Ms:F2}ms max={stats.CaptureGapMaxMs:F2}ms. Audio was not saved. LowLatency={stats.LowLatencyActive}, period={stats.EnginePeriodMs}ms, API={stats.DeviceAverageLatencyMs:F2}ms, fallback={stats.LowLatencyUnavailableReason}");
            }
            finally { await capture.Close(); }
            return true;
        }

        if (args is ["--device-restart-test"] or ["--standard-device-restart-test"])
        {
            using var codec = new AudioEncoder(includeOpus: true);
            var device = new VoiceNative.WindowsAudioDevice(codec, disableSource: true, preferLowLatency: args[0] != "--standard-device-restart-test");
            using var source = new AudioEncoder(includeOpus: true);
            var opus = source.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
            var silence = source.EncodeAudio(new short[960], opus);
            try
            {
                await device.StartAudioSink();
                var expectedRestarts = 0;
                for (var i = 0; i < 100; i++)
                {
                    if (i == 30) { if (device.Snapshot().LowLatencyActive != true) expectedRestarts++; await device.RestartOutputAsync(outputLatencyMs: 20); if (device.Snapshot().RequestedOutputMs != 20) throw new InvalidOperationException("20ms output request was not recorded."); }
                    if (i == 65) { if (device.Snapshot().LowLatencyActive != true) expectedRestarts++; await device.RestartOutputAsync(outputLatencyMs: 40); }
                    if (i == 80) { await device.RestartOutputAsync(); expectedRestarts++; }
                    device.GotEncodedMediaFrame(new EncodedAudioFrame(0, opus, 20, silence));
                    await Task.Delay(20);
                }
                var state = device.Snapshot();
                if (state.PlaybackState != "재생" || state.DeviceRestarts != expectedRestarts || state.RequestedOutputMs != 40 || state.QueueResets != 0 || state.DeviceError is not null)
                    throw new InvalidOperationException("Device restart did not restore healthy playback.");
                Console.WriteLine($"PASS: actual shared WASAPI output switched 40->20->40 during a silent stream; LowLatency={state.LowLatencyActive}, period={state.EnginePeriodMs}ms, API={state.DeviceLatencyMs:F2}ms, fallback={state.LowLatencyUnavailableReason}. Playback resumed without queue trimming.");
            }
            finally { await device.Close(); }
            return true;
        }

        if (args is ["--buffer-recovery-test"])
        {
            var buffer = new VoiceNative.PcmPlayoutBuffer();
            var audio = Enumerable.Repeat((byte)1, 1920).ToArray(); var output = new byte[1920];
            buffer.AddSamples(audio); buffer.AddSamples(audio);
            buffer.Read(output); buffer.Read(output); buffer.Read(output);
            buffer.AddSamples(audio); buffer.Read(output);
            if (output.Any(b => b != 1) || buffer.Snapshot().Underruns != 1 || buffer.Snapshot().QueueMs != 0)
                throw new InvalidOperationException("Short underflow must not cause another 40ms refill delay.");
            if (buffer.Snapshot().UnderfillMs != 20 || buffer.Snapshot().MaxUnderfillMs != 20) throw new InvalidOperationException("Underfill duration must exclude startup reserve.");
            buffer.Suspend(); buffer.AddSamples(audio); buffer.Read(output);
            if (output.Any(b => b != 0) || buffer.Snapshot().QueueMs != 0) throw new InvalidOperationException("Failed device accumulated stale audio.");
            buffer.Resume(); buffer.AddSamples(audio); buffer.Read(output);
            if (output.Any(b => b != 0)) throw new InvalidOperationException("Device restart did not restore startup reserve.");
            buffer.AddSamples(audio); buffer.Read(output);
            if (output.Any(b => b != 1)) throw new InvalidOperationException("Device restart did not resume fresh audio.");
            Console.WriteLine("PASS: short underflow recovery, suspended-device discard and fresh restart reserve.");
            return true;
        }

        if (args is ["--precision-self-test"])
        {
            var metrics = new VoiceNative.RtcpMetrics();
            metrics.Packet(10, 65535, 0); metrics.Packet(10, 0, 0.02); metrics.Packet(10, 65534, 0.03); metrics.Packet(10, 0, 0.04);
            var order = metrics.Snapshot(1);
            if (order.ReorderedPackets != 1 || order.DuplicatePackets != 1 || order.RtcpRttMs is not null) throw new InvalidOperationException("Sequence wrap or missing RTT handling failed.");
            metrics.Sender(20, 1234, 10);
            var report = new ReceptionReportSample(20, 64, 2, 100, 480, 1234, 32768); // 0.5s report delay.
            metrics.Remote(report, 10.75);
            var measured = metrics.Snapshot(12);
            if (measured.RtcpRttMs != 250 || measured.RttAgeSeconds != 1.25 || measured.SendLossPercent != 25 || measured.RemoteJitterMs != 10)
                throw new InvalidOperationException("RTCP RTT, loss or jitter units are incorrect.");
            metrics.Remote(new ReceptionReportSample(99, 255, 100, 100, 0, 1234, 0), 13);
            if (metrics.Snapshot(13).SendLossPercent != 25) throw new InvalidOperationException("Foreign SSRC report contaminated statistics.");
            metrics.Local(new ReceptionReportSample(10, 32, -1, 100, 0, 0, 0), 14);
            if (metrics.Snapshot(15).ReceiveLossPercent != 12.5 || metrics.Snapshot(15).ReceivePacketsLost != -1) throw new InvalidOperationException("Inbound loss scale or signed cumulative loss failed.");
            metrics.Packet(11, 5, 16);
            if (metrics.Snapshot(16).ReceiveLossPercent is not null) throw new InvalidOperationException("Inbound SSRC change did not reset loss.");
            metrics.Sender(21, 9999, 20);
            metrics.Remote(new ReceptionReportSample(21, 0, 0, 1, 0, 9999, 65536), 20.5);
            if (metrics.Snapshot(21).RtcpRttMs is not null) throw new InvalidOperationException("Negative RTT must not be displayed as zero.");
            var timing = new VoiceNative.ProcessingWindow();
            for (var i = 1; i <= 100; i++) timing.Add(i);
            if (timing.Snapshot().P95 != 95 || timing.Snapshot().Max != 100) throw new InvalidOperationException("Processing percentile failed.");
            Console.WriteLine("PASS: RTP wrap/reorder/duplicates, SSRC filtering/reset, RTCP RTT delay subtraction, report age, loss direction, negative RTT rejection, P95.");
            return true;
        }

        if (args is ["--device-playout-test"])
        {
            foreach (var mode in new[] { "WaveOut", "WASAPI" })
            {
                var buffer = new VoiceNative.PcmPlayoutBuffer();
                using NAudio.Wave.IWavePlayer output = mode == "WaveOut"
                    ? new NAudio.Wave.WaveOut { DeviceNumber = -1, BufferMilliseconds = 20, NumberOfBuffers = 2 }
                    : new NAudio.Wave.WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
                output.Init(buffer);
                var silence = new byte[1920]; buffer.AddSamples(silence); buffer.AddSamples(silence);
                output.Play();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var supplied = 0;
                while (clock.Elapsed.TotalSeconds < 5)
                {
                    var expected = (int)(clock.Elapsed.TotalMilliseconds / 20);
                    while (supplied < expected) { buffer.AddSamples(silence); supplied++; }
                    await Task.Delay(2);
                }
                output.Stop(); var snapshot = buffer.Snapshot();
                Console.WriteLine($"{mode}: elapsed={clock.ElapsedMilliseconds}ms input={snapshot.DecodedMs}ms consumed={snapshot.ConsumedMs}ms requested={snapshot.RequestedMs}ms queue={snapshot.QueueMs}ms trims={snapshot.Trims} underruns={snapshot.Underruns}");
                if (mode == "WASAPI" && (snapshot.Trims > 0 || snapshot.RequestedMs < 4700)) throw new InvalidOperationException("WASAPI playback is not consuming audio at real-time speed.");
            }
            return true;
        }

        if (args is ["--period-buffer-test"])
        {
            var buffer = new VoiceNative.PcmPlayoutBuffer(); buffer.ConfigureForOutput(true, 10);
            var frame = Enumerable.Repeat((byte)1, 960).ToArray(); var output = new byte[960];
            buffer.AddSamples(frame); buffer.Read(output);
            if (output.Any(x => x != 0) || buffer.Snapshot().Underruns != 0) throw new InvalidOperationException("Startup must wait for the validated 40ms reserve.");
            buffer.AddSamples(frame); buffer.AddSamples(frame); buffer.AddSamples(frame); buffer.Read(output);
            if (output.Any(x => x != 1)) throw new InvalidOperationException("A 10ms output period must start at 40ms reserve.");
            for (var i = 0; i < 1000; i++) { buffer.AddSamples(frame); buffer.Read(output); }
            if (buffer.Snapshot().Underruns != 0 || buffer.Snapshot().Trims != 0) throw new InvalidOperationException("10ms steady playout must not drop or underflow.");
            for (var i = 0; i < 30; i++) buffer.AddSamples(frame);
            if (buffer.Snapshot().QueueMs > 80 || buffer.Snapshot().QueueMs < 40 || buffer.Snapshot().Trims == 0) throw new InvalidOperationException("Low-latency queue bound failed.");
            if (VoiceNative.AudioSendQueue.Timestamp(1000, 1, 4, 480) != 2440) throw new InvalidOperationException("10ms skipped-frame RTP clock is incorrect.");
            Console.WriteLine("PASS: 40ms reserve, 10ms period FIFO/steady playback, 80ms backlog bound and 480-tick RTP gaps.");
            return true;
        }
        if (args is ["--low-latency-buffer-test"])
        {
            var ring = new VoiceNative.PcmPlayoutBuffer(); var frame = new byte[1920]; var output = new byte[1920];
            for (var i = 0; i < 1000; i++)
            {
                Array.Fill(frame, (byte)(i % 127)); ring.AddSamples(frame);
                if (i == 0) continue;
                ring.Read(output);
                if (output.Any(x => x != (byte)((i - 1) % 127))) throw new InvalidOperationException("Ring wrap corrupted FIFO PCM order.");
            }
            var marked = new List<(string Hash, bool Discarded)>();
            ring.OnFrameSubmitted = (hash, at, discarded) => marked.Add((hash, discarded));
            for (var i = 0; i < 12; i++) ring.AddSamples(new byte[1920], "burst-" + i);
            var state = ring.Snapshot();
            if (state.QueueMs is < 40 or > 80 || state.Trims == 0 || !marked.Any(x => x.Discarded))
                throw new InvalidOperationException("Late speech burst did not discard stale PCM while retaining a playable reserve.");
            for (var i = 0; i < 10; i++) ring.Read(output);
            if (ring.Snapshot().Underruns != 1) throw new InvalidOperationException("A contiguous underflow was counted repeatedly.");
            Console.WriteLine("PASS: PCM ring wrap/FIFO, 80ms backlog bound, retained reserve, dropped probe markers and underflow accounting.");
            return true;
        }
        if (args is ["--playout-self-test"])
        {
            var buffer = new VoiceNative.PcmPlayoutBuffer();
            var frame = Enumerable.Repeat((byte)1, 1920).ToArray();
            var output = new byte[1920];
            buffer.AddSamples(frame); buffer.Read(output);
            if (output.Any(x => x != 0)) throw new InvalidOperationException("Startup reserve was not respected.");
            buffer.AddSamples(frame); buffer.Read(output);
            if (output.Any(x => x != 1)) throw new InvalidOperationException("Buffered audio was not played.");
            for (var i = 0; i < 500; i++) { buffer.AddSamples(frame); buffer.Read(output); }
            var steady = buffer.Snapshot();
            if (steady.Trims != 0 || steady.Underruns != 0 || steady.QueueMs != 20) throw new InvalidOperationException("Steady playback must not discard audio.");
            for (var i = 0; i < 2; i++) buffer.AddSamples(frame); // 60ms burst stays intact.
            if (buffer.Snapshot().Trims != 0) throw new InvalidOperationException("Ordinary bursts must not clear playback.");
            for (var i = 0; i < 20; i++) buffer.AddSamples(frame);
            if (buffer.Snapshot().QueueMs is < 40 or > 80 || buffer.Snapshot().Trims == 0) throw new InvalidOperationException("Large backlog must retain playable audio without stale speech exceeding 80ms.");
            for (var i = 0; i < 20; i++) buffer.Read(output);
            if (buffer.Snapshot().Underruns != 1) throw new InvalidOperationException("Underflow should be counted once before refilling.");
            Console.WriteLine("PASS: startup reserve, 10s continuous playback, burst preservation, bounded backlog and underflow recovery.");
            return true;
        }

        if (args is ["--metrics-self-test"])
        {
            var metrics = new VoiceNative.PeerMetrics();
            var first = metrics.Sample(0);
            if (first.TxKbps is not null || first.RxKbps is not null || first.Jitter is not null || first.Loss is not null)
                throw new InvalidOperationException("Unmeasured diagnostics must remain null.");
            metrics.Sent(1000);
            metrics.Received(1, uint.MaxValue - 959, 1000, 0);
            metrics.Received(1, 0, 1000, 0.020); // RTP timestamp wrap, perfect 20ms spacing.
            var second = metrics.Sample(2);
            if (second.TxKbps != 4 || second.RxKbps != 8 || second.Jitter != 0)
                throw new InvalidOperationException("Interval rates or RTP timestamp wrap are incorrect.");
            metrics.Received(1, 960, 1000, 0.050); // 10ms arrival variation => RFC jitter update 10/16.
            var third = metrics.Sample(3);
            if (Math.Abs(third.Jitter!.Value - 0.625) > 0.000001 || third.TxKbps != 0 || third.RxKbps != 8)
                throw new InvalidOperationException("Jitter or interval reset is incorrect.");
            metrics.Received(2, 900000, 1000, 0.070); // New SSRC must not inherit old jitter.
            if (metrics.Sample(4).Jitter is not null) throw new InvalidOperationException("SSRC change did not reset jitter.");
            metrics.ReportLoss(64);
            if (metrics.Sample().Loss != 25) throw new InvalidOperationException("RTCP loss fraction scale is incorrect.");
            await using var engine = new VoiceNative.WebRtcCallEngine();
            var idle = engine.GetDiagnostics();
            if (idle.InCall || idle.Peers.Length != 0 || idle.Microphone is not null) throw new InvalidOperationException("Idle diagnostics are incorrect.");
            var json = JsonSerializer.Serialize(idle);
            if (!JsonDocument.Parse(json).RootElement.TryGetProperty("Timestamp", out _)) throw new InvalidOperationException("Snapshot serialization failed.");
            Console.WriteLine("PASS: interval bitrate, missing values, timestamp wrap, jitter, SSRC reset, RTCP loss scale, idle snapshot and JSON export.");
            return true;
        }

        if (args is ["--lan-self-test"])
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var successLeft = false;
            var successRight = false;
            await Task.WhenAll(
                VoiceNative.LanUdpProbe.RunAsync("127.0.0.2", message => { if (message.Contains("왕복 응답 성공")) successLeft = true; }, timeout.Token, System.Net.IPAddress.Parse("127.0.0.1")),
                VoiceNative.LanUdpProbe.RunAsync("127.0.0.1", message => { if (message.Contains("왕복 응답 성공")) successRight = true; }, timeout.Token, System.Net.IPAddress.Parse("127.0.0.2")));
            if (!successLeft || !successRight) throw new InvalidOperationException("LAN probe failed to verify both directions.");
            Console.WriteLine("PASS: LAN UDP diagnostic verified both directions and timed out cleanly.");
            return true;
        }

        return false;
    }
}
