"""Compare PC application receive gaps with payload-free Windows UDP metadata."""
import ipaddress
import json
import re
import struct
import sys
from bisect import bisect_left, bisect_right
from collections import Counter
from datetime import datetime
from pathlib import Path
from analyze_pipeline import stats


def nearest(times, at):
    i = bisect_left(times, at)
    options = times[max(0, i - 1):i + 1]
    return min(options, key=lambda v: abs(v - at)) if options else None


def compare_gap(times, start, end, uncertainty_ms):
    left, right = nearest(times, start), nearest(times, end)
    inner = times[bisect_right(times, start + .005):bisect_left(times, end - .005)]
    gap = (end - start) * 1000
    os_gap = (right - left) * 1000 if left is not None and right is not None else None
    edge_error = max(abs(start - left), abs(end - right)) * 1000 if os_gap is not None else None
    category = "unresolved"
    if os_gap is not None and edge_error <= 5 + uncertainty_ms and abs(os_gap - gap) <= 5 and not inner:
        category = "gap-present-before-os-udp-event"
    elif len(inner) >= 3:
        category = "os-events-present-during-app-gap"
    return dict(app_gap_ms=round(gap, 3), nearest_os_gap_ms=round(os_gap, 3) if os_gap is not None else None,
        edge_error_ms=round(edge_error, 3) if edge_error is not None else None,
        interior_udp_events=len(inner), category=category)


def self_test():
    row = compare_gap([0, .01, .02, .08, .09], .021, .081, 1.3)
    assert row["category"] == "gap-present-before-os-udp-event" and row["nearest_os_gap_ms"] == 60
    assert compare_gap([i * .01 for i in range(11)], .021, .081, 1.3)["category"] == "os-events-present-during-app-gap"
    shifted = compare_gap([10000 + v for v in [0, .01, .02, .08, .09]], 10000.021, 10000.081, 1.3)
    assert row == shifted
    assert compare_gap([], 1, 1.05, 1)["category"] == "unresolved"
    print("PASS: OS/app gap comparison, clock shift invariance, intervening UDP events and missing coverage")


def load_host(pc_path, udp_path, anchor_path, summary_path):
    pc, udp, anchor = [json.loads(Path(p).read_text(encoding="utf-8-sig")) for p in (pc_path, udp_path, anchor_path)]
    if pc.get("error") or pc["trace"]["EvictedRecords"] or anchor["stopExitCode"] != 0:
        raise ValueError("Incomplete trace")
    if pc.get("role") not in ("pc", "laptop") or any(len(s["Peers"]) != 1 for s in pc["samples"]):
        raise ValueError("This analyzer requires a controlled one-peer host trace")
    summary = Path(summary_path).read_text(encoding="utf-8-sig")
    lost = re.search(r"Total Events\s+Lost\s+(\d+)", summary)
    if not lost or int(lost[1]):
        raise ValueError("Missing or nonzero ETW lost-event evidence")
    etl_path = Path(str(anchor_path).removesuffix(".json"))
    if etl_path.stat().st_size >= 32 * 1024 * 1024:
        raise ValueError("Circular trace could have wrapped")
    peer = next(s["Peers"][0] for s in pc["samples"] if s["Peers"] and s["Peers"][0]["Route"] == "직접 연결")
    # This controlled experiment has one IPv4 LAN peer; fail rather than guess on other paths.
    address, remote_port = peer["RemoteEndpoint"].rsplit(":", 1)
    ipaddress.IPv4Address(address)
    local_port = int(peer["LocalEndpoint"].rsplit(":", 1)[1])
    remote_port = int(remote_port)
    utc = datetime.fromisoformat(anchor["utc"]).timestamp()
    qpc = (anchor["qpcBefore"] + anchor["qpcAfter"]) / 2
    raw_clock = udp.get("clockSource") == "ETW raw QPC"
    if raw_clock and udp.get("sessionName") != anchor["session"]:
        raise ValueError("Raw ETW export and session metadata do not match")
    uncertainty = 0 if raw_clock else (anchor["qpcAfter"] - anchor["qpcBefore"]) * 500
    if not 0 <= uncertainty <= 5:
        raise ValueError("UTC/QPC anchor uncertainty too large")
    times, sends = [], []
    def port(value):
        value = int(value)
        return ((value & 255) << 8) | (value >> 8)
    def ip(value):
        return str(ipaddress.IPv4Address(struct.pack("<I", int(value))))
    for row in udp["rows"]:
        if row["id"] not in (42, 43):
            continue
        f = row["fields"]
        if int(f["PID"]) != udp["processId"]:
            raise ValueError("Export mixes processes")
        at = row["qpc"] if raw_clock else qpc + datetime.fromisoformat(row["utc"]).timestamp() - utc
        if row["id"] == 43 and ip(f["saddr"]) == address and port(f["sport"]) == remote_port and port(f["dport"]) == local_port:
            times.append(at)
        if row["id"] == 42 and ip(f["daddr"]) == address and port(f["dport"]) == remote_port and port(f["sport"]) == local_port:
            sends.append(at)
    times.sort(); sends.sort()
    app_rx = pc["trace"]["Receives"]
    low, high = min(r["At"] for r in app_rx) + 5, max(r["At"] for r in app_rx) - 1
    if any(not values or values[0] > low or values[-1] < high for values in (times, sends)):
        raise ValueError("OS send/receive events do not cover the analyzed application window")
    return dict(app=pc, receives=times, sends=sends, metadata=dict(etw_events_lost=int(lost[1]),
        etl_bytes=etl_path.stat().st_size, utc_qpc_uncertainty_ms=uncertainty,
        clock_source="ETW raw QPC" if raw_clock else "UTC/QPC anchor (UTC API resolution not included)"))


def run(pc_path, udp_path, anchor_path, summary_path, output_path):
    host = load_host(pc_path, udp_path, anchor_path, summary_path)
    pc, times, sends = host["app"], host["receives"], host["sends"]
    uncertainty = host["metadata"]["utc_qpc_uncertainty_ms"]
    rx = sorted(pc["trace"]["Receives"], key=lambda r: r["At"])
    low, high = rx[0]["At"] + 5, rx[-1]["At"] - 1
    if not times or times[0] > low or times[-1] < high:
        raise ValueError("OS events do not cover the analyzed app window")
    rows = []
    for a, b in zip(rx, rx[1:]):
        if low <= a["At"] < b["At"] <= high and b["At"] - a["At"] > .030:
            rows.append(dict(at_seconds=round(b["At"] - rx[0]["At"], 3),
                **compare_gap(times, a["At"], b["At"], uncertainty)))
    result = dict(note="Same host only. OS UDP events precede managed RTP callback but are not hardware arrival timestamps. No RTP identity/payload; other traffic on the same tuple includes RTCP/STUN/DTLS. Event presence is not per-packet latency proof.",
        **host["metadata"],
        os_receive_events=len(times), os_send_events=len(sends), app_receive_events=len(rx),
        app_gaps_over_30ms=len(rows), categories=dict(Counter(r["category"] for r in rows)),
        largest_app_gaps=sorted(rows, key=lambda r: r["app_gap_ms"], reverse=True)[:20],
        os_send_interval_ms=stats((b - a) * 1000 for a, b in zip(sends, sends[1:]) if low <= a < b <= high))
    Path(output_path).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 6:
        run(*sys.argv[1:])
    else:
        raise SystemExit("Usage: analyze_windows_network.py PC_JSON UDP_JSON ETL_ANCHOR_JSON ETW_SUMMARY OUTPUT_JSON")
