namespace VoiceNative;

// Remote mute is an optional, authenticated declaration, never inferred from silence.
// A stale or disconnected peer must not retain an apparently current microphone state.
internal static class ParticipantAudioStatus
{
    internal static bool? RemoteMuted(string connection, LatencyView? telemetry)
        => connection == "connected" && telemetry?.RemoteAgeSeconds is >= 0 and <= 3
            ? telemetry.Remote?.MicrophoneMuted : null;

    internal static string Describe(bool? remoteMuted, bool locallyMuted)
    {
        var remote = remoteMuted switch
        {
            true => "상대 마이크 꺼짐",
            false => "상대 마이크 켜짐",
            null => "상대 마이크 상태 미확인"
        };
        return locallyMuted ? remote + " · 내 쪽 음소거" : remote;
    }
}
