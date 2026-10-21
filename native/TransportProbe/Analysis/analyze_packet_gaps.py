"""Match RTP identities across hosts and partition receive-spacing expansions.

Uses differences of intervals, never subtracts absolute clocks across hosts.
The residual combines network, OS, socket scheduling and WebRTC work before
the RTP callback; it must not be labelled one-way network latency.
"""
import argparse
import bisect
import json
import math
from pathlib import Path


def load(path):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if data.get("error") or not data.get("trace") or data["trace"]["EvictedRecords"]:
        raise ValueError(f"Incomplete trace: {path}")
    if not data["samples"] or any(s["FrameMs"] != 10 for s in data["samples"]):
        raise ValueError("Explicit 10ms codec trace required")
    if any(len(s["Peers"]) != 1 for s in data["samples"]):
        raise ValueError("Controlled one-peer trace required")
    return data


def stats(values):
    ordered = sorted(values)
    if not ordered:
        return {"count": 0}
    return {"count": len(ordered), **{name: ordered[math.ceil(len(ordered)*q)-1]
            for name, q in [("p50", .5), ("p95", .95), ("max", 1)]}}


def direction(sender, receiver, threshold_ms=40):
    sends = sender["trace"]["Sends"]
    receives = sorted(receiver["trace"]["Receives"], key=lambda p: p["At"])
    if not sends or len(receives) < 2:
        raise ValueError("Empty send/receive trace")
    identity = lambda p: (p["Ssrc"], p["Timestamp"])
    sent = {}
    for packet in sends:
        key = identity(packet)
        if key in sent:
            raise ValueError("Ambiguous sender RTP identity")
        sent[key] = packet
    reads = sorted(receiver["trace"]["Reads"], key=lambda p: p["CalledAt"])
    read_times = [p["CalledAt"] for p in reads]
    gaps, residuals = [], []
    unmatched = sum(identity(p) not in sent for p in receives)
    skipped_order = 0
    low, high = receives[0]["At"] + 5, receives[-1]["At"] - 1
    for before, after in zip(receives, receives[1:]):
        if not low < after["At"] < high:
            continue
        first, last = sent.get(identity(before)), sent.get(identity(after))
        if first is None or last is None:
            continue
        source_samples = (after["Timestamp"] - before["Timestamp"]) & 0xffffffff
        if before["Ssrc"] != after["Ssrc"] or not 0 < source_samples <= 48000*3 or last["SendEnd"] < first["SendEnd"]:
            skipped_order += 1
            continue
        receive_ms = (after["At"]-before["At"])*1000
        send_ms = (last["SendEnd"]-first["SendEnd"])*1000
        residual = receive_ms-send_ms
        residuals.append(residual)
        if receive_ms < threshold_ms:
            continue
        begin = max(1, bisect.bisect_left(read_times, before["At"]) - 1)
        end = min(len(reads), bisect.bisect_right(read_times, after["At"]) + 1)
        nearby = reads[max(0, begin-1):end]
        origin_first, origin_last = first.get("Capture"), last.get("Capture")
        gaps.append({"receiverAt": after["At"], "elapsedSeconds": after["At"]-receives[0]["At"],
                     "ssrc": after["Ssrc"], "rtpTimestamp": after["Timestamp"],
                     "sourceSpanMs": source_samples/48, "receiveSpacingMs": receive_ms,
                     "sendSpacingMs": send_ms, "postSendSpacingExpansionMs": residual,
                     "captureFrameSpacingMs": (origin_last["FrameReadyAt"]-origin_first["FrameReadyAt"])*1000 if origin_first and origin_last else None,
                     "senderQueueWaitMs": (last["DequeuedAt"]-last["QueuedAt"])*1000,
                     "senderApiMs": (last["SendEnd"]-last["SendStart"])*1000,
                     "receiveHandlerMs": (after["BufferReturnAt"]-after["At"])*1000,
                     "nearbyRenderIntervalMaxMs": max(((b["CalledAt"]-a["CalledAt"])*1000 for a,b in zip(nearby, nearby[1:])), default=None),
                     "nearbyRenderWorkMaxMs": max(((r["ReturnedAt"]-r["CalledAt"])*1000 for r in nearby), default=None)})
    return {"senderRole": sender["role"], "receiverRole": receiver["role"],
            "receiverRoute": receiver["samples"][-1]["Peers"][0]["Route"],
            "unmatchedReceives": unmatched, "skippedOrderPairs": skipped_order,
            "postSendSpacingExpansionMs": stats(residuals),
            "gapCount": len(gaps), "gaps": sorted(gaps, key=lambda g: g["receiveSpacingMs"], reverse=True)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("first", type=Path)
    parser.add_argument("second", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.output.resolve() in (args.first.resolve(), args.second.resolve()):
        parser.error("Output must not overwrite input")
    first, second = load(args.first), load(args.second)
    if first["role"] == second["role"]:
        parser.error("Inputs must come from different hosts")
    result = {"directions": [direction(first, second), direction(second, first)],
              "notes": "Matched SSRC/RTP timestamps; 40ms gap threshold; first 5/last 1 seconds excluded. Interval differences do not require a shared epoch but assume sufficiently stable clock rates over each short interval. Residual includes network/OS/socket/WebRTC before callback; not one-way latency. Synthetic speech trace, no subjective quality inference."}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    for row in result["directions"]:
        print(f"{row['senderRole']}->{row['receiverRole']}: gaps={row['gapCount']}, unmatched={row['unmatchedReceives']}")


if __name__ == "__main__":
    main()
