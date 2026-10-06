using System.Text.Json;
using SIPSorcery.Net;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

// Exercise native ICE + DTLS/SRTP + Opus without opening physical audio devices.
using var encoder = new AudioEncoder(includeOpus: true);
using var decoder = new AudioEncoder(includeOpus: true);
var format = encoder.SupportedFormats.Single(f => f.Codec == AudioCodecsEnum.OPUS);
using var left = new RTCPeerConnection(new RTCConfiguration());
using var right = new RTCPeerConnection(new RTCConfiguration());
var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var leftConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rightConnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
left.addTrack(new MediaStreamTrack([format], MediaStreamStatusEnum.SendRecv));
right.addTrack(new MediaStreamTrack([format], MediaStreamStatusEnum.SendRecv));
var pendingLeft = new List<RTCIceCandidateInit>();
var pendingRight = new List<RTCIceCandidateInit>();
var sync = new object();
void Forward(RTCIceCandidate candidate, RTCPeerConnection target, List<RTCIceCandidateInit> pending)
{
    if (candidate is null) return;
    var wire = JsonSerializer.Serialize(new { candidate = candidate.candidate, sdpMid = candidate.sdpMid, sdpMLineIndex = candidate.sdpMLineIndex });
    var copy = JsonSerializer.Deserialize<RTCIceCandidateInit>(wire, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    lock (sync) { if (target.remoteDescription is null) pending.Add(copy); else target.addIceCandidate(copy); }
}
left.onicecandidate += candidate => Forward(candidate, right, pendingRight);
right.onicecandidate += candidate => Forward(candidate, left, pendingLeft);
left.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) leftConnected.TrySetResult(); };
right.onconnectionstatechange += state => { if (state == RTCPeerConnectionState.connected) rightConnected.TrySetResult(); };
right.OnRtpPacketReceived += (_, media, packet) =>
{
    if (media != SDPMediaTypesEnum.audio) return;
    try
    {
        var pcm = decoder.DecodeAudio(packet.Payload, format);
        if (pcm.Length != 960 || pcm.All(x => x == 0)) throw new Exception("Decoded Opus frame is invalid.");
        received.TrySetResult();
    }
    catch (Exception ex) { received.TrySetException(ex); }
};
try
{
    var offer = left.createOffer(null);
    await left.setLocalDescription(offer);
    lock (sync)
    {
        if (right.setRemoteDescription(offer) != SetDescriptionResultEnum.OK) throw new Exception("Offer rejected.");
        foreach (var candidate in pendingRight) right.addIceCandidate(candidate);
    }
    var answer = right.createAnswer(null);
    await right.setLocalDescription(answer);
    lock (sync)
    {
        if (left.setRemoteDescription(answer) != SetDescriptionResultEnum.OK) throw new Exception("Answer rejected.");
        foreach (var candidate in pendingLeft) left.addIceCandidate(candidate);
    }
    await Task.WhenAll(leftConnected.Task, rightConnected.Task).WaitAsync(TimeSpan.FromSeconds(20));
    var samples = Enumerable.Range(0, 960).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 8000)).ToArray();
    for (var i = 0; i < 10 && !received.Task.IsCompleted; i++)
    {
        left.SendAudio(960, encoder.EncodeAudio(samples, format));
        await Task.Delay(20);
    }
    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine("PASS: two native peers negotiated Opus, connected via ICE/DTLS, and delivered a decodable 20ms SRTP audio frame.");
}
finally { left.Close("probe complete"); right.Close("probe complete"); }
