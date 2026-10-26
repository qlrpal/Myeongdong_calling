"""Locate a late RTP frame's first observed missed deadline in bounded traces.

Absolute timestamps are compared only within the receiver. Across hosts only
adjacent packet intervals are compared; their difference is delay variation,
never absolute one-way transport or physical mouth-to-ear latency.
"""
import collections
import csv
import io
import json
import sys
from contextlib import nullcontext
from decimal import Decimal
from pathlib import Path

from analyze_packet_pair import trace


def identity(item):
    return item["Ssrc"], item["Timestamp"]


def capture_intervals(receiver, capture_csv):
    """Compare same packet-pair intervals, without aligning pcap epoch to QPC.

    Input is a one-interface tshark CSV with the fields listed below. Endpoint
    ports and full clear RTP identity bind observations to this receiver.
    """
    rx = trace(receiver)
    ports = {int(p["LocalEndpoint"].rsplit(":", 1)[1])
             for sample in receiver.get("samples", []) for p in sample["Peers"]}
    if not ports:
        raise ValueError("Receiver socket endpoint history required")
    captured = collections.defaultdict(list)
    required = {"frame.time_epoch", "udp.dstport", "rtp.ssrc", "rtp.timestamp", "rtp.seq"}
    with (nullcontext(capture_csv) if hasattr(capture_csv, "read")
          else Path(capture_csv).open(encoding="utf-8-sig", newline="")) as handle:
        reader = csv.DictReader(handle)
        if not required <= set(reader.fieldnames or []):
            raise ValueError("Unsupported tshark RTP CSV fields")
        for row in reader:
            if int(row["udp.dstport"]) not in ports:
                continue
            key = int(row["rtp.ssrc"], 0), int(row["rtp.timestamp"]), int(row["rtp.seq"])
            captured[key].append(Decimal(row["frame.time_epoch"]))
    streams = collections.defaultdict(list)
    observed = collections.Counter()
    for packet in rx["Receives"]:
        streams[packet["Ssrc"]].append(packet)
        observed[(*identity(packet), packet["Sequence"])] += 1
    late = {identity(p) for p in rx.get("Admissions", []) if p["Outcome"] == "late"}
    matched = {key for key in observed if observed[key] == 1 and len(captured[key]) == 1}
    gaps = []
    for stream in streams.values():
        stream.sort(key=lambda p: p["At"])
        for a, b in zip(stream, stream[1:]):
            app_gap = (b["At"] - a["At"]) * 1000
            if app_gap <= 30:
                continue
            ka, kb = (*identity(a), a["Sequence"]), (*identity(b), b["Sequence"])
            exact = ka in matched and kb in matched
            event = dict(ssrc=b["Ssrc"], timestamp=b["Timestamp"], callbackGapMs=app_gap,
                         exactBoundaries=exact, late=identity(b) in late)
            if exact:
                cap_gap = float((captured[kb][0] - captured[ka][0]) * 1000)
                if cap_gap < 0:
                    raise ValueError("Capture order reversed for receiver callback order")
                event.update(captureGapMs=cap_gap, callbackGapGrowthAfterCaptureMs=app_gap - cap_gap)
            gaps.append(event)
    return dict(receiverFrames=len(rx["Receives"]), uniquelyMatchedFrames=len(matched),
                completeReceiverMatch=len(matched) == len(rx["Receives"]) and bool(matched),
                lateFrames=len(late), uniquelyMatchedLateFrames=sum(key[:2] in late for key in matched), gaps=gaps,
                limits="Same exact packet-pair intervals only; pcap epoch is never subtracted from app QPC. "
                       "Use a verified monotonic capture timestamp source. Intervals assume comparable clock rates. "
                       "Matching every callback is not proof that no additional packet was lost before the callback. "
                       "Capture drops/completeness and observer overhead must be checked separately. "
                       "Software capture is not physical NIC arrival or mouth-to-ear latency.")


