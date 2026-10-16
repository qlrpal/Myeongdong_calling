"""Correlate xperf raw-QPC DPC/ISR and runnable-thread waits with one host's app gaps."""
import csv
import json
import re
import sys
from io import StringIO
from contextlib import nullcontext
from bisect import bisect_left, bisect_right
from collections import defaultdict
from pathlib import Path
from analyze_pipeline import stats


def records(path, frequency):
    at = None
    context = nullcontext(path) if hasattr(path, "read") else Path(path).open(encoding="utf-8-sig", newline="")
    with context as handle:
        for line in handle:
            if line.startswith("#"):
                match = re.match(r"#\s+-?\d+\s+#\s+(0x[0-9a-fA-F]+)", line)
                at = int(match[1], 16) / frequency if match else None
            elif at is not None and line.strip():
                row = [v.strip() for v in next(csv.reader([line], skipinitialspace=True))]
                yield at, row
                at = None


def union(intervals):
    merged = []
    for start, end in sorted(intervals):
        if not merged or start > merged[-1][1]:
            merged.append([start, end])
        else:
            merged[-1][1] = max(end, merged[-1][1])
    return sum(b - a for a, b in merged), max((b - a for a, b in merged), default=0)


def own(name, pid):
    match = re.search(r"\(\s*(\d+)\s*\)$", name)
    return bool(match and int(match[1]) == pid)


def ready_waits(path, frequency, pid, low, high):
    pending = {}; waits = []; seen = defaultdict(int)
    for at, row in records(path, frequency):
        kind = row[0]
        if kind == "ReadyThread":
            if len(row) != 10:
                raise ValueError("Unsupported ReadyThread schema")
            if own(row[4], pid):
                pending.setdefault(int(row[5]), at); seen[kind] += 1
        elif kind == "CSwitch":
            if len(row) < 17:
                raise ValueError("Unsupported CSwitch schema")
            if own(row[2], pid):
                tid = int(row[3]); start = pending.pop(tid, None); seen[kind] += 1
                if start is not None and low <= at <= high and start <= at:
                    waits.append(dict(start=start, end=at, tid=tid, delayMs=(at - start) * 1000))
            if own(row[8], pid) and row[9] != row[3]:
                tid = int(row[9])
                if row[12] == "Ready":
                    pending.setdefault(tid, at)
                else:
                    pending.pop(tid, None)
    return waits, dict(seen)


