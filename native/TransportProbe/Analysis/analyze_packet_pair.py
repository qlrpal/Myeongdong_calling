"""Match bounded sender API and receiver callback traces by SSRC/RTP timestamp.
Never subtract absolute clocks from different hosts or label API return as NIC Tx.
"""
import json
import sys
from pathlib import Path


def trace(document):
    value = document.get("pipeline") or document.get("trace") or document
    if value.get("EvictedRecords", 0):
        raise ValueError("Trace records evicted: missing-packet classification is unsafe")
    if not all(k in value for k in ("Sends", "Receives")):
        raise ValueError("Detailed trace missing; second-level samples cannot reconstruct packets")
    return value


def compare(sender, receiver):
    tx, rx = trace(sender), trace(receiver)
    sent = {}
    for item in tx["Sends"]:
        key = (item["Ssrc"], item["Timestamp"])
        if key in sent:
            raise ValueError("Ambiguous duplicate sender identity")
        sent[key] = item
    received = {}
    for item in rx["Receives"]:
        received.setdefault((item["Ssrc"], item["Timestamp"]), []).append(item)
    durations = {(p["Ssrc"], p["Timestamp"]): p.get("FrameSamples") for p in rx.get("Admissions", [])
                 if p.get("Outcome") == "queued"}
    admissions = {}
    for item in rx.get("Admissions", []):
        admissions.setdefault((item["Ssrc"], item["Timestamp"]), []).append(item.get("Outcome"))
    admission_available = bool(rx.get("Admissions"))
    rows = []
    for ssrc in sorted({key[0] for key in sent}):
        stream = sorted((v for key, v in sent.items() if key[0] == ssrc), key=lambda x: x["Sequence"])
        matched = [i for i, p in enumerate(stream) if (ssrc, p["Timestamp"]) in received]
        if len(matched) < 2:
            continue
        # Missing outside first/last matched boundary can be simple capture start/stop skew.
        interior = stream[matched[0]:matched[-1] + 1]
        missing = [p for p in interior if (ssrc, p["Timestamp"]) not in received]
        mismatches = [p["Timestamp"] for p in interior if (ssrc, p["Timestamp"]) in received
                      and p.get("FrameSamples") is not None
                      and durations.get((ssrc, p["Timestamp"])) is not None
                      and p["FrameSamples"] != durations[(ssrc, p["Timestamp"])]]
        rows.append({"ssrc": ssrc, "interiorSenderFrames": len(interior),
                     "notSeenAtReceiver": len(missing), "missingRtpTimestamps": [p["Timestamp"] for p in missing],
                     "durationMismatches": mismatches,
                     "admissionRecordsAvailable": admission_available,
                     "seenWithoutAdmissionRecord": sum(1 for p in interior
                         if (ssrc, p["Timestamp"]) in received and (ssrc, p["Timestamp"]) not in admissions)
                         if admission_available else None,
                     "observedWithoutQueuedAdmission": sum(1 for p in interior
                         if (ssrc, p["Timestamp"]) in received
                         and "queued" not in admissions.get((ssrc, p["Timestamp"]), []))
                         if admission_available else None,
                     "senderApplicationSequenceGaps": sum(max(0, b["Sequence"] - a["Sequence"] - 1)
                                                          for a, b in zip(interior, interior[1:]))})
    if not rows:
        raise ValueError("No overlapping exact RTP boundaries; wrong direction or insufficient capture")
    return {"streams": rows, "limits": "Only sender API log to receiver callback observation. Not proof of NIC transmission or physical network loss. No cross-host clock subtraction. Sender Sequence is application sequence, not wire RTP sequence. Missing outside matched boundaries excluded."}


def self_test():
    def p(n):
        return {"Ssrc": 7, "Timestamp": (4294967200 + n * 120) % 4294967296,
                "Sequence": n, "FrameSamples": 120}
    tx = {"Sends": [p(n) for n in range(5)], "Receives": [], "EvictedRecords": 0}
    rx = {"Sends": [], "Receives": [p(n) for n in (1, 3, 4)], "EvictedRecords": 0}
    result = compare({"pipeline": tx}, {"pipeline": rx})["streams"][0]
    assert result["notSeenAtReceiver"] == 1 and result["missingRtpTimestamps"] == [p(2)["Timestamp"]]
    rx["Admissions"] = [dict(p(n), Outcome="queued") for n in (1, 4)]
    result = compare(tx, rx)["streams"][0]
    assert result["seenWithoutAdmissionRecord"] == 1 and result["observedWithoutQueuedAdmission"] == 1
    rx["EvictedRecords"] = 1
    try:
        compare(tx, rx)
        raise AssertionError("Eviction accepted")
    except ValueError:
        pass
    try:
        trace({"samples": []})
        raise AssertionError("Summary accepted as packet trace")
    except ValueError:
        pass
    print("PASS: direction/overlap boundaries, wrap, missing interior and eviction/summary rejection")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 4:
        value = compare(*(json.loads(Path(p).read_text(encoding="utf-8-sig")) for p in sys.argv[1:3]))
        Path(sys.argv[3]).write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")
    else:
        raise SystemExit("Usage: analyze_packet_pair.py SEND.json RECEIVE.json OUTPUT.json | --self-test")