def analyze(sender, receiver):
    tx, rx = trace(sender), trace(receiver)
    sent = {}
    for item in tx["Sends"]:
        if identity(item) in sent:
            raise ValueError("Ambiguous sender frame identity")
        sent[identity(item)] = item
    received = collections.defaultdict(list)
    streams = collections.defaultdict(list)
    for item in rx["Receives"]:
        received[identity(item)].append(item)
        streams[item["Ssrc"]].append(item)
    previous = {}
    for stream in streams.values():
        stream.sort(key=lambda p: p["At"])
        for a, b in zip(stream, stream[1:]):
            previous[id(b)] = a
    admissions = collections.defaultdict(list)
    for item in rx.get("Admissions", []):
        admissions[identity(item)].append(item)
    concealments = collections.defaultdict(list)
    for item in rx.get("Playout", []):
        if item["Outcome"] in ("plc-hold", "plc-loss"):
            concealments[identity(item)].append(item)
    events = []
    for key, attempts in admissions.items():
        if key not in sent or not any(a["Outcome"] == "late" for a in attempts):
            continue
        late = min((a for a in attempts if a["Outcome"] == "late"), key=lambda a: a["CalledAt"])
        candidates = received[key]
        earlier_plc = [p for p in concealments[key] if p["At"] <= late["LockAt"]]
        losses = [p for p in earlier_plc if p["Outcome"] == "plc-loss"]
        event = dict(ssrc=key[0], timestamp=key[1],
                     classification="unresolved-history", lateQueueMs=late["QueueMs"],
                     admissionLockWaitMs=(late["LockAt"] - late["CalledAt"]) * 1000,
                     matchingHoldCount=sum(p["Outcome"] == "plc-hold" for p in earlier_plc),
                     matchingLossCount=len(losses))
        if any(a["Outcome"] == "queued" for a in attempts) or len(candidates) != 1:
            event["classification"] = "duplicate-or-ambiguous-receive"
            events.append(event)
            continue
        current = candidates[0]
        if earlier_plc:
            # Hold already replaces audible audio. Advancing the RTP timestamp
            # is a later decision and must not hide that first missed demand.
            missed = min(p["At"] for p in earlier_plc)
            event["firstConcealToReceiveMs"] = (current["At"] - missed) * 1000
            if losses:
                event["firstLossToReceiveMs"] = (current["At"] - min(p["At"] for p in losses)) * 1000
            if current["At"] > missed:
                event["classification"] = "missed-before-rtp-callback"
            elif late["LockAt"] > missed:
                event["classification"] = "callback-observed-before-admission-lock"
            else:
                event["classification"] = "requires-buffer-state-history"
            reads = sorted((r for r in rx.get("Reads", []) if r["Peer"] == late["Peer"]
                            and missed - .06 <= r["CalledAt"] <= late["ReturnedAt"]), key=lambda r: r["CalledAt"])
            if reads:
                event["receiverReadMaxWorkMs"] = max((r["ReturnedAt"] - r["CalledAt"]) * 1000 for r in reads)
                event["receiverReadMaxLockMs"] = max((r["LockAt"] - r["CalledAt"]) * 1000 for r in reads)
                event["receiverReadMaxGapMs"] = max(
                    ((b["CalledAt"] - a["CalledAt"]) * 1000 for a, b in zip(reads, reads[1:])), default=None)
        before = previous.get(id(current))
        if before is not None and identity(before) in sent:
            a, b = sent[identity(before)], sent[key]
            step = b["Sequence"] - a["Sequence"]
            event["sourceFrameStep"] = step
            if step > 0:
                send_interval = (b["SendStart"] - a["SendStart"]) * 1000
                receive_interval = (current["At"] - before["At"]) * 1000
                event.update(senderApiStartIntervalMs=send_interval,
                             receiverCallbackIntervalMs=receive_interval,
                             apiToCallbackIntervalGrowthMs=receive_interval - send_interval,
                             senderApiWorkMs=(b["SendEnd"] - b["SendStart"]) * 1000)
                if earlier_plc:
                    event["previousReceiveToFirstConcealMs"] = (min(p["At"] for p in earlier_plc) - before["At"]) * 1000
                if b.get("Capture"):
                    event["senderCallbackToApiMs"] = (b["SendStart"] - b["Capture"]["CallbackAt"]) * 1000
            else:
                event["arrivalOrder"] = "reordered-or-duplicate"
        events.append(event)
    return dict(events=events, counts=dict(collections.Counter(e["classification"] for e in events)),
                limits="Only matched late frames in retained trace history. Callback is after SDK RTP processing, not OS/NIC receive. "
                       "Interval growth compares separate host-local durations and assumes comparable monotonic clock rates; "
                       "it is not absolute one-way latency and does not separate socket/OS/network/SRTP scheduling. "
                       "Missing history, duplicate receives and trace eviction must not establish a cause. "
                       "This tool does not analyze every output-underfill or rebuffer episode.")


