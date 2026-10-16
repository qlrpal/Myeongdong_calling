"""Analyze timestamp-only two-PC traces. Uses standard library; no audio is read."""
import json
import math
import sys
from bisect import bisect_left
from collections import Counter
from pathlib import Path


def stats(values):
    values = sorted(v for v in values if v is not None and math.isfinite(v) and v >= 0)
    if not values:
        return {"count": 0}
    def at(q):
        return round(values[min(len(values) - 1, math.ceil(q * len(values)) - 1)], 3)
    return {"count": len(values), "p50": at(.5), "p95": at(.95), "p99": at(.99), "max": at(1)}


def delta(end, start):
    return (end - start) * 1000 if end is not None and start is not None else None


def key(row):
    return row["Ssrc"], row["Timestamp"]


def classify(receive_gap, send_gap, callback_gap, queue_wait, send_call, preprocess=0):
    # These are interval differences, not absolute cross-host timestamp differences.
    if send_gap >= receive_gap * .65:
        if preprocess is not None and preprocess >= send_gap * .5:
            return "capture-preprocess-or-probe"
        if callback_gap is not None and callback_gap >= send_gap * .65:
            return "capture-callback-delivery"
        if queue_wait is not None and queue_wait >= send_gap * .5:
            return "send-queue-or-dispatch"
        if send_call >= send_gap * .5:
            return "send-call-or-scheduling"
        return "sender-other"
    if receive_gap - send_gap >= receive_gap * .5:
        return "post-send-before-receive-callback"
    return "mixed"


