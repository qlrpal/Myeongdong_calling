using Concentus;
using Concentus.Structs;
using NAudio.Wave;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Buffers;

namespace VoiceNative;

public sealed record RtpPlayoutDiagnostics(double TargetReserveMs, int BufferedPackets, long DecodedPackets,
    long PlcFrames, double ConcealedMs, long LatePackets, long DuplicatePackets, long InvalidPackets,
    long Resyncs, long Rebuffers, long SourceChanges, double? DecodeMeanMs, double? DecodeP95Ms,
    double? DecodeMaxMs, double? LastPacketAgeMs, double? LevelDbFs,
    double RateCorrectionPpm = 0, double? ReadRequestP95Ms = null, double? ReadRequestMaxMs = null,
    double? QueueAfterReadMs = null, long RateAdjustedSamples = 0,
    double? ArrivalJitterP95Ms = null, double HeldMs = 0, double MaxConsecutivePlcMs = 0,
    long ReserveIncreases = 0, long ReserveDecreases = 0, bool AdaptiveReserve = false,
    double? ClockTargetMs = null, double? ClockFilteredQueueMs = null,
    long RecoveryDiscardedPackets = 0, double RecoveryDiscardedMs = 0,
    long ShortReserveRestarts = 0, int? RecoveryReserveMs = null, string Encoding = "Opus", double PacketQuantumMs = 10);