def self_test():
    def fixtures(sender_offset=0, receiver_offset=0):
        tx = dict(Sends=[dict(Ssrc=1, Timestamp=n * 120, Sequence=n,
                             SendStart=sender_offset + n * .01, SendEnd=sender_offset + n * .01 + .0001)
                         for n in range(2)], Receives=[])
        rx = dict(Sends=[], Receives=[dict(Ssrc=1, Timestamp=n * 120, At=receiver_offset + at)
                                     for n, at in enumerate((.02, .06))],
                  Admissions=[dict(Ssrc=1, Timestamp=120, Peer="p", CalledAt=receiver_offset + .06,
                                   LockAt=receiver_offset + .0601, ReturnedAt=receiver_offset + .0602,
                                   Outcome="late", QueueMs=0)],
                  Playout=[dict(Ssrc=1, Timestamp=120, At=receiver_offset + .05, Outcome="plc-loss")], Reads=[])
        return tx, rx
    a = analyze(*fixtures())["events"][0]
    assert a["classification"] == "missed-before-rtp-callback"
    assert abs(a["apiToCallbackIntervalGrowthMs"] - 30) < 1e-6
    b = analyze(*fixtures(10000, -20000))["events"][0]
    for key in ("apiToCallbackIntervalGrowthMs", "firstConcealToReceiveMs", "firstLossToReceiveMs"):
        assert abs(a[key] - b[key]) < 1e-6, "Cross-host absolute offset affected classification"
    tx, rx = fixtures()
    rx["Receives"][1]["At"] = .04
    assert analyze(tx, rx)["events"][0]["classification"] == "callback-observed-before-admission-lock"
    rx["Playout"].append(dict(Ssrc=1, Timestamp=120, At=.035, Outcome="plc-hold"))
    assert analyze(tx, rx)["events"][0]["classification"] == "missed-before-rtp-callback"
    tx, rx = fixtures()
    rx["Playout"] = []
    assert analyze(tx, rx)["events"][0]["classification"] == "unresolved-history"
    tx, rx = fixtures()
    rx["Receives"].append(dict(rx["Receives"][1]))
    assert analyze(tx, rx)["events"][0]["classification"] == "duplicate-or-ambiguous-receive"
    tx, rx = fixtures()
    tx["Sends"].append(dict(Ssrc=1, Timestamp=240, Sequence=2, SendStart=.02, SendEnd=.0201))
    rx["Receives"][0]["Timestamp"] = 240
    reordered = analyze(tx, rx)["events"][0]
    assert reordered["arrivalOrder"] == "reordered-or-duplicate"
    assert "apiToCallbackIntervalGrowthMs" not in reordered
    tx, rx = fixtures()
    rx["EvictedRecords"] = 1
    try:
        analyze(tx, rx)
    except ValueError:
        pass
    else:
        raise AssertionError("Evicted history accepted")
    columns = ["frame.time_epoch", "udp.dstport", "rtp.ssrc", "rtp.timestamp", "rtp.seq"]
    def capture(offset=0, duplicate=False, missing=False):
        handle = io.StringIO()
        writer = csv.writer(handle); writer.writerow(columns)
        writer.writerow([Decimal("1800000000.02") + offset, 50001, "0x1", 0, 0])
        if not missing:
            writer.writerow([Decimal("1800000000.06") + offset, 50001, "0x1", 120, 1])
        if duplicate:
            writer.writerow([Decimal("1800000000.061") + offset, 50001, "0x1", 120, 1])
        handle.seek(0)
        return handle
    _, r = fixtures(0, 10000)
    for n, packet in enumerate(r["Receives"]):
        packet["Sequence"] = n
    r["samples"] = [dict(Peers=[dict(LocalEndpoint="[::]:50001")])]
    a = capture_intervals(r, capture())
    assert a["completeReceiverMatch"] and a["uniquelyMatchedLateFrames"] == 1
    assert abs(a["gaps"][0]["callbackGapGrowthAfterCaptureMs"]) < 1e-6
    assert capture_intervals(r, capture(offset=Decimal("123456789")))["gaps"] == a["gaps"], "Capture epoch affected intervals"
    assert not capture_intervals(r, capture(duplicate=True))["gaps"][0]["exactBoundaries"]
    assert not capture_intervals(r, capture(missing=True))["completeReceiverMatch"]
    print("PASS: deadline boundaries, independent clock offsets, missing/duplicate/evicted history")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 5 and sys.argv[1] == "--capture-intervals":
        source, capture_csv, output = map(Path, sys.argv[2:])
        if output.resolve() in (source.resolve(), capture_csv.resolve()):
            raise ValueError("Preserve source traces")
        result = capture_intervals(json.loads(source.read_text(encoding="utf-8-sig")), capture_csv)
        output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print("MATCHED", result["uniquelyMatchedFrames"], "/", result["receiverFrames"], "receiver frames")
    elif len(sys.argv) == 4:
        left, right, output = map(Path, sys.argv[1:])
        if output.resolve() in (left.resolve(), right.resolve()):
            raise ValueError("Preserve source traces")
        result = analyze(*(json.loads(p.read_text(encoding="utf-8-sig")) for p in (left, right)))
        output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print("CLASSIFIED", len(result["events"]), "matched late frames:", result["counts"])
    else:
        raise SystemExit("Usage: analyze_deadline_event.py SEND.json RECEIVE.json OUTPUT.json | "
                         "--capture-intervals RECEIVE.json PCAP.csv OUTPUT.json | --self-test")
