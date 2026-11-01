using SIPSorcery.Net;

namespace VoiceNative;

internal sealed record IncidentReview(bool HasSend, bool HasReceive, bool HasPlayout, bool HasPrehistory,
    bool HasAftermath, int PeerConfirmations, int PeerCount, long Skipped, long Overwritten, string Limit);
internal sealed record IncidentArchiveEntry(IncidentSnapshot Capture, IncidentPeerStatus[] Peers, string OriginalState);
internal sealed record IncidentDetails(IncidentArchiveEntry[] History, long HistoryEvicted,
    IncidentPeerStatus[] Peers, IncidentReview Review);

public sealed partial class WebRtcCallEngine
{
    private readonly object incidentArchiveSync = new();
    private readonly Queue<IncidentArchiveEntry> incidentArchive = new(2);
    private readonly List<IncidentPeerStatus> retiredIncidentPeers = new(6);
    private long incidentArchiveEvicted;
    internal bool IncidentRecordingActive => pipeline.Incidents.Active;
    internal string? IncidentEventId => pipeline.Incidents.Header().Id;
    internal void DiagnosticTriggerIncident()
    {
        if (DiagnosticControlReceived is null) throw new InvalidOperationException("Explicit diagnostic engine required.");
        pipeline.Incidents.Write(new(ProbeClock.Now, "diagnostic-event"), "diagnostic-test");
    }
    internal async Task RearmIncidentRecordingAsync()
    {
        await gate.WaitAsync();
        try
        {
        if (session is null) throw new InvalidOperationException("통화 중에 기록을 다시 준비하세요.");
        if (!AutomaticIncidentRecording) throw new InvalidOperationException("이 시험에서는 자동 기록이 꺼져 있습니다.");
        lock (incidentArchiveSync)
        {
            var previous = pipeline.Incidents.Snapshot(ProbeClock.Now);
            if (previous.Records.Length > 0)
            {
                if (incidentArchive.Count == 2) { incidentArchive.Dequeue(); incidentArchiveEvicted++; }
                incidentArchive.Enqueue(new(previous with { State = "Archived",
                    EndReason = previous.State is "Rolling" or "AfterTrigger" ? "manual-rearm" : previous.EndReason },
                    SnapshotIncidentPeers(), previous.State));
            }
            // A live call already has authenticated media; retain the two-second
            // settling period instead of treating a button press as a fault.
            pipeline.Incidents.Reset(ProbeClock.Now, armTriggers: false);
            foreach (var peer in Volatile.Read(ref sendPeers)) peer.IncidentLink.ResetRound();
        }
        }
        finally { gate.Release(); }
    }
    private static bool SendIncident(Peer peer, IncidentSignal signal)
    {
        var channel = peer.DiagnosticsChannel;
        if (channel?.readyState != RTCDataChannelState.open || channel.bufferedAmount >= 4096) return false;
        try { channel.send(IncidentProtocol.Encode(signal)); return true; }
        catch (Exception) { return false; } // Diagnostic coordination cannot fail media.
    }
    private void PumpIncidentLinks(IEnumerable<Peer> current)
    {
        if (!AutomaticIncidentRecording) return;
        var header = pipeline.Incidents.Header(); var now = ProbeClock.Now;
        foreach (var peer in current)
        {
            if (peer.DiagnosticsChannel?.readyState != RTCDataChannelState.open) continue;
            if (peer.IncidentLink.HelloDue(now)) _ = SendIncident(peer, new("hello"));
            if (header.Id is not null && header.Cause != "remote-request" && peer.IncidentLink.NoticeDue(header.Id, now))
                _ = SendIncident(peer, new("notice", header.Id));
        }
    }
    private void HandleIncidentMessage(Peer peer, byte[] bytes)
    {
        if (!AutomaticIncidentRecording) return;
        var message = IncidentProtocol.Decode(bytes);
        if (message is null) return;
        var now = ProbeClock.Now;
        if (message.Type == "hello")
        {
            peer.IncidentLink.SetSupported();
            if (peer.IncidentLink.HelloResponseDue(now)) _ = SendIncident(peer, new("hello-ack"));
        }
        else if (message.Type == "hello-ack") peer.IncidentLink.SetSupported();
        else if (message.Type == "ack") peer.IncidentLink.Acknowledge(message.Id!, message.Status!);
        else if (message.Type == "notice" && peer.IncidentLink.ReceiveDue(message.Id!, now))
        {
            string result;
            lock (incidentArchiveSync)
            {
                var archived = incidentArchive.Any(entry => entry.Capture.EventId == message.Id
                    || entry.Capture.LinkedEventIds?.Contains(message.Id!) == true);
                result = archived ? "busy" : pipeline.Incidents.AcceptRemote(message.Id!, now);
            }
            peer.IncidentLink.ReceivedResult(result);
            _ = SendIncident(peer, new("ack", message.Id, result));
        }
    }
    private static bool Confirmed(IncidentPeerStatus peer, HashSet<string> ids) =>
        peer.SentId is not null && ids.Contains(peer.SentId) && peer.Response is "accepted" or "linked"
        || peer.ReceivedId is not null && ids.Contains(peer.ReceivedId) && peer.ReceivedResult is "accepted" or "linked";
    private IncidentPeerStatus[] SnapshotIncidentPeers()
    {
        var active = Volatile.Read(ref sendPeers).Where(p => p.Connection.connectionState == RTCPeerConnectionState.connected)
            .Select(p => p.IncidentLink.Snapshot(p.Id)).ToArray();
        lock (incidentArchiveSync) return active.Concat(retiredIncidentPeers.Where(p => active.All(a => a.Peer != p.Peer))).ToArray();
    }
    private void RetireIncidentPeer(Peer peer)
    {
        var status = peer.IncidentLink.Snapshot(peer.Id) with { Connected = false };
        lock (incidentArchiveSync)
        {
            retiredIncidentPeers.RemoveAll(p => p.Peer == peer.Id);
            if (retiredIncidentPeers.Count == 6) retiredIncidentPeers.RemoveAt(0);
            retiredIncidentPeers.Add(status);
        }
    }
    internal IncidentDetails SnapshotIncidentDetails(IncidentSnapshot current)
    {
        var peers = SnapshotIncidentPeers();
        var ids = new HashSet<string>(current.LinkedEventIds ?? [], StringComparer.Ordinal);
        if (current.EventId is not null) ids.Add(current.EventId);
        var expected = peers.Where(p => p.Connected).ToArray();
        if (expected.Length == 0) expected = peers.Where(p => p.SentId is not null && ids.Contains(p.SentId)
            || p.ReceivedId is not null && ids.Contains(p.ReceivedId)).ToArray();
        var records = current.Records;
        var review = new IncidentReview(records.Any(r => r.Stage == "send-api-enter"),
            records.Any(r => r.Stage == "authenticated-rtp-callback"), records.Any(r => r.Stage == "playout-read"),
            current.TriggerAt is { } at && records.Any(r => r.At < at),
            current.TriggerAt is { } after && records.Any(r => r.At > after),
            expected.Count(p => Confirmed(p, ids)), expected.Length, current.SkippedContention, current.Overwritten,
            "상대 확인은 사건 ID의 기록 수락 응답이며 파일 수집/완전성 확인이 아닙니다. 양쪽 진단 파일을 별도로 저장하세요. 공통 ID도 물리적 같은 원인이나 시계 동기화를 보장하지 않습니다.");
        lock (incidentArchiveSync) return new(incidentArchive.ToArray(), incidentArchiveEvicted, peers, review);
    }
    private string IncidentOverview()
    {
        var header = pipeline.Incidents.Header();
        var peers = Volatile.Read(ref sendPeers).Where(p => p.Connection.connectionState == RTCPeerConnectionState.connected)
            .Select(p => p.IncidentLink.Snapshot(p.Id)).ToArray();
        var confirmed = peers.Count(p => header.Id is not null && (p.SentId == header.Id && p.Response is "accepted" or "linked"
            || p.ReceivedId == header.Id && p.ReceivedResult is "accepted" or "linked"));
        var busy = peers.Count(p => p.Response == "busy");
        var supported = peers.Count(p => p.Supported);
        return FormattableString.Invariant($"{pipeline.Incidents.Status} · {header.Count}개 · 충돌 누락 {header.Skipped} · 상대 지원 {supported}/{peers.Length} · 확인 {confirmed}/{peers.Length} · 이전 사건 보존 중 {busy}명 (파일 별도 저장)");
    }
}
