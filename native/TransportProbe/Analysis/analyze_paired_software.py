"""Pair host-local durations for the SAME audio frame; exclude intervening transport.
Usage: python analyze_paired_software.py A.json B.json OUTPUT.json
"""
from pathlib import Path
import json,sys
from analyze_steady_pipeline import analyze,distribution,unique


def combine(sender,receiver):
    source=unique(sender,lambda p:(p['ssrc'],p['timestamp']))
    sink=unique(receiver,lambda p:(p['ssrc'],p['timestamp']))
    rows=[]
    for key,tx in source.items():
        rx=sink.get(key)
        if rx is None:continue
        total=tx['firstSampleAgeMs']+rx['receiveToSupplyMs']
        rows.append({'samples':tx['samples'],'sumMs':total})
    samples=sum(r['samples'] for r in rows)
    return {'matchedFrames':len(rows),'sourceFrames':len(source),'receiverFrames':len(sink),'matchedAudioMs':samples/48,
            'localStageSum':distribution(r['sumMs'] for r in rows),
            'sampleWeightedMeanMs':sum(r['sumMs']*r['samples'] for r in rows)/samples if samples else None}


def summarize(a,b):
    if a['run']!=b['run'] or a['role']==b['role']:raise ValueError('Mismatched pair')
    if a['buildHash']!=b['buildHash'] or a['probeHash']!=b['probeHash']:raise ValueError('Mismatched builds')
    for key in ['fixtureHash','route','shared','reserveMs','observeSeconds']:
        if key not in a or key not in b or a[key]!=b[key]:raise ValueError('Mismatched trial configuration: '+key)
    x,y=analyze(a,True),analyze(b,True)
    return {'run':a['run'],'directions':{
        a['role']+'To'+b['role']:combine(x['frameDetails']['tx'],y['frameDetails']['rx']),
        b['role']+'To'+a['role']:combine(y['frameDetails']['tx'],x['frameDetails']['rx'])},
        'hosts':{a['role']:{k:v for k,v in x.items() if k!='frameDetails'},b['role']:{k:v for k,v in y.items() if k!='frameDetails'}},
        'limits':'Same-frame sum of sender device-reported first-sample-to-send-API and receiver authenticated-callback-to-first-PCM-supply durations. No cross-host absolute subtraction. Excludes transport/SDK between these boundaries and post-supply device playback; not mouth-to-ear latency. Frame-first-sample timing has render-pull quantization; not an exact per-sample acoustic distribution. Sample weighting avoids counting short frames twice; filtering/survivor bias and unmatched coverage remain explicit.'}


if __name__=='__main__':
    a,b=[json.loads(Path(p).read_text(encoding='utf-8-sig')) for p in sys.argv[1:3]]
    Path(sys.argv[3]).write_text(json.dumps(summarize(a,b),ensure_ascii=False,indent=2),encoding='utf-8')
