using System.Diagnostics;
using SIPSorcery.Net;
using System.Buffers.Binary;

namespace VoiceNative;

public sealed record NetworkPrecision(double? RtcpRttMs, double? RttAgeSeconds,
    double? ReceiveLossPercent, int? ReceivePacketsLost, double? ReceiveReportAgeSeconds,
    double? RemoteJitterMs, double? LastPacketAgeSeconds, long ReorderedPackets, long DuplicatePackets,
    double? SendLossPercent = null, double? SendReportAgeSeconds = null,
    double? ArrivalGapP95Ms = null, double? ArrivalGapMaxMs = null);

internal sealed class RtcpMetrics
{
    private readonly object sync = new();
    private readonly Dictionary<uint, (uint Ssrc, double Sent)> sent = new();
    private double? rtt, rttAt, receiveLoss, receiveAt, remoteJitter, lastPacket, sendLoss, sendReportAt;
    private int? receiveLost;
    private uint? incomingSsrc, outgoingSsrc;
    private long highest, reordered, duplicates;
    private bool sequenceStarted;
    private readonly HashSet<long> seen = new();
    private ProcessingWindow gaps = new();
    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Packet(uint ssrc, ushort sequence, double? at = null)
    {
        lock (sync)
        {
            var arrival = at ?? Now;
            if (ssrc == incomingSsrc && lastPacket is { } previous && arrival >= previous) gaps.Add((arrival - previous) * 1000);
            lastPacket = arrival;
            if (incomingSsrc != ssrc)
            {
                incomingSsrc = ssrc; sequenceStarted = false; seen.Clear();
                receiveLoss = receiveAt = null; receiveLost = null;
                gaps = new();
            }
            var extended = sequenceStarted ? highest + unchecked((short)(sequence - (ushort)highest)) : sequence;
            if (seen.Contains(extended)) duplicates++;
            else
            {
                if (sequenceStarted && extended < highest) reordered++;
                seen.Add(extended); highest = sequenceStarted ? Math.Max(highest, extended) : extended;
                if (seen.Count > 4096) seen.RemoveWhere(value => value < highest - 2048);
            }
            sequenceStarted = true;
        }
    }
    public void Sender(uint ssrc, uint compactNtp, double? at = null)
    {
        lock (sync)
        {
            if (outgoingSsrc != ssrc) { outgoingSsrc = ssrc; sent.Clear(); rtt = rttAt = remoteJitter = sendLoss = sendReportAt = null; }
            sent[compactNtp] = (ssrc, at ?? Now);
            if (sent.Count > 64) sent.Remove(sent.MinBy(entry => entry.Value.Sent).Key);
        }
    }
    public void Outgoing(RTCPCompoundPacket report)
    {
        if (report.SenderReport is { } sender)
        {
            var bytes = sender.GetBytes();
            if (bytes.Length >= 16)
                Sender(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)),
                    (uint)(BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(8)) >> 16));
        }
        foreach (var sample in Reports(report)) Local(sample);
    }
    public void Incoming(RTCPCompoundPacket report) { foreach (var sample in Reports(report)) Remote(sample); }
    private static IEnumerable<ReceptionReportSample> Reports(RTCPCompoundPacket report) =>
        (report.SenderReport?.ReceptionReports ?? []).Concat(report.ReceiverReport?.ReceptionReports ?? []);
    public void Local(ReceptionReportSample report, double? at = null)
    {
        lock (sync)
        {
            if (report.SSRC != incomingSsrc) return;
            receiveLoss = report.FractionLost * 100.0 / 256; receiveLost = report.PacketsLost; receiveAt = at ?? Now;
        }
    }
    public void Remote(ReceptionReportSample report, double? at = null)
    {
        lock (sync)
        {
            if (report.SSRC != outgoingSsrc) return;
            remoteJitter = report.Jitter / 48.0;
            var now = at ?? Now;
            sendLoss = report.FractionLost * 100.0 / 256; sendReportAt = now;
            if (report.LastSenderReportTimestamp == 0 || !sent.TryGetValue(report.LastSenderReportTimestamp, out var matching) || matching.Ssrc != report.SSRC) return;
            var elapsed = now - matching.Sent;
            var seconds = elapsed - report.DelaySinceLastSenderReport / 65536.0;
            if (elapsed is < 0 or > 120 || seconds is < 0 or > 30) return;
            rtt = seconds * 1000; rttAt = now;
        }
    }
    public NetworkPrecision Snapshot(double? at = null)
    {
        lock (sync)
        {
            var now = at ?? Now;
            double? Age(double? value) => value is { } instant ? Math.Max(0, now - instant) : null;
            var gap = gaps.Snapshot();
            return new(rtt, Age(rttAt), receiveLoss, receiveLost, Age(receiveAt), remoteJitter, Age(lastPacket), reordered, duplicates, sendLoss, Age(sendReportAt), gap.P95, gap.Max);
        }
    }
}

internal sealed class ProcessingWindow
{
    private readonly Queue<double> values = new();
    public void Add(double ms) { values.Enqueue(ms); if (values.Count > 256) values.Dequeue(); }
    public (double? P95, double? Max) Snapshot()
    {
        if (values.Count == 0) return (null, null);
        var sorted = values.Order().ToArray();
        return (sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], sorted[^1]);
    }
}