def direction(source, target):
    tx = {key(s): s for s in source["trace"]["Sends"]}
    rx = {}
    for r in sorted(target["trace"]["Receives"], key=lambda r: r["At"]):
        rx.setdefault(key(r), r)
    admission = {key(a): a for a in target["trace"]["Admissions"] if a["Outcome"] == "queued"}
    latest_admission = {key(a): a for a in target["trace"]["Admissions"]}
    played = {key(p): p for p in target["trace"]["Playout"] if p["Outcome"] == "played"}
    matched = [(tx[k], r) for k, r in rx.items() if k in tx]
    if not matched:
        raise ValueError("No SSRC/timestamp matches between peers")
    # Ignore start/stop boundaries; either peer may have started tracing first.
    low, high = matched[0][1]["At"] + 5, matched[-1][1]["At"] - 1
    pairs = [(s, r) for s, r in matched if low <= r["At"] <= high]
    render_locks = sorted(r["LockAt"] for r in target["trace"]["Reads"])
    timing = {name: [] for name in ("first_sample_to_first_callback", "first_callback_to_frame_ready",
        "callback_conversion_before_lock", "capture_lock_wait", "frame_ready_to_encode", "encode",
        "encode_to_queue", "send_queue", "dispatch_before_send", "send_call", "receive_preamble",
        "admission_lock_wait", "admission_work", "admitted_queue", "receive_to_next_render",
        "next_render_to_decode", "receive_to_decode", "decode", "decode_to_submit")}
    source_intervals, receive_intervals, extra_intervals, gaps = [], [], [], []
    callback_sizes = {}
    previous = None
    for s, r in pairs:
        c = s.get("Capture") or {}
        ident = key(r)
        a, p = admission.get(ident), played.get(ident)
        def add(name, end, start):
            timing[name].append(delta(end, start))
        add("first_sample_to_first_callback", c.get("FirstCallbackAt"), c.get("FirstSampleAt"))
        add("first_callback_to_frame_ready", c.get("FrameReadyAt"), c.get("FirstCallbackAt"))
        add("callback_conversion_before_lock", c.get("LockRequestedAt"), c.get("CallbackAt"))
        add("capture_lock_wait", c.get("LockAcquiredAt"), c.get("LockRequestedAt"))
        add("frame_ready_to_encode", c.get("EncodeStart"), c.get("FrameReadyAt"))
        add("encode", c.get("EncodeEnd"), c.get("EncodeStart"))
        add("encode_to_queue", s["QueuedAt"], c.get("EncodeEnd"))
        add("send_queue", s["DequeuedAt"], s["QueuedAt"])
        add("dispatch_before_send", s["SendStart"], s["DequeuedAt"])
        add("send_call", s["SendEnd"], s["SendStart"])
        add("receive_preamble", r["BufferCallAt"], r["At"])
        if a:
            add("admission_lock_wait", a["LockAt"], a["CalledAt"])
            add("admission_work", a["ReturnedAt"], a["LockAt"])
            timing["admitted_queue"].append(a["QueueMs"])
            index = bisect_left(render_locks, a["ReturnedAt"])
            if p and index < len(render_locks) and render_locks[index] <= p["DecodeStart"]:
                add("receive_to_next_render", render_locks[index], r["At"])
                add("next_render_to_decode", p["DecodeStart"], render_locks[index])
        if p:
            add("receive_to_decode", p["DecodeStart"], r["At"])
            add("decode", p["DecodeEnd"], p["DecodeStart"])
            add("decode_to_submit", p["SubmittedAt"], p["DecodeEnd"])
        if c.get("CallbackAt"):
            callback_sizes[c["CallbackAt"]] = c["CallbackSamples"] / 48
        if previous:
            ps, pr = previous
            if s["Sequence"] > ps["Sequence"]:
                sg = delta(s["SendEnd"], ps["SendEnd"])
                rg = delta(r["At"], pr["At"])
                source_intervals.append(sg); receive_intervals.append(rg)
                extra_intervals.append(max(0, rg - sg))
                if rg > 30:
                    pc = ps.get("Capture") or {}
                    cb = delta(c.get("CallbackAt"), pc.get("CallbackAt"))
                    qw = delta(s["DequeuedAt"], s["QueuedAt"])
                    call = delta(s["SendEnd"], s["SendStart"])
                    preprocess = delta(c.get("EncodeStart"), c.get("FrameReadyAt"))
                    outcome = latest_admission.get(ident, {}).get("Outcome", "unmatched")
                    gaps.append(dict(at_seconds=round(r["At"] - low + 5, 3),
                        receive_gap_ms=round(rg, 3), send_gap_ms=round(sg, 3),
                        post_send_gap_excess_ms=round(rg - sg, 3),
                        capture_callback_gap_ms=round(cb, 3) if cb is not None else None,
                        capture_sample_age_ms=delta(c.get("CallbackAt"), c.get("FirstSampleAt")),
                        callback_samples=c.get("CallbackSamples"), send_queue_ms=qw,
                        send_call_ms=call, capture_preprocess_ms=preprocess, admission=outcome,
                        source_send_end=s["SendEnd"], target_received_at=r["At"],
                        category=classify(rg, sg, cb, qw, call, preprocess)))
        previous = s, r
    reads = target["trace"]["Reads"]
    reads = [r for r in reads if low <= r["CalledAt"] <= high]
    read_gaps = [dict(gap_ms=round(delta(b["CalledAt"], a["CalledAt"]), 3),
        work_ms=round(delta(b["ReturnedAt"], b["LockAt"]), 3), queue_before_ms=b["QueueBeforeMs"],
        requested_ms=b["RequestedSamples"] / 48, provided_ms=b["ProvidedSamples"] / 48)
        for a, b in zip(reads, reads[1:]) if delta(b["CalledAt"], a["CalledAt"]) > 30]
    return dict(direction=source["role"] + " -> " + target["role"], matched_packets=len(matched), analyzed_packets=len(pairs),
        unmatched_source_keys=len(tx.keys() - rx.keys()), unmatched_receive_keys=len(rx.keys() - tx.keys()),
        note="Unmatched includes trace boundaries; not a packet-loss estimate. Cross-host comparisons use interval differences only.",
        stages_ms={k: stats(v) for k, v in timing.items()}, callback_chunk_ms=stats(callback_sizes.values()),
        send_interval_ms=stats(source_intervals), receive_interval_ms=stats(receive_intervals),
        positive_post_send_gap_excess_ms=stats(extra_intervals),
        gaps_over_30ms=len(gaps), gap_categories=dict(Counter(g["category"] for g in gaps)),
        receive_gaps=gaps,
        largest_receive_gaps=sorted(gaps, key=lambda g: g["receive_gap_ms"], reverse=True)[:15],
        admission_outcomes=dict(Counter(a["Outcome"] for a in target["trace"]["Admissions"])),
        read_request_ms=stats(r["RequestedSamples"] / 48 for r in reads),
        read_lock_wait_ms=stats(delta(r["LockAt"], r["CalledAt"]) for r in reads),
        read_work_ms=stats(delta(r["ReturnedAt"], r["LockAt"]) for r in reads),
        read_gaps_over_30ms=len(read_gaps), largest_read_gaps=sorted(read_gaps, key=lambda r: r["gap_ms"], reverse=True)[:10])


