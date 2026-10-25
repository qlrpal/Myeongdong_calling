"""Same-host deadline classification plus offset-independent packet interval variation.
No absolute clocks from separate hosts are subtracted. No audio payload is needed.
"""
import collections
import json
import math
import sys
from pathlib import Path
from analyze_packet_pair import trace, compare


def stats(values):
    values = sorted(values)
    if not values:
        return {"count": 0, "p50Ms": None, "p95Ms": None, "maxMs": None}
    return {"count": len(values), "p50Ms": values[(len(values)-1)//2],
            "p95Ms": values[math.ceil(len(values)*.95)-1], "maxMs": values[-1]}


def analyze(sender, receiver):
    if sender.get("error") or receiver.get("error"):
        raise ValueError("Failed trials cannot support deadline analysis")
    matched = compare(sender, receiver)  # Also checks direction, overlap and eviction.
    tx, rx = trace(sender), trace(receiver)
    peer_ids = {r["Peer"] for r in rx.get("Admissions", [])}
    if len(peer_ids) != 1 or not rx.get("Reads"):
        raise ValueError("One peer with admissions and render reads is required")
    peer = next(iter(peer_ids))
    reads = sorted((r for r in rx["Reads"] if r["Peer"] == peer), key=lambda r:r["LockAt"])
    played = collections.defaultdict(list)
    for item in rx.get("Playout", []):
        if item["Peer"] == peer:
            played[(item["Ssrc"],item["Timestamp"])].append(item)
    for items in played.values():
        items.sort(key=lambda r:r["At"])
    incoming_keys = {(r["Ssrc"], r["Timestamp"]) for r in rx["Receives"]}
    interior_keys = set()
    for stream in matched["streams"]:
        ordered = sorted((s for s in tx["Sends"] if s["Ssrc"] == stream["ssrc"]), key=lambda s:s["Sequence"])
        indexes = [i for i,s in enumerate(ordered) if (s["Ssrc"],s["Timestamp"]) in incoming_keys]
        interior_keys.update((s["Ssrc"],s["Timestamp"]) for s in ordered[indexes[0]:indexes[-1]+1])
    late_context = collections.Counter()
    for item in rx["Admissions"]:
        if item["Outcome"] != "late" or (item["Ssrc"],item["Timestamp"]) not in interior_keys:
            continue
        history = played.get((item["Ssrc"], item["Timestamp"]), [])
        previous = [p for p in history if p["At"] <= item["LockAt"]]
        late_context[previous[-1]["Outcome"] if previous else "no_prior_record"] += 1
    read_gaps = [(b["LockAt"]-a["LockAt"])*1000-a["RequestedSamples"]/48
                 for a,b in zip(reads,reads[1:])]
    read_work = [(r["ReturnedAt"]-r["LockAt"])*1000 for r in reads]
    lock_wait = [(r["LockAt"]-r["CalledAt"])*1000 for r in reads]
    incoming = collections.defaultdict(list)
    for r in rx["Receives"]:
        incoming[(r["Ssrc"],r["Timestamp"])].append(r)
    interval_variation = []
    backwards = 0
    for stream in matched["streams"]:
        ssrc = stream["ssrc"]
        ordered = sorted((s for s in tx["Sends"] if s["Ssrc"]==ssrc
                          and (ssrc,s["Timestamp"]) in incoming), key=lambda s:s["Sequence"])
        for a,b in zip(ordered,ordered[1:]):
            ar = min(incoming[(ssrc,a["Timestamp"])], key=lambda r:r["At"])
            br = min(incoming[(ssrc,b["Timestamp"])], key=lambda r:r["At"])
            tx_step = b["SendStart"]-a["SendStart"]
            rx_step = br["At"]-ar["At"]
            if tx_step < 0 or rx_step < 0:
                backwards += 1
                continue
            interval_variation.append((rx_step-tx_step)*1000)
    callback_work = [(r["BufferReturnAt"]-r["At"])*1000 for r in rx["Receives"]]
    send_work = [(s["SendEnd"]-s["SendStart"])*1000 for s in tx["Sends"] if "SendEnd" in s]
    dispatch_wait = [(s["SendStart"]-s["QueuedAt"])*1000 for s in tx["Sends"] if "QueuedAt" in s]
    measurements = read_gaps+read_work+lock_wait+callback_work+interval_variation+send_work+dispatch_wait
    if not all(math.isfinite(v) for v in measurements):
        raise ValueError("Nonfinite timing")
    return {"packetMatch": matched, "latePriorPlayoutOutcome": dict(late_context),
            "renderGapExcessOverPriorAudioDuration": stats(read_gaps),
            "senderApiWork": stats(send_work), "senderDispatchWait": stats(dispatch_wait),
            "renderWork": stats(read_work), "renderLockWait": stats(lock_wait),
            "receiveCallbackThroughBufferReturn": stats(callback_work),
            "receiveMinusSendIntervalVariation": stats(interval_variation),
            "reorderedOrBackwardsMatchedIntervals": backwards,
            "limits": "Late contexts use exact overlapping sender boundaries; other timings use the bounded receiver trace. Only recorded intervals; late without prior record is unresolved, not proved loss. "
                      "Prior PLC does not prove why its deadline was missed. Read gap reference is prior PCM duration, "
                      "not measured hardware period. Interval variation combines sender OS, transit and receiver OS; "
                      "unsynchronised clock-rate error remains. No physical NIC or mouth-to-ear latency claim."}


def self_test():
    def packet(n):
        return {"Ssrc":7,"Timestamp":120*n,"Sequence":n,"FrameSamples":120,"SendStart":100+n*.01}
    tx={"Sends":[packet(n) for n in range(3)],"Receives":[],"EvictedRecords":0}
    receives=[dict(packet(n), Peer="p", At=800+n*.01+(0.012 if n==2 else 0),
                   BufferReturnAt=800+n*.01+(0.012 if n==2 else 0)+.0001) for n in range(3)]
    rx={"Sends":[],"Receives":receives,"EvictedRecords":0,
        "Admissions":[dict(packet(n),Peer="p",LockAt=receives[n]["At"],Outcome="late" if n==2 else "queued") for n in range(3)],
        "Playout":[dict(packet(2),Peer="p",At=800.025,Outcome="plc-loss")],
        "Reads":[dict(Peer="p",LockAt=800+n*.01,CalledAt=800+n*.01-.0001,
                      ReturnedAt=800+n*.01+.0002,RequestedSamples=480) for n in range(4)]}
    a=analyze(tx,rx)
    assert a["latePriorPlayoutOutcome"]=={"plc-loss":1}
    assert abs(a["receiveMinusSendIntervalVariation"]["maxMs"]-12)<1e-6
    assert abs(a["renderGapExcessOverPriorAudioDuration"]["maxMs"])<1e-6
    for p in tx["Sends"]:p["SendStart"]+=100000
    b=analyze(tx,rx)
    assert abs(a["receiveMinusSendIntervalVariation"]["maxMs"]-b["receiveMinusSendIntervalVariation"]["maxMs"])<1e-5
    rx["Playout"][0]["Outcome"]="played"
    assert analyze(tx,rx)["latePriorPlayoutOutcome"]=={"played":1}
    rx["Playout"].clear()
    assert analyze(tx,rx)["latePriorPlayoutOutcome"]=={"no_prior_record":1}
    try:analyze({**tx,"error":"failed"},rx)
    except ValueError:pass
    else:raise AssertionError("Failed trial accepted")
    rx["EvictedRecords"]=1
    try:analyze(tx,rx)
    except ValueError:pass
    else:raise AssertionError("Eviction accepted")
    print("PASS: deadline identity, 12ms interval variation, independent clock offsets and eviction rejection")


if __name__=="__main__":
    if sys.argv[1:]==["--self-test"]:self_test()
    elif len(sys.argv)==4:
        source,target,output=map(Path,sys.argv[1:])
        if output.resolve() in (source.resolve(),target.resolve()):raise ValueError("Output must preserve inputs")
        result=analyze(json.loads(source.read_text(encoding="utf-8-sig")),json.loads(target.read_text(encoding="utf-8-sig")))
        output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
    else:raise SystemExit("Usage: analyze_packet_deadline_pair.py SEND.json RECEIVE.json OUT.json | --self-test")
