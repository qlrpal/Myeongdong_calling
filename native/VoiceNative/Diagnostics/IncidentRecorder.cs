namespace VoiceNative;

// Value metadata only. Sequence is an app frame ID for send/drop and RTP sequence
// for receive. SSRC+RtpTimestamp links the two; no payload or endpoint is retained.
internal readonly record struct IncidentRecord(double At, string Stage, string Peer = "", uint Ssrc = 0,
    uint RtpTimestamp = 0, long Sequence = 0, long SampleOffset = 0, int Samples = 0,
    double DurationMs = 0, double QueueMs = 0, string? Outcome = null, double? SourceAt = null,
    double WorkMs = 0, double LockWaitMs = 0, int Flags = 0, int SampleRate = 48000);
internal sealed record IncidentSnapshot(string State, string? Trigger, double? TriggerAt, double? CaptureUntil,
    int Capacity, long Overwritten, long SkippedContention, IncidentRecord[] Records,
    int? TriggerGen0, int? TriggerGen1, int? TriggerGen2, string? EndReason, double? TriggerArmedAt = null,
    string? EventId = null, string[]? LinkedEventIds = null)
{
    public string MeasurementLimits { get; } = "통화당 여러 라운드를 준비할 수 있으며 각 라운드의 첫 사건만 자동 보존합니다. 공통 사건 ID는 기록 요청의 연계를 뜻하며 같은 물리 원인이나 시계 동기화를 보장하지 않습니다. 일반 통화는 상대의 인증된 협상 음성을 처음 수신한 뒤 2초간 트리거에서 제외합니다. 그 전에도 배경 메타데이터는 순환 기록합니다. 사건 전후는 레코드 수로 제한되며 후속 기록은 최대 2초입니다. At/SourceAt은 이 프로세스의 단조 시각(초)이고 다른 PC 시각과 직접 차감할 수 없습니다. send-api-return은 OS 소켓 전송 완료가 아닙니다. rtp-channel-observer는 SDK 후속 이벤트이며 NIC 수신·인증 전 경계가 아닙니다. SRTP 거부 사유·물리 네트워크 손실·GC 정지 시간은 확정하지 않습니다. TriggerGen은 사건 순간 GC 누적 횟수입니다.";
}

// First incident per call: bounded prehistory, then two seconds of aftermath.
// No per-record allocations, blocking audio locks, timers or disk writes.
internal sealed class IncidentRecorder(int capacity = 8192)
{
    private readonly IncidentRecord[] records = new IncidentRecord[capacity is >= 4 and <= 32768
        ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity))];
    private readonly object sync = new();
    private int next, count, state, postCount; // 0 stopped, 1 rolling, 2 post-trigger, 3 frozen
    private long overwritten, skipped;
    private double armedAt, triggerAt, until;
    private bool triggersArmed;
    private string? trigger;
    private string? endReason;
    private int? gc0, gc1, gc2;
    private string preparedId = Guid.NewGuid().ToString("N");
    private readonly List<string> linkedIds = new(6);
    internal object SyncForChecks => sync;
    public bool Active => Volatile.Read(ref state) is 1 or 2;
    public string Status => Volatile.Read(ref state) switch
    { 1 => Volatile.Read(ref triggersArmed) ? "순환 기록 중" : "순환 기록 중 · 상대 음성 대기", 2 => "사건 후 기록 중", 3 => "첫 사건 보존 완료", _ => "중지" };
    public void Reset(double at, bool armTriggers = true)
    {
        lock (sync)
        {
            Array.Clear(records); next = count = 0; overwritten = skipped = 0;
            armedAt = at; triggerAt = until = 0; trigger = endReason = null; postCount = 0; gc0 = gc1 = gc2 = null;
            preparedId = Guid.NewGuid().ToString("N"); linkedIds.Clear();
            Volatile.Write(ref triggersArmed, armTriggers);
            Volatile.Write(ref state, 1);
        }
    }
    public void ArmTriggers(double at)
    {
        if (Volatile.Read(ref triggersArmed) || !Active) return;
        if (!Monitor.TryEnter(sync)) { Interlocked.Increment(ref skipped); return; }
        try
        {
            if (state != 1 || triggersArmed) return;
            armedAt = at;
            Volatile.Write(ref triggersArmed, true);
        }
        finally { Monitor.Exit(sync); }
    }
    public void Stop() { lock (sync) { endReason ??= "call-ended"; Volatile.Write(ref state, 0); } }
    internal (string? Id, string? Cause, int Count, long Skipped, long Overwritten) Header()
    { lock (sync) return (trigger is null ? null : preparedId, trigger, count, Interlocked.Read(ref skipped), overwritten); }
    internal string AcceptRemote(string id, double now)
    {
        if (!Guid.TryParseExact(id, "N", out _)) return "invalid";
        lock (sync)
        {
            if (trigger is not null && (preparedId == id || linkedIds.Contains(id))) return "accepted";
            if (state == 1 && triggersArmed && now - armedAt >= 2)
            {
                records[next] = new(now, "remote-incident", Outcome: "remote-request"); next = (next + 1) % records.Length;
                if (count == records.Length) overwritten++; else count++;
                preparedId = id; BeginTrigger("remote-request", now); return "accepted";
            }
            // Simultaneous local/remote notices can name the same surrounding
            // interval differently. Preserve both IDs without equating their clocks.
            if (trigger is not null && state is 2 or 3 && now <= until + 1 && linkedIds.Count < 6)
            { linkedIds.Add(id); return "linked"; }
            return state == 1 ? "not-ready" : "busy";
        }
    }
    private void BeginTrigger(string cause, double at)
    {
        trigger = cause; triggerAt = at; until = at + 2;
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        if (count > records.Length / 2) { overwritten += count - records.Length / 2; count = records.Length / 2; }
        Volatile.Write(ref state, 2);
    }
    public void Write(IncidentRecord record, string? cause = null)
    {
        if (!Active) return;
        if (!Monitor.TryEnter(sync)) { Interlocked.Increment(ref skipped); return; }
        try
        {
            if (state is not (1 or 2)) return;
            if (state == 2 && (record.At > until || postCount >= records.Length / 2))
            { endReason = record.At > until ? "duration-limit" : "capacity-limit"; Volatile.Write(ref state, 3); return; }
            if (count == records.Length) overwritten++; else count++;
            records[next] = record; next = (next + 1) % records.Length;
            if (state == 2) postCount++;
            // Ignore startup warmup as a trigger, but retain its metadata.
            if (state == 1 && triggersArmed && cause is not null && record.At - armedAt >= 2)
            {
                // Reserve half for aftermath so a high peer count cannot overwrite
                // the trigger and all prehistory before the two-second limit.
                BeginTrigger(cause, record.At);
            }
        }
        finally { Monitor.Exit(sync); }
    }
    public IncidentSnapshot Snapshot(double now)
    {
        lock (sync)
        {
            if (state == 2 && now > until) { endReason = "duration-limit"; Volatile.Write(ref state, 3); }
            var result = new IncidentRecord[count];
            var first = (next - count + records.Length) % records.Length;
            for (var i = 0; i < count; i++) result[i] = records[(first + i) % records.Length];
            return new(state switch { 1 => "Rolling", 2 => "AfterTrigger", 3 => "Frozen", _ => "Stopped" },
                trigger, trigger is null ? null : triggerAt, trigger is null ? null : until,
                records.Length, overwritten, Interlocked.Read(ref skipped), result, gc0, gc1, gc2, endReason,
                triggersArmed ? armedAt : null, trigger is null ? null : preparedId, linkedIds.ToArray());
        }
    }
}