// Render-driven: the compressed packet reserve replaces the old PCM startup reserve.
// No timer or extra PCM queue is placed in front of this provider.
internal sealed class RtpOpusPlayoutBuffer : IPlayoutBuffer, IDisposable
{
    private const int SamplesPerMs = 48;
    private const int MaximumSamples = 5760; // 120ms Opus packet/backlog bound.
    private const int MaximumPlcSamples = 2880; // Rebuffer after 60ms of continuous loss.
    private const int RecoveryPressureEpisodes = 3;
    private const int RecoveryPressureWindowSamples = 3 * 48000;
    private readonly object sync = new();
    private readonly IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(48000, 1);
    // At most 120 one-ms packets plus the candidate before backlog trimming.
    // A small ordered array avoids a tree-node allocation for every packet.
    private readonly SortedList<long, Packet> packets = new(MaximumSamples / 48 + 1);
    private readonly ArrayPool<byte> packetPool;
    private readonly HashSet<uint> retiredSources = new();
    private readonly short[] pcm = new short[MaximumSamples];
    private readonly ProcessingWindow processing = new();
    private readonly ProcessingWindow readRequests = new();
    private readonly AudioFrameTimeline timeline;
    private readonly int reserveSamples;
    private readonly int configuredQuantumSamples, maximumConcealSamples, configuredBacklogSamples;
    private int quantumSamples;
    private bool hasRemoteQuantum;
    private int maximumQueuedSamples;
    private int lastReadSamples;
    private readonly bool experimental;
    private readonly int emptyHoldSamples;
    private readonly int coarsePeerFloorSamples;
    private int PeerReserveFloorSamples => quantumSamples >= 480 ? coarsePeerFloorSamples : 0;
    private int CurrentReserveSamples => Math.Max(adaptive.TargetSamples, PeerReserveFloorSamples);
    private readonly bool pcmTransport;
    private uint? source;
    private long highestTimestamp, expectedTimestamp;
    private bool hasTimestamp, hasPosition, started, everStarted, suspended, disposed, underflowActive;
    private int queuedSamples, pcmOffset, pcmCount, missingSamples, fadeRemaining;
    private string? pcmHash;
    private long decodedPackets, plcFrames, concealedSamples, late, duplicates, invalid, resyncs, rebuffers, sourceChanges;
    private long decodedSamples, consumedSamples, requestedSamples, underfillSamples, episodeSamples, maxEpisodeSamples, trims;
    private double totalDecodeMs;
    private double? level;
    private long? lastPacketAt;
    private double? queueAfterRead;
    private readonly PlayoutClockController clock;
    private readonly bool transientEmptyQueueHold;
    private readonly bool freshRestart;
    private readonly bool freshInitialStart;
    private readonly bool contiguousRestart;
    private readonly bool boundedRestartWait;
    private bool restartWaited;
    private readonly int recoveryReserveSamples;
    private bool gapRestart;
    private long shortReserveRestarts;
    private readonly bool repeatedGapRecovery;
    private readonly Queue<long> recentRebuffers = new();
    private long recoveryDiscardedPackets, recoveryDiscardedSamples;
    private readonly AdaptivePlayoutReserve adaptive;
    private readonly PipelineTrace? pipeline;
    private readonly string tracePeer;
    private AutoResetEvent? primingSignal;
    internal WaitHandle PrimingSignal
    {
        get { lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return primingSignal ??= new(false); } }
    }
    internal bool CanPrimeOutput(int requiredSamples)
    {
        if (requiredSamples is < 1 or > MaximumSamples) throw new ArgumentOutOfRangeException(nameof(requiredSamples));
        lock (sync) return !disposed && !suspended && (started || queuedSamples >= Math.Min(MaximumSamples, CurrentReserveSamples + requiredSamples));
    }
    private PipelinePlayout? pcmTrace;
    private long heldSamples;
    private int maxMissingSamples;
    private double phase = 1;
    private short previousSample, currentSample;
    private long rateAdjustedSamples;
    private readonly record struct Packet(byte[] Payload, int Length, int Samples, string? Hash);
    private KeyValuePair<long, Packet> FirstPacket => new(packets.Keys[0], packets.Values[0]);
    private bool Overlaps(long timestamp, int samples)
    {
        for (var i = 0; i < packets.Count; i++)
        {
            var start = packets.Keys[i];
            if (start >= timestamp + samples) break;
            if (timestamp < start + packets.Values[i].Samples) return true;
        }
        return false;
    }
    public WaveFormat WaveFormat { get; } = new(48000, 16, 1);
    public Action<string, double, bool>? OnFrameSubmitted { get; set; }

    public RtpOpusPlayoutBuffer(AudioFrameTimeline timeline, int targetReserveMs = 20, bool correctClock = true, bool adaptiveReserve = true, PipelineTrace? pipeline = null, string tracePeer = "", bool fixedReserveClockTarget = false, bool transientEmptyQueueHold = false, bool freshRestart = false, bool freshInitialStart = false, bool contiguousRestart = false, bool boundedRestartWait = false, bool responsiveReserve = false, int recoveryReserveMs = 0, bool repeatedGapRecovery = false, LatencyExperiment? experiment = null, ArrayPool<byte>? packetPool = null)
    {
        this.packetPool = packetPool ?? ArrayPool<byte>.Shared;
        if (targetReserveMs is < 10 or > 40) { decoder.Dispose(); throw new ArgumentOutOfRangeException(nameof(targetReserveMs)); }
        if (recoveryReserveMs is not (0 or 10 or 15)) { decoder.Dispose(); throw new ArgumentOutOfRangeException(nameof(recoveryReserveMs)); }
        recoveryReserveSamples = recoveryReserveMs * SamplesPerMs;
        this.repeatedGapRecovery = repeatedGapRecovery;
        this.timeline = timeline;
        experiment?.Validate();
        experimental = experiment is not null;
        emptyHoldSamples = (int)((experiment?.EmptyHoldMs ?? 0) * SamplesPerMs);
        coarsePeerFloorSamples = experiment?.AdaptiveShortReserve == true ? experiment.MaxAdaptiveReserveMs * SamplesPerMs : 0;
        configuredQuantumSamples = quantumSamples = experiment?.FrameSamples ?? 480;
        pcmTransport = experiment?.Pcm == true;
        configuredBacklogSamples = maximumQueuedSamples = experiment?.BacklogMs * 48 ?? MaximumSamples;
        maximumConcealSamples = experiment?.PlcLimitMs * 48 ?? MaximumPlcSamples;
        reserveSamples = experiment?.ReserveSamples ?? targetReserveMs * SamplesPerMs;
        if (experiment is not null) { adaptiveReserve = responsiveReserve = transientEmptyQueueHold = false; fixedReserveClockTarget = freshRestart = freshInitialStart = true; }
        clock = new(reserveSamples, correctClock, fixedReserveClockTarget, responsiveReserve);
        this.transientEmptyQueueHold = transientEmptyQueueHold;
        this.freshRestart = freshRestart;
        this.freshInitialStart = freshInitialStart;
        this.contiguousRestart = contiguousRestart;
        this.boundedRestartWait = boundedRestartWait;
        adaptive = new(reserveSamples, (adaptiveReserve || responsiveReserve || experiment?.AdaptiveShortReserve == true) && correctClock,
            responsiveReserve, experiment?.AdaptiveShortReserve == true, (experiment?.MaxAdaptiveReserveMs ?? 15) * SamplesPerMs,
            experiment?.ShortReserveQuietSeconds ?? 6);
        this.pipeline = pipeline; this.tracePeer = tracePeer;
    }

    public void Add(uint ssrc, uint timestamp, byte[] payload, double? arrivalMs = null)
    {
        var tracing = pipeline?.Enabled == true;
        var calledAt = tracing ? ProbeClock.Now : 0;
        lock (sync)
        {
            var lockAt = tracing ? ProbeClock.Now : 0;
            var outcome = "ignored";
            try
            {
                if (disposed || suspended) return;
                int samples;
                try { samples = pcmTransport ? payload.Length % 2 == 0 ? payload.Length / 2 : -1 : payload.Length is > 0 and <= 1275 ? OpusPacketInfo.GetNumSamples(payload, 48000) : -1; }
                catch (Exception error) when (error is ArgumentException or OpusException) { samples = -1; }
                // Opus packet duration belongs to the remote sender. A local 10ms
                // capture setting must not reject a valid remote 2.5ms stream.
                var wireQuantum = pcmTransport ? quantumSamples : 120;
                if (samples < wireQuantum || samples > MaximumSamples || samples % wireQuantum != 0) { invalid++; outcome = "invalid"; return; }
                if (source != ssrc)
                {
                    if (retiredSources.Contains(ssrc)) { RejectLate(payload); outcome = "retired-source"; return; }
                    if (source is { } previous)
                    {
                        // Bound metadata and reject stale packets from recent old SSRCs.
                        if (retiredSources.Count == 4) retiredSources.Clear();
                        retiredSources.Add(previous); sourceChanges++;
                        ResetTimeline();
                    }
                    source = ssrc;
                    quantumSamples = configuredQuantumSamples;
                }
                if (!pcmTransport)
                {
                    // Incoming packet duration is independent of local capture.
                    var initialQuantum = experimental ? Math.Min(samples, Math.Max(120, maximumConcealSamples)) : configuredQuantumSamples;
                    quantumSamples = hasRemoteQuantum ? Math.Min(quantumSamples, samples) : Math.Min(initialQuantum, samples);
                    hasRemoteQuantum = true;
                }
                var stamp = hasTimestamp ? highestTimestamp + unchecked((int)(timestamp - (uint)highestTimestamp)) : timestamp;
                adaptive.Arrival(stamp, arrivalMs ?? ProbeClock.Now * 1000);
                if (hasPosition && stamp < expectedTimestamp)
                {
                    if (stamp + samples >= expectedTimestamp - MaximumPlcSamples) adaptive.Late();
                    RejectLate(payload); outcome = "late"; return;
                }
                if (packets.ContainsKey(stamp)) { duplicates++; outcome = "duplicate"; return; }
                var reanchored = false;
                if (hasPosition && stamp - expectedTimestamp > 9600)
                {
                    ResetTimeline(); resyncs++;
                    stamp = timestamp; reanchored = true;
                }
                if (hasTimestamp && (stamp - highestTimestamp) % quantumSamples != 0) { invalid++; outcome = "invalid"; return; }
                // Reject overlapping spans before they can alter decoder state.
                if (Overlaps(stamp, samples)) { invalid++; outcome = "overlap"; return; }
                highestTimestamp = hasTimestamp ? Math.Max(highestTimestamp, stamp) : stamp; hasTimestamp = true;
                var hash = timeline.Enabled ? AudioFrameTimeline.Hash(payload) : null;
                var storage = packetPool.Rent(payload.Length);
                try
                {
                    payload.CopyTo(storage, 0);
                    packets.Add(stamp, new(storage, payload.Length, samples, hash));
                }
                catch { packetPool.Return(storage); throw; }
                queuedSamples += samples;
                if (!started) primingSignal?.Set();
                outcome = "queued";
                if (reanchored) { expectedTimestamp = stamp; hasPosition = true; }
                lastPacketAt = Stopwatch.GetTimestamp();
                UpdateQueueBound();
                if (queuedSamples + pcmCount - pcmOffset > maximumQueuedSamples)
                {
                    // Shed old backlog; do not play PLC for deliberately discarded speech.
                    while (packets.Count > 1 && queuedSamples > (maximumQueuedSamples == MaximumSamples ? 2880 : Math.Max(480, maximumQueuedSamples / 2)))
                    {
                        if (experimental) { recoveryDiscardedPackets++; recoveryDiscardedSamples += FirstPacket.Value.Samples; }
                        RemoveFirst(discarded: true);
                    }
                    if (experimental) recoveryDiscardedSamples += pcmCount - pcmOffset;
                    DiscardPcm(); decoder.ResetState(); started = false; restartWaited = false;
                    expectedTimestamp = FirstPacket.Key; hasPosition = true;
                    missingSamples = 0; fadeRemaining = 240; trims++; resyncs++;
                }
            }
            finally
            {
                if (tracing) pipeline!.Admitted(new(tracePeer, ssrc, timestamp, calledAt, lockAt, ProbeClock.Now,
                    outcome, (queuedSamples + pcmCount - pcmOffset) / 48.0));
            }
        }
    }

    public int Read(Span<byte> destination)
    {
        if (destination.Length % 2 != 0) throw new ArgumentException("Complete PCM samples required.");
        var tracing = pipeline?.Enabled == true;
        var calledAt = tracing ? ProbeClock.Now : 0;
        lock (sync)
        {
            var lockAt = tracing ? ProbeClock.Now : 0;
            var queueBefore = (queuedSamples + pcmCount - pcmOffset) / 48.0;
            var plcAtEntry = plcFrames;
            var provided = 0;
            try
            {
                destination.Clear(); requestedSamples += destination.Length / 2;
                lastReadSamples = destination.Length / 2;
                UpdateQueueBound();
                readRequests.Add(destination.Length / 96.0);
                if (disposed || suspended) return destination.Length;
                clock.SetReserve(CurrentReserveSamples);
                if (experimental && started && packets.Count > 0)
                {
                    // Trim at the consumption boundary, where a complete device pull
                    // is known. A capture burst must not reset a nearly full pull.
                    var keep = destination.Length / 2 + CurrentReserveSamples;
                    var discarded = false;
                    while (packets.Count > 1 && queuedSamples - FirstPacket.Value.Samples >= keep)
                    {
                        recoveryDiscardedSamples += FirstPacket.Value.Samples;
                        recoveryDiscardedPackets++; RemoveFirst(discarded: true); discarded = true;
                    }
                    if (discarded)
                    {
                        recoveryDiscardedSamples += pcmCount - pcmOffset;
                        DiscardPcm(); decoder.ResetState(); expectedTimestamp = FirstPacket.Key;
                        missingSamples = 0; fadeRemaining = 48; resyncs++;
                    }
                }
                if (!started)
                {
                    // The window uses unwrapped source RTP time, including replay.
                    // Initial/isolated/reconfigured streams retain their full reserve.
                    var pressure = !repeatedGapRecovery || (recentRebuffers.Count >= RecoveryPressureEpisodes
                        && highestTimestamp - recentRebuffers.Peek() <= RecoveryPressureWindowSamples);
                    var restartReserve = gapRestart && pressure && recoveryReserveSamples > 0
                        ? Math.Max(PeerReserveFloorSamples, Math.Min(CurrentReserveSamples, recoveryReserveSamples)) : CurrentReserveSamples;
                    var startupSamples = Math.Min(MaximumSamples, restartReserve + destination.Length / 2);
                    // Preserve a small reserve AFTER the first render request, accounting for
                    // resampler/device pull size instead of stacking another 40ms PCM queue.
                    if (packets.Count == 0 || queuedSamples < startupSamples)
                    {
                        if (everStarted) RecordUnderfill(destination.Length / 2);
                        return destination.Length;
                    }
                    if ((freshRestart && everStarted) || (freshInitialStart && !everStarted))
                    {
                        var discarded = false;
                        if (contiguousRestart && everStarted)
                        {
                            // Experimental: a total packet count can hide RTP holes. Require
                            // reserve + this pull in the newest contiguous tail before restarting.
                            long? next = null; long tailStart = 0; var tailSamples = 0;
                            for (var index = packets.Count - 1; index >= 0; index--)
                            {
                                var entry = new KeyValuePair<long, Packet>(packets.Keys[index], packets.Values[index]);
                                if (next is { } boundary && entry.Key + entry.Value.Samples != boundary) break;
                                tailStart = entry.Key; tailSamples += entry.Value.Samples; next = entry.Key;
                            }
                            if (tailSamples < startupSamples)
                            {
                                if (!boundedRestartWait || !restartWaited)
                                {
                                    restartWaited = true;
                                    RecordUnderfill(destination.Length / 2);
                                    return destination.Length;
                                }
                                // After one extra pull, preserve baseline progress instead of
                                // waiting indefinitely for a complete tail on a lossy link.
                                tailStart = FirstPacket.Key;
                            }
                            while (FirstPacket.Key < tailStart)
                            {
                                recoveryDiscardedSamples += FirstPacket.Value.Samples;
                                recoveryDiscardedPackets++; discarded = true;
                                RemoveFirst(discarded: true);
                            }
                        }
                        // At the selected start boundary, preserve only the fresh reserve plus this pull.
                        // Deliberately discarded speech is tracked separately from PLC/loss.
                        while (packets.Count > 1 && queuedSamples - FirstPacket.Value.Samples >= startupSamples)
                        {
                            recoveryDiscardedSamples += FirstPacket.Value.Samples;
                            recoveryDiscardedPackets++; discarded = true;
                            RemoveFirst(discarded: true);
                        }
                        if (discarded) { decoder.ResetState(); resyncs++; }
                    }
                    expectedTimestamp = FirstPacket.Key; hasPosition = started = everStarted = true;
                    if (gapRestart && restartReserve < adaptive.TargetSamples) shortReserveRestarts++;
                    gapRestart = false;
                    restartWaited = false;
                    missingSamples = 0; fadeRemaining = 240;
                }
                var filled = 0;
                var consumedBefore = consumedSamples;
                var plcBefore = plcFrames;
                while (filled < destination.Length)
                {
                    if (clock.Enabled)
                    {
                        // Causal linear interpolation: at most one sample of delay,
                        // no frame-sized second PCM queue and no speculative PLC read-ahead.
                        var available = true;
                        while (phase >= 1)
                        {
                            if (pcmOffset == pcmCount && !PreparePcm()) { available = false; break; }
                            if (pcmHash is not null || pcmTrace is not null) SubmitFrame();
                            previousSample = currentSample; currentSample = pcm[pcmOffset++];
                            consumedSamples++; phase -= 1;
                        }
                        if (!available) { phase = 1; previousSample = currentSample = 0; break; }
                        var sample = (short)(previousSample + (currentSample - previousSample) * phase);
                        if (fadeRemaining > 0) { sample = (short)(sample * (240 - fadeRemaining) / 240); fadeRemaining--; }
                        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(filled, 2), sample); filled += 2;
                        phase += clock.Rate;
                        continue;
                    }
                    if (pcmOffset == pcmCount && !PreparePcm()) break;
                    if (pcmHash is not null || pcmTrace is not null) SubmitFrame();
                    var take = Math.Min(pcmCount - pcmOffset, (destination.Length - filled) / 2);
                    for (var i = 0; i < take; i++)
                    {
                        var value = pcm[pcmOffset++];
                        if (fadeRemaining > 0) { value = (short)(value * (240 - fadeRemaining) / 240); fadeRemaining--; }
                        BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(filled, 2), value); filled += 2;
                    }
                    consumedSamples += take;
                }
                if (filled < destination.Length) RecordUnderfill((destination.Length - filled) / 2);
                else { underflowActive = false; episodeSamples = 0; }
                queueAfterRead = (queuedSamples + pcmCount - pcmOffset) / 48.0;
                rateAdjustedSamples += consumedSamples - consumedBefore - filled / 2;
                if (filled < destination.Length) clock.Reset();
                else if (plcFrames == plcBefore) clock.Observe(queuedSamples + pcmCount - pcmOffset, filled / 2);
                adaptive.Advance(destination.Length / 2, plcFrames != plcBefore || filled < destination.Length);
                provided = filled / 2;
                return destination.Length;
            }
            finally
            {
                if (tracing) pipeline!.Read(new(tracePeer, calledAt, lockAt, ProbeClock.Now,
                    destination.Length / 2, provided, plcFrames - plcAtEntry, queueBefore,
                    (queuedSamples + pcmCount - pcmOffset) / 48.0, started));
            }
        }
    }

    private void SubmitFrame()
    {
        var at = ProbeClock.Now;
        if (pcmHash is { } hash) { OnFrameSubmitted?.Invoke(hash, at, false); pcmHash = null; }
        if (pcmTrace is { } frame)
        {
            pipeline!.Played(frame with { At = at, SubmittedAt = at, Outcome = "played", QueueMs = (queuedSamples + pcmCount - pcmOffset) / 48.0 });
            pcmTrace = null;
        }
    }

    private void UpdateQueueBound()
    {
        // Until the first device pull, its demand is unknown. Keep a bounded
        // cold-start window; the first read then trims it to fresh reserve.
        if (experimental) maximumQueuedSamples = lastReadSamples == 0 ? MaximumSamples : Math.Min(MaximumSamples,
            Math.Max(configuredBacklogSamples, lastReadSamples * 2 + CurrentReserveSamples + quantumSamples));
    }

    private bool PreparePcm()
    {
        pcmOffset = pcmCount = 0;
        var packetIndex = packets.IndexOfKey(expectedTimestamp);
        if (packetIndex >= 0)
        {
            var packet = packets.Values[packetIndex];
            packets.RemoveAt(packetIndex);
            var packetTimestamp = unchecked((uint)expectedTimestamp);
            queuedSamples -= packet.Samples;
            var began = ProbeClock.Now; var watch = Stopwatch.GetTimestamp();
            try
            {
                if (pcmTransport)
                {
                    pcmCount = packet.Samples;
                    for (var i = 0; i < pcmCount; i++) pcm[i] = BinaryPrimitives.ReadInt16BigEndian(packet.Payload.AsSpan(i * 2, 2));
                }
                else pcmCount = decoder.Decode(packet.Payload.AsSpan(0, packet.Length), pcm, pcm.Length, false);
                if (pcmCount != packet.Samples) throw new ArgumentException("Unexpected decoded packet duration.");
            }
            catch (Exception error) when (error is OpusException or ArgumentException)
            {
                invalid++;
                if (packet.Hash is { } badHash) OnFrameSubmitted?.Invoke(badHash, ProbeClock.Now, true);
                if (pipeline?.Enabled == true) pipeline.Played(new(tracePeer, source ?? 0, packetTimestamp,
                    ProbeClock.Now, began, ProbeClock.Now, null, "decode-invalid", queuedSamples / 48.0));
                return Conceal();
            }
            finally { packetPool.Return(packet.Payload); }
            var elapsed = Stopwatch.GetElapsedTime(watch).TotalMilliseconds;
            processing.Add(elapsed); totalDecodeMs += elapsed; decodedPackets++; decodedSamples += pcmCount;
            expectedTimestamp += pcmCount; missingSamples = 0;
            pcmHash = packet.Hash;
            if (pipeline?.Enabled == true) pcmTrace = new(tracePeer, source ?? 0, packetTimestamp, ProbeClock.Now,
                began, ProbeClock.Now, null, "pending", (queuedSamples + pcmCount) / 48.0);
            if (pcmHash is { } hash) TraceDecodedFrame(hash, began);
            double energy = 0; for (var i = 0; i < pcmCount; i++) energy += (double)pcm[i] * pcm[i];
            level = Math.Max(-120, 10 * Math.Log10(Math.Max(energy / pcmCount, 0.000001) / (32768.0 * 32768)));
            return true;
        }
        if (missingSamples >= maximumConcealSamples)
        {
            EnterRebuffer();
            return false;
        }
        // No future RTP packet means this may be an arrival-phase miss, not a
        // proven network loss. Retain its timestamp for a bounded short hold.
        // Future queued packets still reveal actual gaps and advance normal PLC.
        if (packets.Count == 0 && missingSamples + quantumSamples <= emptyHoldSamples)
            return Conceal(advanceTimestamp: false);
        // Diagnostic candidate: one 10ms hold at the start of an empty-queue
        // episode, without permanently raising the target. Future packets already
        // queued imply a real RTP gap, so those still advance ordinary PLC.
        // missingSamples includes this hold, preserving the total 60ms bound.
        if (transientEmptyQueueHold && packets.Count == 0 && missingSamples == 0)
            return Conceal(advanceTimestamp: false);
        if (packets.Count == 0 && adaptive.TryHold(quantumSamples))
        {
            clock.SetReserve(CurrentReserveSamples);
            return Conceal(advanceTimestamp: false);
        }
        return Conceal();
    }

    private bool Conceal(bool advanceTimestamp = true)
    {
        // Ordinary loss advances RTP once. A bounded empty-queue hold inserts
        // PLC without declaring that RTP packet consumed; it may still arrive.
        // Both count toward the same 60ms PLC bound. FEC is not requested.
        pcmHash = null;
        if (missingSamples + quantumSamples > maximumConcealSamples) { EnterRebuffer(); return false; }
        if (pcmTransport) { pcmCount = quantumSamples; pcm.AsSpan(0, pcmCount).Clear(); }
        else pcmCount = decoder.Decode(ReadOnlySpan<byte>.Empty, pcm, quantumSamples, false);
        if (pipeline?.Enabled == true) pipeline.Played(new(tracePeer, source ?? 0, unchecked((uint)expectedTimestamp),
            ProbeClock.Now, null, null, null, advanceTimestamp ? "plc-loss" : "plc-hold", queuedSamples / 48.0));
        if (advanceTimestamp) expectedTimestamp += pcmCount;
        else heldSamples += pcmCount;
        missingSamples += pcmCount; maxMissingSamples = Math.Max(maxMissingSamples, missingSamples);
        plcFrames++; concealedSamples += pcmCount;
        return pcmCount > 0;
    }

    private void RecordUnderfill(int samples)
    {
        if (!underflowActive) { underflowActive = true; underfillEpisodes++; }
        underfillSamples += samples; episodeSamples += samples; maxEpisodeSamples = Math.Max(maxEpisodeSamples, episodeSamples);
    }
    private void EnterRebuffer()
    {
        started = false; gapRestart = true; restartWaited = false; rebuffers++;
        recentRebuffers.Enqueue(highestTimestamp);
        while (recentRebuffers.Count > RecoveryPressureEpisodes || highestTimestamp - recentRebuffers.Peek() > RecoveryPressureWindowSamples)
            recentRebuffers.Dequeue();
        decoder.ResetState(); adaptive.Rebuffer();
    }
    private void RejectLate(byte[] payload)
    {
        late++;
        if (!timeline.Enabled) return;
        // A late duplicate must not erase a frame already decoded/submitted.
        // A never-played trace must not masquerade as a verified probe result.
        timeline.Update(AudioFrameTimeline.Hash(payload), old => old.DecodeStart is null && old.OutputSubmittedAt is null
            ? old with { Discarded = true } : old);
    }
    private long underfillEpisodes;
    private void RemoveFirst(bool discarded)
    {
        var first = FirstPacket; packets.RemoveAt(0); queuedSamples -= first.Value.Samples;
        try
        {
            if (discarded && pipeline?.Enabled == true) pipeline.Played(new(tracePeer, source ?? 0, unchecked((uint)first.Key),
                ProbeClock.Now, null, null, null, "discarded", queuedSamples / 48.0));
            if (first.Value.Hash is { } hash) OnFrameSubmitted?.Invoke(hash, ProbeClock.Now, discarded);
        }
        finally { packetPool.Return(first.Value.Payload); }
    }
    private void TraceDecodedFrame(string hash, double began) =>
        timeline.Update(hash, old => old with { DecodeStart = began, DecodeEnd = ProbeClock.Now, QueuedAt = ProbeClock.Now });
    private void DiscardPcm()
    {
        if (pcmHash is { } hash) OnFrameSubmitted?.Invoke(hash, ProbeClock.Now, true);
        if (pcmTrace is { } traced) pipeline!.Played(traced with { At = ProbeClock.Now, Outcome = "discarded" });
        pcmTrace = null;
        pcmHash = null; pcmOffset = pcmCount = 0;
        phase = 1; previousSample = currentSample = 0; clock.Reset();
        queueAfterRead = null;
    }
    private void ResetTimeline()
    {
        while (packets.Count > 0) RemoveFirst(discarded: true);
        DiscardPcm(); decoder.ResetState(); started = hasPosition = hasTimestamp = false;
        restartWaited = false;
        gapRestart = false;
        recentRebuffers.Clear();
        missingSamples = 0; fadeRemaining = 240;
        hasRemoteQuantum = false;
        adaptive.Reset();
        primingSignal?.Reset();
    }
    public void ConfigureForOutput(bool lowLatency, int periodMs) { } // Render request size is handled in Read.
    public void Suspend() { lock (sync) { if (disposed) return; suspended = true; ResetTimeline(); } }
    public void Resume() { lock (sync) { if (disposed) return; ResetTimeline(); suspended = false; } }
    public (double QueueMs, long Trims, long Underruns, double DecodedMs, double ConsumedMs, double RequestedMs, double UnderfillMs, double MaxUnderfillMs) Snapshot()
    {
        lock (sync) return ((queuedSamples + pcmCount - pcmOffset) / 48.0, trims, underfillEpisodes,
            decodedSamples / 48.0, consumedSamples / 48.0, requestedSamples / 48.0, underfillSamples / 48.0, maxEpisodeSamples / 48.0);
    }
    public RtpPlayoutDiagnostics Details()
    {
        lock (sync)
        {
            var timing = processing.Snapshot();
            return new(CurrentReserveSamples / 48.0, packets.Count, decodedPackets, plcFrames, concealedSamples / 48.0,
                late, duplicates, invalid, resyncs, rebuffers, sourceChanges,
                decodedPackets > 0 ? totalDecodeMs / decodedPackets : null, timing.P95, timing.Max,
                lastPacketAt is { } at ? Stopwatch.GetElapsedTime(at).TotalMilliseconds : null, level,
                RateCorrectionPpm: (clock.Rate - 1) * 1000000,
                ReadRequestP95Ms: readRequests.Snapshot().P95, ReadRequestMaxMs: readRequests.Snapshot().Max,
                QueueAfterReadMs: queueAfterRead, RateAdjustedSamples: rateAdjustedSamples,
                ArrivalJitterP95Ms: adaptive.Enabled ? adaptive.JitterP95Ms : null, HeldMs: heldSamples / 48.0,
                MaxConsecutivePlcMs: maxMissingSamples / 48.0, ReserveIncreases: adaptive.Increases,
                ReserveDecreases: adaptive.Decreases, AdaptiveReserve: adaptive.Enabled,
                ClockTargetMs: clock.Enabled ? clock.TargetSamples / 48 : null,
                ClockFilteredQueueMs: clock.FilteredQueueSamples / 48,
                RecoveryDiscardedPackets: recoveryDiscardedPackets, RecoveryDiscardedMs: recoveryDiscardedSamples / 48.0,
                ShortReserveRestarts: shortReserveRestarts, RecoveryReserveMs: recoveryReserveSamples > 0 ? recoveryReserveSamples / 48 : null,
                Encoding: pcmTransport ? "L16 PCM (loss: silence)" : "Opus", PacketQuantumMs: quantumSamples / 48.0);
        }
    }
    public void Dispose()
    {
        lock (sync) { if (disposed) return; ResetTimeline(); disposed = true; primingSignal?.Dispose(); decoder.Dispose(); }
    }
}
