"""Compare OS event intervals on both hosts, never their absolute clocks."""
import json
import sys
from collections import Counter
from pathlib import Path
from analyze_pipeline import direction
from analyze_windows_network import load_host, compare_gap


def classify(send, receive):
    if receive["category"] == "os-events-present-during-app-gap":
        return "receiver-app-delivery-needs-investigation"
    sg, rg = send["nearest_os_gap_ms"], receive["nearest_os_gap_ms"]
    if send["category"] == "unresolved" or receive["category"] == "unresolved" or sg is None or rg is None:
        return "unresolved"
    if sg >= rg * .65:
        return "source-os-send-gap-already-present"
    if rg - sg >= 20:
        return "variation-between-os-send-and-os-receive"
    return "mixed"


def analyze(source, target):
    matched = direction(source["app"], target["app"])
    results = []
    for gap in matched["receive_gaps"]:
        se, re = gap["source_send_end"], gap["target_received_at"]
        source_os = compare_gap(source["sends"], se - gap["send_gap_ms"] / 1000, se,
            source["metadata"]["utc_qpc_uncertainty_ms"])
        receive_os = compare_gap(target["receives"], re - gap["receive_gap_ms"] / 1000, re,
            target["metadata"]["utc_qpc_uncertainty_ms"])
        results.append(dict(at_seconds=gap["at_seconds"], app_send_gap_ms=gap["send_gap_ms"],
            app_receive_gap_ms=gap["receive_gap_ms"], source_os=source_os, receiver_os=receive_os,
            category=classify(source_os, receive_os)))
    return dict(direction=matched["direction"], matched_packets=matched["matched_packets"],
        analyzed_packets=matched["analyzed_packets"], gap_count=len(results),
        categories=dict(Counter(r["category"] for r in results)),
        gaps=sorted(results, key=lambda r: r["app_receive_gap_ms"], reverse=True))


def self_test():
    source = compare_gap([0, .01, .02], .001, .011, 1)
    target = compare_gap([500, 500.06], 500.001, 500.061, 1)
    assert classify(source, target) == "variation-between-os-send-and-os-receive"
    assert classify(target, target) == "source-os-send-gap-already-present"
    target_shifted = compare_gap([90000, 90000.06], 90000.001, 90000.061, 1)
    assert classify(source, target_shifted) == classify(source, target)
    assert classify(compare_gap([], 0, .01, 1), target) == "unresolved"
    print("PASS: dual-host interval classification, independent host-clock offsets and missing coverage")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 10:
        pc = load_host(*sys.argv[1:5]); laptop = load_host(*sys.argv[5:9])
        if pc["app"]["role"] != "pc" or laptop["app"]["role"] != "laptop":
            raise ValueError("Expected PC inputs followed by laptop inputs")
        if any(h["metadata"]["clock_source"] != "ETW raw QPC" for h in (pc, laptop)):
            raise ValueError("Dual-host analysis requires tracerpt -rts raw QPC exports on both hosts")
        result = dict(note="Compare same-host intervals across two hosts. Not absolute one-way latency. UDP events include RTP/RTCP/STUN/DTLS and lack RTP identity. OS send/receive events are not NIC wire timestamps. Classification is a boundary-localization heuristic.",
            metadata={h["app"]["role"]: h["metadata"] for h in (pc, laptop)},
            directions=[analyze(pc, laptop), analyze(laptop, pc)])
        Path(sys.argv[9]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        for d in result["directions"]:
            print(d["direction"], d["gap_count"], d["categories"])
    else:
        raise SystemExit("Usage: analyze_dual_os.py PC_JSON PC_UDP PC_ANCHOR PC_SUMMARY LAPTOP_JSON LAPTOP_UDP LAPTOP_ANCHOR LAPTOP_SUMMARY OUTPUT_JSON")
