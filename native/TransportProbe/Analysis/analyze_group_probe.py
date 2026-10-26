"""Summarize one host of a two-host real-audio group probe, without cross-host clocks."""
import collections
import json
import math
import sys
from pathlib import Path


def percentile(values, q):
    values=sorted(v for v in values if v is not None)
    return values[math.ceil(len(values)*q)-1] if values else None


def analyze(report):
    if report.get("error") or report.get("failures") or not report.get("cleanupOk"):
        raise ValueError("Failed/incompletely cleaned trial")
    required={"three":3,"six":6} if report.get("quick") else {
        "three":3,"six":6,"leave-one":5,"rejoin":6,"reconnect":6,"empty":0,"soak-six":6}
    if report.get("senderMode") == "baseline":required={"pair":2}
    elif report.get("senderMode") in ("inline","dedicated"):required={"pair":2,"three":3,"six":6}
    elif report.get("senderMode") is not None:raise ValueError("Invalid sender comparison mode")
    if report.get("soakOnly"):required={"soak-six":6}
    phases={p["Name"]:len(p["members"]) for p in report["membership"]}
    if phases!=required:raise ValueError("Missing or incorrect membership phases")
    streams=collections.defaultdict(list)
    sources=collections.defaultdict(list)
    for row in report["samples"]:
        phase,slot,call=row["Phase"],row["Slot"],row["Call"]
        peers=call["Peers"]
        if not call["InCall"] or len(peers)!=required[phase]-1 or len({p["Id"] for p in peers})!=len(peers):
            raise ValueError("Stable-window membership mismatch")
        if len(peers)>1 and (call.get("FrameAdaptation") or {}).get("AllPeersReady"):
            raise ValueError("Unqualified multi-peer frame adaptation")
        sources[(phase,slot)].append(call)
        for peer in peers:
            if peer["State"]!="connected" or peer["Audio"]["DeviceError"] or peer["Audio"]["RtpPlayout"]["InvalidPackets"]:
                raise ValueError("Media/device failure")
            streams[(phase,slot,peer["Id"])].append((call["MonotonicSeconds"],peer))
    local_slots={0,1,2} if report["role"]=="pc" else {3,4,5}
    expected={(p["Name"],slot) for p in report["membership"] for slot in local_slots.intersection(p["members"])}
    if set(sources)!=expected:raise ValueError("Missing local client/phase coverage")
    rows=[]
    for (phase,slot,peer_id),points in streams.items():
        low=points[0][0]+10
        chosen=[(at,p) for at,p in points if at>=low]
        if len(chosen)<2:raise ValueError("Insufficient settled samples")
        first,last=chosen[0][1],chosen[-1][1]
        duration=chosen[-1][0]-chosen[0][0]
        if duration<=0:raise ValueError("Nonpositive window")
        def delta(path):
            def get(p):
                for key in path:p=p[key]
                return p
            value=get(last)-get(first)
            if value < -1e-6:raise ValueError("Counter reset inside stable peer epoch")
            return max(0,value)
        tx,rx=delta(["TxFrames"]),delta(["RxPackets"])
        decoded=delta(["Audio","RtpPlayout","DecodedMs"]) if "DecodedMs" in last["Audio"]["RtpPlayout"] else delta(["Audio","DecodedMs"])
        if min(tx,rx,decoded)<=0:raise ValueError("Missing bidirectional decoded media")
        plc=delta(["Audio","RtpPlayout","ConcealedMs"])
        def optional_playout(key):
            if key not in first["Audio"]["RtpPlayout"] or key not in last["Audio"]["RtpPlayout"]:return None
            return delta(["Audio","RtpPlayout",key])
        held=optional_playout("HeldMs")
        if held is not None and held>plc+1e-6:raise ValueError("PLC hold exceeds total concealment")
        rows.append(dict(phase=phase,slot=slot,peer=last["Name"],sameHost=last["Name"].startswith("group-"+report["role"]+"-"),
            seconds=duration,txFrames=tx,rxPackets=rx,
            queueMedianMs=percentile((p["Audio"]["QueueMs"] for _,p in chosen),.5),
            queueP95Ms=percentile((p["Audio"]["QueueMs"] for _,p in chosen),.95),
            plcMs=plc,plcHoldMs=held,plcAdvanceMs=max(0,plc-held) if held is not None else None,
            latePackets=optional_playout("LatePackets"),duplicatePackets=optional_playout("DuplicatePackets"),rebuffers=optional_playout("Rebuffers"),
            underfillMs=delta(["Audio","UnderfillMs"]),
            discardMs=delta(["Audio","RtpPlayout","RecoveryDiscardedMs"])))
    source_rows=[]
    for (phase,slot),calls in sources.items():
        source_rows.append(dict(phase=phase,slot=slot,
            senderMaxWaitMsSinceJoin=max((c.get("Sender") or {}).get("MaxWaitMs",0) for c in calls),
            senderDropsSinceJoin=max((c.get("Sender") or {}).get("DroppedFrames",0) for c in calls),
            encodeRollingP95MaxMs=max(c["Microphone"]["ProcessingP95Ms"] or 0 for c in calls),
            frameMs=sorted({c["FrameMs"] for c in calls})))
    if report.get("soakOnly") or not report.get("quick") and report.get("senderMode") is None:
        soak=[r for r in rows if r["phase"]=="soak-six"]
        if not soak or min(r["seconds"] for r in soak)<report.get("soakSeconds",600)-30:raise ValueError("Six-peer soak too short")
    return dict(role=report["role"],quick=report.get("quick",False),cleanupOk=True,streams=rows,sources=source_rows,
        sdkBuild=report.get("sdkBuild"),
        phaseResources=report.get("phaseResources"),resources=report["resources"],
        traceEvicted={k:v["EvictedRecords"] for k,v in report.get("traces",{}).items()},
        limits="Per-peer counters after 10s warmup, receiver-local duration only. Same-host streams bypass the LAN. "
        "Source maxima are cumulative since join, not isolated phase maxima. Probe process CPU includes all local clients and charged user/kernel time, but excludes audiodg.exe, other processes and uncharged system/driver work. "
        "PLC hold repeats without advancing RTP; advance PLC reflects a playout deadline decision, not proof of physical packet loss. "
        "Retained diagnostics affect memory; before/after private bytes are not a leak verdict. No physical mouth-to-ear claim.")


