"""Compare logged SDK RTPChannel header events with decoded RTP callbacks.
The channel observer runs after existing SDK handlers: it is not socket completion.
No payload bytes or cross-host absolute time subtraction are used.
"""
import collections
import json
import sys
from pathlib import Path
from analyze_packet_pair import trace, compare


def analyze(sender,receiver):
    if sender.get("error") or receiver.get("error"):raise ValueError("Failed trial")
    tx,rx=trace(sender),trace(receiver)
    raw=rx.get("RtpChannel")
    if raw is None:raise ValueError("RTPChannel events unavailable; do not treat legacy absence as zero")
    channel={(r["Ssrc"],r["Timestamp"]) for r in raw}
    decoded={(r["Ssrc"],r["Timestamp"]) for r in rx["Receives"]}
    raw_rx=dict(Sends=[],Receives=raw,EvictedRecords=rx.get("EvictedRecords",0))
    matched=compare(sender,raw_rx)
    rows=[]
    for stream in matched["streams"]:
        ordered=sorted((s for s in tx["Sends"] if s["Ssrc"]==stream["ssrc"]),key=lambda s:s["Sequence"])
        indexes=[i for i,s in enumerate(ordered) if (s["Ssrc"],s["Timestamp"]) in channel]
        interior=ordered[indexes[0]:indexes[-1]+1]
        keys={(s["Ssrc"],s["Timestamp"]) for s in interior}
        duplicates=collections.Counter((r["Ssrc"],r["Timestamp"]) for r in raw if (r["Ssrc"],r["Timestamp"]) in keys)
        only_channel=keys & (channel-decoded)
        only_decoded=keys & (decoded-channel)
        rows.append(dict(ssrc=stream["ssrc"],interiorSenderFrames=len(interior),
            notSeenAtChannel=len(keys-channel),channelObservedWithoutLoggedCallback=len(only_channel),
            loggedCallbackWithoutChannelObservation=len(only_decoded),
            channelDuplicateRecords=sum(n-1 for n in duplicates.values()),
            receiveBufferBytes=sorted({r["ReceiveBufferBytes"] for r in raw if r["Ssrc"]==stream["ssrc"]}),
            consistentObserverIdentities=not only_decoded,
            channelOnlyTimestamps=sorted(k[1] for k in only_channel)))
    return dict(streams=rows,limits="RTPChannel observer is appended after existing SDK handlers and may see a modified buffer. "
        "Observed channel/header with no logged callback narrows a recorded boundary, not proof of a particular SRTP failure. "
        "Absent channel events can include upstream SDK filtering, handler exceptions, socket/OS/network or observation gaps. "
        "Do not claim physical UDP loss or pre-decryption timing. Scope is exact first/last channel-matched sender boundaries.")


def group(left,right):
    from analyze_group_probe import analyze as validate
    validate(left);validate(right)
    if {left["role"],right["role"]}!={"pc","laptop"} or left["engineBuild"]!=right["engineBuild"]:
        raise ValueError("Distinct matching-build host roles required")
    traces={int(k):v for d in (left,right) for k,v in d["traces"].items()}
    if set(traces)!=set(range(6)):raise ValueError("Six traces required")
    rows=[]
    for a in range(6):
        for b in range(6):
            if a==b:continue
            v=analyze({"trace":traces[a]},{"trace":traces[b]})
            if len(v["streams"])!=1:raise ValueError("Ambiguous media direction")
            rows.append(dict(sender=a,receiver=b,sameHost=(a<3)==(b<3),**v["streams"][0]))
    return dict(directions=rows,limits=v["limits"])


def self_test():
    def p(n):return dict(Ssrc=7,Timestamp=(4294967200+120*n)%4294967296,Sequence=n,ReceiveBufferBytes=8192)
    tx=dict(Sends=[p(n) for n in range(6)],Receives=[],EvictedRecords=0)
    rx=dict(Sends=[],Receives=[p(n) for n in (1,4,5)],RtpChannel=[p(n) for n in (1,2,4,5)],EvictedRecords=0)
    a=analyze(tx,rx)["streams"][0]
    assert a["interiorSenderFrames"]==5 and a["notSeenAtChannel"]==1
    assert a["channelObservedWithoutLoggedCallback"]==1 and a["consistentObserverIdentities"]
    rx["Receives"].append(p(3))
    assert not analyze(tx,rx)["streams"][0]["consistentObserverIdentities"]
    rx["RtpChannel"]=None
    try:analyze(tx,rx)
    except ValueError:pass
    else:raise AssertionError("Legacy missing observer treated as zero")
    rx["RtpChannel"]=[p(n) for n in range(6)];rx["EvictedRecords"]=1
    try:analyze(tx,rx)
    except ValueError:pass
    else:raise AssertionError("Eviction accepted")
    print("PASS: RTP wrap, upstream/channel-only boundaries, inconsistent identities, legacy absence and eviction rejection")


if __name__=="__main__":
    if sys.argv[1:]==["--self-test"]:self_test()
    else:
        args=sys.argv[1:]; grouping=args and args[0]=="--group"
        if grouping:args=args[1:]
        if len(args)!=3:raise SystemExit("Usage: analyze_rtp_channel_boundary.py [--group] SEND.json RECEIVE.json OUTPUT.json | --self-test")
        left,right,output=map(Path,args)
        if output.resolve() in (left.resolve(),right.resolve()):raise ValueError("Preserve inputs")
        docs=[json.loads(p.read_text(encoding="utf-8-sig")) for p in (left,right)]
        result=group(*docs) if grouping else analyze(*docs)
        output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding="utf-8")
        print("PASS: boundary comparison written")
