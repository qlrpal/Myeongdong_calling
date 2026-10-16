"""Correlate independent ICMP control with pipeline gaps using the PC's QPC clock."""
import json
import sys
from pathlib import Path
from analyze_pipeline import stats

icmp, pipeline = [json.loads(Path(p).read_text(encoding="utf-8-sig")) for p in sys.argv[1:3]]
targets = []
for row in icmp["rows"]:
    samples = row["samples"]
    targets.append(dict(target=row["target"], count=len(samples),
        failed=sum(s.get("status") != "Success" for s in samples),
        kernel_rtt_ms=stats(s.get("kernelRttMs") for s in samples),
        managed_elapsed_ms=stats((s["completedAt"] - s["startedAt"]) * 1000 for s in samples)))
gaps = []
for direction in pipeline["directions"]:
    for gap in direction["largest_receive_gaps"]:
        pc_at = gap["source_send_end"] if direction["direction"].startswith("pc ") else gap["target_received_at"]
        controls = []
        for row in icmp["rows"]:
            # Same-host monotonic time only; ICMP packet transmission may precede its managed completion.
            nearby = [s for s in row["samples"] if s["startedAt"] <= pc_at + .1 and s["completedAt"] >= pc_at - .1]
            controls.append(dict(target=row["target"], samples=len(nearby),
                kernel_rtt_ms=stats(s.get("kernelRttMs") for s in nearby),
                managed_elapsed_ms=stats((s["completedAt"] - s["startedAt"]) * 1000 for s in nearby)))
        heartbeat = [h["intervalMs"] for h in icmp.get("heartbeat", [])
            if h["at"] - h["intervalMs"] / 1000 <= pc_at + .1 and h["at"] >= pc_at - .1]
        gaps.append(dict(direction=direction["direction"], gap=gap, controls=controls, heartbeat_interval_ms=stats(heartbeat)))
result = dict(note="Correlation within +/-100ms, not proof of shared cause. ICMP and RTP use different protocols; concurrent traffic may affect Wi-Fi behavior. Dedicated thread intervals include timer resolution, process GC and scheduling.", targets=targets,
    heartbeat_interval_ms=stats(h["intervalMs"] for h in icmp.get("heartbeat", [])), gaps=gaps)
Path(sys.argv[3]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
for row in targets:
    print(row)
for row in gaps:
    print(row["direction"], row["gap"]["receive_gap_ms"], [(c["target"], c["kernel_rtt_ms"]) for c in row["controls"]], "heartbeat", row["heartbeat_interval_ms"])