def compare_groups(left,right):
    from analyze_packet_pair import compare
    analyze(left);analyze(right)
    if {left["role"],right["role"]}!={"pc","laptop"} or left["engineBuild"]!=right["engineBuild"]:
        raise ValueError("Matching builds on distinct host roles are required")
    if left.get("sdkBuild")!=right.get("sdkBuild"):
        raise ValueError("Matching SDK identities are required; do not mix original and patched runtimes")
    traces={int(k):v for report in (left,right) for k,v in report.get("traces",{}).items()}
    if set(traces)!=set(range(6)):raise ValueError("Six participant traces required")
    rows=[]
    for sender in range(6):
        for receiver in range(6):
            if sender==receiver:continue
            pair=compare({"trace":traces[sender]},{"trace":traces[receiver]})
            if len(pair["streams"])!=1:raise ValueError("Ambiguous participant media direction")
            rows.append(dict(sender=sender,receiver=receiver,sameHost=(sender<3)==(receiver<3),**pair["streams"][0]))
    return dict(directions=rows,limits="Only exact interior boundaries of final bounded traces, not whole-call loss or physical NIC transmission. Missing receiver callbacks include transport/SDK validation boundaries before application playout.")


def self_test():
    def call(at,n):
        peers=[]
        for i in range(n-1):
            peers.append(dict(Id=str(i),Name="group-laptop-"+str(i),State="connected",TxFrames=at+1,RxPackets=at+1,
                Audio=dict(DeviceError=None,QueueMs=5,DecodedMs=at+1,UnderfillMs=0,
                    RtpPlayout=dict(InvalidPackets=0,ConcealedMs=0,HeldMs=0,RecoveryDiscardedMs=0))))
        return dict(InCall=True,MonotonicSeconds=at,Peers=peers,FrameMs=2.5,
                    Microphone=dict(ProcessingP95Ms=.2),Sender=dict(MaxWaitMs=.1,DroppedFrames=0))
    r=dict(role="pc",quick=True,error=None,failures=[],cleanupOk=True,resources={},
        membership=[dict(Name="three",members=[0,3,4]),dict(Name="six",members=list(range(6)))],
        samples=[dict(Phase=p,Slot=slot,Call=call(at,n)) for p,n in [("three",3),("six",6)] for slot in ([0] if p=="three" else [0,1,2]) for at in (0,11,12)])
    baseline={**r,"quick":False,"senderMode":"baseline",
        "membership":[dict(Name="pair",members=[0,3])],
        "samples":[dict(Phase="pair",Slot=0,Call=call(at,2)) for at in (0,11,12)]}
    assert len(analyze(baseline)["streams"])==1
    comparison={**r,"quick":False,"senderMode":"inline",
        "membership":baseline["membership"]+r["membership"],"samples":baseline["samples"]+r["samples"]}
    assert len(analyze(comparison)["streams"])==18
    boundary={**r,"quick":False,"senderMode":"dedicated","soakOnly":True,"soakSeconds":90,
        "membership":[dict(Name="soak-six",members=list(range(6)))],
        "samples":[dict(Phase="soak-six",Slot=slot,Call=call(at,6)) for slot in (0,1,2) for at in (0,11,80)]}
    assert len(analyze(boundary)["streams"])==15
    result=analyze(r)
    assert len(result["streams"])==17 and all(s["queueMedianMs"]==5 for s in result["streams"])
    assert all(s["plcHoldMs"]==0 and s["plcAdvanceMs"]==0 for s in result["streams"])
    import copy
    split=copy.deepcopy(r)
    for sample in split["samples"]:
        at=sample["Call"]["MonotonicSeconds"]
        for peer in sample["Call"]["Peers"]:
            peer["Audio"]["RtpPlayout"].update(ConcealedMs=at*3,HeldMs=at)
    assert all(s["plcHoldMs"]==1 and s["plcAdvanceMs"]==2 for s in analyze(split)["streams"])
    legacy=copy.deepcopy(split)
    for sample in legacy["samples"]:
        for peer in sample["Call"]["Peers"]:del peer["Audio"]["RtpPlayout"]["HeldMs"]
    assert all(s["plcHoldMs"] is None and s["plcAdvanceMs"] is None for s in analyze(legacy)["streams"])
    for sample in split["samples"]:
        for peer in sample["Call"]["Peers"]:peer["Audio"]["RtpPlayout"]["HeldMs"]*=4
    try:analyze(split)
    except ValueError:pass
    else:raise AssertionError("Concealment decomposition accepted impossible hold")
    try:analyze({**r,"cleanupOk":False})
    except ValueError:pass
    else:raise AssertionError("Cleanup failure accepted")
    try:analyze({**r,"samples":r["samples"][:3]})
    except ValueError:pass
    else:raise AssertionError("Missing six-peer clients accepted")
    r["samples"][-1]["Call"]["Peers"][0]["RxPackets"]=0
    try:analyze(r)
    except ValueError:pass
    else:raise AssertionError("Counter reset accepted")
    print("PASS: group boundaries, peer media flow, cleanup rejection and peer counter epochs")


if __name__=="__main__":
    if sys.argv[1:]==["--self-test"]:self_test()
    elif len(sys.argv)==5 and sys.argv[1]=="--pairs":
        left,right,output=map(Path,sys.argv[2:])
        if output.resolve() in (left.resolve(),right.resolve()):raise ValueError("Preserve sources")
        result=compare_groups(json.loads(left.read_text(encoding="utf-8-sig")),json.loads(right.read_text(encoding="utf-8-sig")))
        output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
        print("PASS: exact trace pairing for",len(result["directions"]),"media directions")
    elif len(sys.argv)==3:
        source,output=map(Path,sys.argv[1:])
        if source.resolve()==output.resolve():raise ValueError("Preserve source")
        summary=analyze(json.loads(source.read_text(encoding="utf-8-sig")))
        output.write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding="utf-8")
        print("PASS:",summary["role"],len(summary["streams"]),"peer windows; cleanup verified")
    else:raise SystemExit("Usage: analyze_group_probe.py INPUT.json OUTPUT.json | --pairs LEFT.json RIGHT.json OUTPUT.json | --self-test")
