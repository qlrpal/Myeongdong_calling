namespace VoiceNative;

public readonly record struct CaptureContinuityEvent(double At, int Flags, int PacketSamples,
    int AppDiscardedSamples, long DevicePosition, long? ExpectedDevicePosition,
    long? RawPositionDeltaSamples, bool PositionComparable, double? RawQpcStepMs, double? ExpectedQpcStepMs);
public sealed record CaptureContinuityDiagnostics(long ObservedPackets, long AppDiscardedSamples,
    double AppDiscardedMs, long ComparablePositionBreaks, long OverwrittenEvents, CaptureContinuityEvent[] RecentEvents)
{
    public string Limits { get; } = "AppDiscardedSamples는 입력 불연속 표시로 앱이 초기화한 부분 PCM의 정확한 샘플 수입니다. 장치/드라이버가 잃은 음성량이나 네트워크 손실이 아닙니다. DataDiscontinuity/입력 시각 오류/새 기준점에서는 이전 장치 위치와 비교할 수 없습니다. RawPositionDeltaSamples/RawQpcStepMs는 원시 관측이며 실제 누락량으로 합산하지 않습니다. At은 이 프로세스 단조 시각이고 실제 수음 시각이 아닙니다. 최근 사건16개만 유지합니다.";
}

// Protected by WindowsAudioDevice.audioLock. No packet allocations or extra timer.
internal sealed class CaptureContinuityTracker
{
    private readonly CaptureContinuityEvent[] events = new CaptureContinuityEvent[16];
    private int next, count;
    private long packets, discarded, breaks, overwritten;
    private long? expectedPosition, previousQpc;
    private int previousSamples;
    public long ObservedPackets => packets;

    public void Observe(double at, int frames, int flags, long position, long qpc, int discardedPartialSamples)
    {
        if (frames == 0 && flags == 0 && discardedPartialSamples == 0) return;
        packets++;
        var discontinuity = (flags & 1) != 0;
        var invalidTime = (flags & 4) != 0 || position < 0 || qpc <= 0 || frames <= 0;
        var comparable = expectedPosition.HasValue && !discontinuity && !invalidTime;
        var delta = expectedPosition is { } expected && position >= 0 ? position - expected : (long?)null;
        var qpcStep = previousQpc is { } previous && qpc > 0 ? (qpc - previous) / 10000.0 : (double?)null;
        if (comparable && delta != 0) breaks++;
        discarded += discardedPartialSamples;
        if (discontinuity || invalidTime || comparable && delta != 0)
        {
            events[next] = new(at, flags, frames, discardedPartialSamples, position, expectedPosition,
                delta, comparable, qpcStep, previousQpc.HasValue ? previousSamples / 48.0 : null);
            next = (next + 1) % events.Length;
            if (count == events.Length) overwritten++; else count++;
        }
        // A flagged position may establish a new epoch, but is never compared
        // across the discontinuity. TimestampError invalidates the anchor.
        expectedPosition = invalidTime || position > long.MaxValue - frames ? null : position + frames;
        previousQpc = invalidTime ? null : qpc;
        previousSamples = frames;
    }

    public CaptureContinuityDiagnostics Snapshot()
    {
        var recent = new CaptureContinuityEvent[count];
        var first = (next - count + events.Length) % events.Length;
        for (var i = 0; i < count; i++) recent[i] = events[(first + i) % events.Length];
        return new(packets, discarded, discarded / 48.0, breaks, overwritten, recent);
    }
}
