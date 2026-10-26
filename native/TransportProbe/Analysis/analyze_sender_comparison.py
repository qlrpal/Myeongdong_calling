"""Sender comparison: host-local durations only; never subtract two PCs' clocks."""
import collections
import json
import math
import sys
from pathlib import Path

from analyze_group_probe import analyze


def distribution(values):
    values = sorted(values)
    if any(not math.isfinite(v) or v < 0 for v in values):
        raise ValueError("Invalid host-local duration")
    def at(q):
        return values[math.ceil(len(values) * q) - 1] if values else None
    return dict(count=len(values), p50=at(.5), p95=at(.95), p99=at(.99), maximum=at(1))


def summarize(report):
    group = analyze(report)
    output = []
    phase_traces = report.get("phaseTraces")
    if not phase_traces or set(phase_traces) != {m["Name"] for m in report["membership"]}:
        raise ValueError("Complete per-phase traces required")
    for phase, traces in phase_traces.items():
        callbacks, by_frame, sends, admissions, playout, reads = [], collections.defaultdict(list), [], [], [], []
        native_handler_work = []; missing_callback_origins = 0
        recorder_max = 0
        for slot, trace in traces.items():
            if trace["EvictedRecords"] or not trace.get("CaptureCallbacks"):
                raise ValueError("Missing or evicted capture metadata")
            if not trace["Sends"] or not trace["Reads"]:
                raise ValueError("No sender/render trace")
            recorder_max = max(recorder_max, trace["MaxRecorderMs"])
            # Remove recorder start/stop edges; this is not a steady-state warmup.
            low = max(trace["CaptureCallbacks"][0]["CalledAt"], trace["Sends"][0]["SendStart"]) + .25
            high = min(trace["CaptureCallbacks"][-1]["ReturnedAt"], trace["Sends"][-1]["SendEnd"]) - .25
            if high - low < 2:
                raise ValueError("Insufficient interior trace")
            for c in trace["CaptureCallbacks"]:
                if low <= c["CalledAt"] <= c["ReturnedAt"] <= high:
                    if not c["CalledAt"] <= c["LockAt"] <= c["ReturnedAt"]:
                        raise ValueError("Invalid capture chronology")
                    callbacks.append(c)
            selected = [s for s in trace["Sends"] if low <= s["SendStart"] <= s["SendEnd"] <= high]
            for s in selected:
                by_frame[(slot, s["Sequence"])].append(s)
            callback_origins = {v["Capture"]["LockRequestedAt"]:v["Capture"]["CallbackAt"] for v in selected if v.get("Capture")}
            for c in trace["CaptureCallbacks"]:
                if low <= c["CalledAt"] <= c["ReturnedAt"] <= high:
                    if c["CalledAt"] in callback_origins:
                        native_handler_work.append((c["ReturnedAt"]-callback_origins[c["CalledAt"]])*1000)
                    else:missing_callback_origins += 1
            sends.extend(selected)
            admissions.extend((slot, a) for a in trace["Admissions"] if low <= a["LockAt"] <= high)
            playout.extend((slot, p) for p in trace["Playout"] if low <= p["At"] <= high)
            reads.extend(r for r in trace["Reads"] if low <= r["LockAt"] <= high)
        if not callbacks or not by_frame:
            raise ValueError("Empty comparison interior")
        first = [min(v, key=lambda s: s["SendStart"]) for v in by_frame.values()]
        fanout = [(max(s["SendEnd"] for s in v) - min(s["SendStart"] for s in v)) * 1000 for v in by_frame.values()]
        received = {(slot, a["Peer"], a["Ssrc"], a["Timestamp"]): a["LockAt"] for slot, a in admissions if a["Outcome"] == "queued"}
        admission_to_play = [(p["At"] - received[key]) * 1000 for slot, p in playout
                             if p["Outcome"] == "played" and (key := (slot, p["Peer"], p["Ssrc"], p["Timestamp"])) in received]
        device_to_api = [(s["SendStart"] - s["Capture"]["FirstSampleAt"]) * 1000 for s in first
                         if s.get("Capture") and s["Capture"]["FirstSampleAt"] is not None]
        output.append(dict(phase=phase, traceCallbacks=len(callbacks), traceFrames=len(first),
            callbackWorkMs=distribution((c["ReturnedAt"]-c["CalledAt"])*1000 for c in callbacks),
            nativeHandlerToCaptureReturnMs=distribution(native_handler_work),
            callbacksWithoutNativeOrigin=missing_callback_origins,
            callbackLockWaitMs=distribution((c["LockAt"]-c["CalledAt"])*1000 for c in callbacks),
            inputSamples=distribution(c["InputSamples"] for c in callbacks),
            framesPerCallback=distribution(c["EncodedFrames"] for c in callbacks),
            queueToFirstApiMs=distribution((s["SendStart"]-s["QueuedAt"])*1000 for s in first),
            queueWaitMs=distribution((s["DequeuedAt"]-s["QueuedAt"])*1000 for s in first),
            encodeEndToFirstApiMs=distribution((s["SendStart"]-s["Capture"]["EncodeEnd"])*1000 for s in first if s.get("Capture")),
            encodeEndToLastApiMs=distribution((max(s["SendEnd"] for s in v)-v[0]["Capture"]["EncodeEnd"])*1000
                                                for v in by_frame.values() if v[0].get("Capture")),
            currentCallbackToLastApiMs=distribution((max(s["SendEnd"] for s in v)-v[0]["Capture"]["CallbackAt"])*1000
                                                  for v in by_frame.values() if v[0].get("Capture")),
            firstCallbackToLastApiMs=distribution((max(s["SendEnd"] for s in v)-v[0]["Capture"]["FirstCallbackAt"])*1000
                                                for v in by_frame.values() if v[0].get("Capture")),
            firstCallbackToFirstApiMs=distribution((s["SendStart"]-s["Capture"]["FirstCallbackAt"])*1000 for s in first if s.get("Capture")),
            encodeMs=distribution((s["Capture"]["EncodeEnd"]-s["Capture"]["EncodeStart"])*1000 for s in first if s.get("Capture")),
            reportedFirstSampleToFirstApiMs=distribution(v for v in device_to_api if v >= 0),
            negativeDeviceTimestampDurations=sum(v < 0 for v in device_to_api),
            apiFanoutMs=distribution(fanout),
            perPeerApiMs=distribution((s["SendEnd"]-s["SendStart"])*1000 for s in sends),
            admissionToSubmissionMs=distribution(admission_to_play),
            matchedPlayedPackets=len(admission_to_play),
            admissionOutcomes=dict(collections.Counter(a["Outcome"] for _, a in admissions)),
            playoutOutcomes=dict(collections.Counter(p["Outcome"] for _, p in playout)),
            readWorkMs=distribution((r["ReturnedAt"]-r["CalledAt"])*1000 for r in reads),
            maxRecorderMs=recorder_max))
    return dict(role=report["role"], senderMode=report["senderMode"], engineBuild=report["engineBuild"],
                sdkBuild=report["sdkBuild"], opusBackend=report["opusBackend"], options=report["options"],
                phases=output, group=group,
                limits="Durations stay inside each host QPC. SendEnd is SDK API return, not socket/physical send completion. "
                "Capture work begins after PCM conversion; native-handler work includes preceding conversion but only for callbacks with a matched send origin and excludes raw native lease and trailing record write. "
                "Device first-sample time is reported, not acoustic capture. Admission/submission covers surviving matched packets only. "
                "Five-second trace interiors and full-phase counter/resource windows differ. Same-host streams bypass LAN; three engines per host are not six PCs. "
                "No physical mouth-to-ear, actual game-load, or kernel/SRTP rejection attribution.")


def self_test():
    assert distribution([1, 2, 3, 4])["p50"] == 2
    assert distribution([])["p99"] is None
    for bad in [-1, math.inf, math.nan]:
        try:
            distribution([bad])
        except ValueError:
            pass
        else:
            raise AssertionError("Invalid duration accepted")
    print("PASS: host-local distribution bounds; empty windows remain unknown")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 3:
        source, target = map(Path, sys.argv[1:])
        if source.resolve() == target.resolve():
            raise ValueError("Preserve input")
        result = summarize(json.loads(source.read_text(encoding="utf-8-sig")))
        target.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print("PASS:", result["role"], result["senderMode"], len(result["phases"]), "host-local phase summaries")
    else:
        raise SystemExit("Usage: analyze_sender_comparison.py INPUT.json OUTPUT.json | --self-test")