def analyze(app_path, dpc_path, thread_path, udp_path, trace_stats_path, output_path):
    app = json.loads(Path(app_path).read_text(encoding="utf-8-sig"))
    udp = json.loads(Path(udp_path).read_text(encoding="utf-8-sig"))
    summary = Path(trace_stats_path).read_text(encoding="utf-8-sig")
    if app.get("error") or app["trace"]["EvictedRecords"] or udp.get("clockSource") != "ETW raw QPC":
        raise ValueError("Incomplete app trace or missing raw QPC frequency")
    for label in ("Events", "Buffers"):
        match = re.search(r"Total # Lost " + label + r"\s*:\s*(\d+)", summary)
        if not match or int(match[1]):
            raise ValueError("Missing or nonzero scheduling trace loss")
    frequency, pid = udp["frequency"], udp["processId"]
    rx = sorted(app["trace"]["Receives"], key=lambda r: r["At"])
    low, high = rx[0]["At"] + 5, rx[-1]["At"] - 1
    events = []; modules = defaultdict(list); invalid = 0; first = None; last = None
    for at, row in records(dpc_path, frequency):
        first = at if first is None else min(first, at)
        last = at if last is None else max(last, at)
        if row[0] not in ("DPC", "Interrupt"):
            continue
        if row[0] == "DPC" and len(row) != 6 or row[0] == "Interrupt" and len(row) != 9:
            raise ValueError("Unsupported xperf DPC/ISR schema")
        duration = float(row[2]) / 1_000_000
        if duration < 0 or duration > high - low:
            invalid += 1; continue
        if not low <= at <= high:
            continue
        event = dict(start=at - duration, end=at, durationMs=duration * 1000,
            cpu=int(row[3]), kind=row[0], module=row[-1].split("!")[0].strip())
        events.append(event); modules[event["module"]].append(event)
    if not events:
        raise ValueError("No DPC/ISR coverage during the app analysis window")
    if first > low or last < high:
        raise ValueError("DPC/ISR export does not cover the whole app analysis window")
    waits, seen = ready_waits(thread_path, frequency, pid, low, high)
    if not seen.get("CSwitch") or not seen.get("ReadyThread"):
        raise ValueError("Missing context-switch or ready-thread coverage for the test process")
    events.sort(key=lambda e: e["start"])
    starts = [e["start"] for e in events]
    longest = max(e["durationMs"] for e in events) / 1000
    gaps = []
    for a, b in zip(rx, rx[1:]):
        start, end = a["At"], b["At"]
        if not low <= start < end <= high or end - start <= .030:
            continue
        candidates = events[bisect_left(starts, start - longest):bisect_right(starts, end)]
        intersect = [e for e in candidates if e["end"] >= start]
        cpus = defaultdict(list)
        for e in intersect:
            cpus[e["cpu"]].append((max(start, e["start"]), min(end, e["end"])))
        busy = [(cpu, *union(values)) for cpu, values in cpus.items()]
        nearby_waits = [w for w in waits if w["start"] <= end and w["end"] >= start]
        gaps.append(dict(at_seconds=round(end - rx[0]["At"], 3), receive_gap_ms=round((end - start) * 1000, 3),
            max_single_dpc_isr_ms=round(max((e["durationMs"] for e in intersect), default=0), 3),
            busiest_cpu=max(busy, key=lambda item: item[1])[0] if busy else None,
            max_cpu_busy_union_ms=round(max((item[1] for item in busy), default=0) * 1000, 3),
            max_contiguous_cpu_busy_ms=round(max((item[2] for item in busy), default=0) * 1000, 3),
            max_ready_to_run_ms=round(max((w["delayMs"] for w in nearby_waits), default=0), 3),
            longest_events=sorted(intersect, key=lambda e: e["durationMs"], reverse=True)[:3]))
    result = dict(role=app["role"], note="DPC/ISR elapsed time may nest: per-CPU unions avoid double counting. Ready-to-run covers observed OS runnable threads, not managed work queues or DPC queueing. Correlation is not causation. Sleep/SMI/firmware/network and hardware arrival remain unmeasured.",
        trace_lost_events=0, trace_lost_buffers=0, excluded_invalid_elapsed_events=invalid,
        dpc_ms=stats(e["durationMs"] for e in events if e["kind"] == "DPC"),
        isr_ms=stats(e["durationMs"] for e in events if e["kind"] == "Interrupt"),
        ready_to_run_ms=stats(w["delayMs"] for w in waits), scheduler_events_for_process=seen,
        modules=sorted([dict(module=name, count=len(values), max_ms=max(e["durationMs"] for e in values),
            sum_event_elapsed_ms=sum(e["durationMs"] for e in values)) for name, values in modules.items()], key=lambda m: m["max_ms"], reverse=True),
        largest_ready_waits=sorted(waits, key=lambda w: w["delayMs"], reverse=True)[:15],
        receive_gaps=sorted(gaps, key=lambda g: g["receive_gap_ms"], reverse=True))
    Path(output_path).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(result["role"], "DPC", result["dpc_ms"], "ISR", result["isr_ms"], "ready", result["ready_to_run_ms"])
    for gap in result["receive_gaps"]:
        print({k: v for k, v in gap.items() if k != "longest_events"})


def self_test():
    assert union([(0, 10), (2, 4), (9, 12)]) == (12, 12)
    assert union([(0, 1), (2, 3)]) == (2, 1)
    assert own("TransportProbe.exe ( 2052)", 2052) and not own("other.exe (20521)", 2052)
    assert union([]) == (0, 0)
    # A blocked thread's 1-second sleep must not become runnable CPU wait.
    rows = [
        (1000, ["ReadyThread", "0", "other (9)", "9", "probe (123)", "1", "0", "0", "0", "0"]),
        (1010, ["CSwitch", "0", "probe (123)", "1", "0", "0", "0", "0", "other (9)", "9", "0", "0", "Waiting", "0", "0", "0", "0"]),
        (1020, ["CSwitch", "0", "other (9)", "9", "0", "0", "0", "0", "probe (123)", "1", "0", "0", "Waiting", "0", "0", "0", "0"]),
        (2020, ["CSwitch", "0", "probe (123)", "1", "0", "0", "0", "0", "other (9)", "9", "0", "0", "Waiting", "0", "0", "0", "0"]),
        (2030, ["CSwitch", "0", "other (9)", "9", "0", "0", "0", "0", "probe (123)", "1", "0", "0", "Ready", "0", "0", "0", "0"]),
        (2035, ["CSwitch", "0", "probe (123)", "1", "0", "0", "0", "0", "other (9)", "9", "0", "0", "Waiting", "0", "0", "0", "0"])]
    fixture = "".join(f"# 0 # {at:#x} # 0 # 0\n" + ",".join(row) + "\n" for at, row in rows)
    with StringIO(fixture) as handle:
        waits, _ = ready_waits(handle, 1000, 123, 0, 3)
        assert len(waits) == 2 and abs(waits[0]["delayMs"] - 10) < .001 and abs(waits[1]["delayMs"] - 5) < .001
    print("PASS: nested DPC/ISR union, exact PID, raw QPC units, runnable/preempted wait and exclusion of blocked sleep")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 7:
        analyze(*sys.argv[1:])
    else:
        raise SystemExit("Usage: analyze_scheduling.py APP_JSON DPC_RAW_CSV THREAD_RAW_CSV UDP_JSON XPERF_STATS OUTPUT_JSON")
