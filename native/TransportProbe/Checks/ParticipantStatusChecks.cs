using System.Text;
using System.Text.Json;

namespace VoiceNative;

internal static class ParticipantStatusChecks
{
    internal static void Run()
    {
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var state = new TelemetryState();
        var report = new LatencyTelemetry(1, null, null, null, null, null, null, null, MicrophoneMuted: true);
        foreach (var muted in new[] { true, false })
        {
            Require(state.Receive(JsonSerializer.SerializeToUtf8Bytes(report with { MicrophoneMuted = muted })), "Mute telemetry rejected");
            var remote = state.Snapshot();
            var view = new LatencyView(null, null, remote.Age, remote.Remote, "");
            Require(ParticipantAudioStatus.RemoteMuted("connected", view) == muted, "Fresh remote mute not reflected");
            Require(ParticipantAudioStatus.RemoteMuted("disconnected", view) is null, "Disconnected mute retained");
            Require(ParticipantAudioStatus.RemoteMuted("connected", view with { RemoteAgeSeconds = 3.01 }) is null, "Stale mute retained");
            foreach (var age in new double?[] { null, -1, double.NaN, double.PositiveInfinity })
                Require(ParticipantAudioStatus.RemoteMuted("connected", view with { RemoteAgeSeconds = age }) is null, "Invalid freshness accepted");
        }
        Require(!state.Receive(Encoding.UTF8.GetBytes("{\"Version\":1,\"FrameMs\":10,\"MicrophoneMuted\":\"false\"}")), "Wrong mute type accepted");
        Require(state.Snapshot().Remote?.MicrophoneMuted == false, "Invalid message changed state");
        // Old clients omit the optional member: silence/absence must never imply unmuted.
        Require(state.Receive(Encoding.UTF8.GetBytes("{\"Version\":1,\"FrameMs\":10}")), "Legacy telemetry rejected");
        var legacy = state.Snapshot();
        Require(ParticipantAudioStatus.RemoteMuted("connected", new(null, null, legacy.Age, legacy.Remote, "")) is null, "Old client falsely shown as microphone on");
        Require(ParticipantAudioStatus.Describe(true, false) == "상대 마이크 꺼짐", "Remote mute label");
        Require(ParticipantAudioStatus.Describe(false, true) == "상대 마이크 켜짐 · 내 쪽 음소거", "Local mute confused with remote mute");
        Require(ParticipantAudioStatus.Describe(true, true) == "상대 마이크 꺼짐 · 내 쪽 음소거", "Combined mute label");
        Console.WriteLine("PASS: remote microphone declaration, legacy/invalid/stale/disconnected handling and independent local mute labels.");
    }
}
