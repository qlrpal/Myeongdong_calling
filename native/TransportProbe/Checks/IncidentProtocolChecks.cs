using System.Text;
using VoiceNative;

internal static class IncidentProtocolChecks
{
    internal static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var id = Guid.NewGuid().ToString("N"); var other = Guid.NewGuid().ToString("N");
        foreach (var signal in new[] { new IncidentSignal("hello"), new("hello-ack"), new("notice", id), new("ack", id, "accepted") })
            Require(IncidentProtocol.Decode(Encoding.UTF8.GetBytes(IncidentProtocol.Encode(signal))) == signal, "Incident wire round trip");
        Require(IncidentProtocol.Decode("voice-incident-v1:{broken"u8) is null, "Invalid JSON accepted");
        Require(IncidentProtocol.Decode(Encoding.UTF8.GetBytes("voice-incident-v1:" + new string('x', 400))) is null, "Oversized notice accepted");
        Require(IncidentProtocol.Decode(Encoding.UTF8.GetBytes(IncidentProtocol.Encode(new("notice", "invalid")))) is null,
            "Invalid event identity accepted");
        Require(IncidentProtocol.Decode(Encoding.UTF8.GetBytes(IncidentProtocol.Encode(new("ack", id, "overwrite")))) is null,
            "Unknown ack accepted");
        var link = new IncidentPeerLink();
        Require(link.HelloDue(0) && !link.HelloDue(.5) && link.HelloDue(1) && link.HelloDue(2) && !link.HelloDue(3), "Hello retry budget");
        Require(!link.NoticeDue(id, 0), "Unadvertised peer received notices");
        link.SetSupported(); Require(link.NoticeDue(id, 0) && !link.NoticeDue(id, .5), "Notice rate limit");
        link.Acknowledge(other, "accepted"); Require(link.Snapshot("peer").Response is null, "Stale ack accepted");
        link.Acknowledge(id, "not-ready"); Require(link.NoticeDue(id, 1), "Not-ready notice not retried");
        link.Acknowledge(id, "accepted"); Require(!link.NoticeDue(id, 2), "Accepted notice repeated");
        link.ResetRound(); link.Acknowledge(id, "accepted");
        Require(link.Snapshot("peer").Response is null && link.NoticeDue(other, 3), "Rearm accepted old ack or lost support");
        Require(link.ReceiveDue(id, 0) && !link.ReceiveDue(other, .5) && link.ReceiveDue(other, 1), "Incoming notice flood unbounded");
        var recorder = new IncidentRecorder(32); recorder.Reset(0, armTriggers: false);
        Require(recorder.AcceptRemote(id, 10) == "not-ready" && recorder.Header().Id is null, "Remote request armed pre-media recording");
        recorder.ArmTriggers(10); Require(recorder.AcceptRemote(id, 11) == "not-ready", "Remote bypassed warmup");
        recorder.Write(new(11.9, "before"));
        Require(recorder.AcceptRemote(id, 12) == "accepted", "Ready peer did not capture notice");
        recorder.Write(new(13, "after"));
        var snapshot = recorder.Snapshot(15);
        Require(snapshot.EventId == id && snapshot.Trigger == "remote-request" && snapshot.Records.Any(r => r.Stage == "before")
            && snapshot.Records.Any(r => r.Stage == "after"), "Remote episode lost identity/pre/post");
        Require(recorder.AcceptRemote(other, 20) == "busy" && recorder.Snapshot(20).EventId == id, "Late remote notice overwrote frozen episode");
        Require(recorder.AcceptRemote(id, 20) == "accepted", "Idempotent notice failed");
        recorder.Reset(0); recorder.Write(new(3, "local"), "capture-gap");
        var localId = recorder.Header().Id;
        Require(recorder.AcceptRemote(other, 3.1) == "linked" && recorder.Snapshot(3.1).EventId == localId
            && recorder.Snapshot(3.1).LinkedEventIds!.Contains(other), "Simultaneous event identities not linked");
        Console.WriteLine("PASS: bounded incident protocol, capability/retry/rate limits, stale ack, remote warmup, paired IDs, simultaneous linkage and frozen evidence protection.");
    }
}