def self_test():
    assert classify(70, 10, 10, .1, .1) == "post-send-before-receive-callback"
    assert classify(72, 70, 70, .1, .1) == "capture-callback-delivery"
    assert classify(72, 70, 10, 60, .1) == "send-queue-or-dispatch"
    assert classify(123, 122, 4, .1, .6, 117) == "capture-preprocess-or-probe"
    a = delta(1000.070, 1000.000) - delta(1.010, 1.000)
    b = delta(91000.070, 91000.000) - delta(1.010, 1.000)
    assert abs(a - b) < .000001
    assert stats([None, -1, 0, 10, 20])["p50"] == 10
    source = dict(role="pc", trace=dict(Sends=[dict(Ssrc=1, Timestamp=i * 480, Sequence=i,
        QueuedAt=i, DequeuedAt=i + .001, SendStart=i + .002, SendEnd=i + .003) for i in range(10)]))
    def target(offset):
        trace = dict(Receives=[], Admissions=[], Playout=[], Reads=[])
        for i in range(10):
            t = offset + i
            identity = dict(Ssrc=1, Timestamp=i * 480)
            trace["Receives"].append(dict(**identity, At=t, BufferCallAt=t, BufferReturnAt=t + .001))
            trace["Admissions"].append(dict(**identity, Outcome="queued", CalledAt=t, LockAt=t,
                ReturnedAt=t + .001, QueueMs=30))
            trace["Playout"].append(dict(**identity, Outcome="played", DecodeStart=t + .015,
                DecodeEnd=t + .016, SubmittedAt=t + .016))
            trace["Reads"].append(dict(CalledAt=t + .005, LockAt=t + .005, ReturnedAt=t + .006,
                QueueBeforeMs=30, RequestedSamples=480, ProvidedSamples=480))
        return dict(role="laptop", trace=trace)
    baseline, shifted = direction(source, target(1000)), direction(source, target(91000))
    assert baseline["stages_ms"] == shifted["stages_ms"]
    assert baseline["stages_ms"]["receive_to_next_render"]["p50"] == 5
    assert baseline["stages_ms"]["next_render_to_decode"]["p50"] == 10
    print("PASS: gap localization, cross-host clock-offset invariance and missing observations")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        if len(sys.argv) != 4:
            raise SystemExit("Usage: analyze_pipeline.py PC_JSON LAPTOP_JSON OUTPUT_JSON")
        pc, laptop = [json.loads(Path(p).read_text(encoding="utf-8-sig")) for p in sys.argv[1:3]]
        for report in (pc, laptop):
            if report.get("error") or report["trace"]["EvictedRecords"]:
                raise ValueError("Incomplete/evicted trace cannot support gap localization")
        result = dict(notes="Timestamp-only diagnosis. Send-return to receive-callback variation includes network, OS and library scheduling; not wire latency or mouth-to-ear latency.",
            recorder_max_ms={r["role"]: r["trace"]["MaxRecorderMs"] for r in (pc, laptop)},
            directions=[direction(pc, laptop), direction(laptop, pc)])
        Path(sys.argv[3]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        for d in result["directions"]:
            print(d["direction"], "matched", d["matched_packets"], "gap categories", d["gap_categories"])
