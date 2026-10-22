"""Compare downlink delay variation on a shared server time axis, without clock-offset claims."""
import json
import ipaddress
import sys
from collections import defaultdict
from pathlib import Path
from analyze_turn_boundaries import pcap_records,udp_record
from analyze_nic_rtp import stats


def variation(receives,egress):
    matches=[(egress[(r['Ssrc'],r['Timestamp'],r['Sequence'])],r['At']) for r in receives if (r['Ssrc'],r['Timestamp'],r['Sequence']) in egress]
    matches.sort()
    if not matches:return []
    st,rt=matches[0]
    differences=[((at-rt)-(sent-st))*1000 for sent,at in matches]
    baseline=min(differences)
    return [{'serverAt':sent,'variationMs':d-baseline} for (sent,_),d in zip(matches,differences)]


def compare(streams,width=.1,threshold=20):
    if any(not x for x in streams.values()):raise ValueError('Missing matched stream')
    start=max(min(p['serverAt'] for p in s) for s in streams.values());end=min(max(p['serverAt'] for p in s) for s in streams.values())
    if end-start<10:raise ValueError('Insufficient overlap')
    bins={}
    for role,points in streams.items():
        by=defaultdict(list)
        for p in points:
            if start<=p['serverAt']<end:by[int((p['serverAt']-start)/width)].append(p['variationMs'])
        bins[role]=by
    names=list(streams);common=sorted(set(bins[names[0]])&set(bins[names[1]]));rows=[]
    for i in common:
        vals={role:max(bins[role][i]) for role in names}
        rows.append({'serverAt':start+i*width,'maxMs':vals,'bothHigh':all(v>=threshold for v in vals.values()),'anyHigh':any(v>=threshold for v in vals.values())})
    episodes=[]
    for row in rows:
        if not row['bothHigh']:continue
        if episodes and row['serverAt']-episodes[-1]['end']<=width*1.1:
            episodes[-1]['end']=row['serverAt']+width;episodes[-1]['bins']+=1
            for role in names:episodes[-1]['maxMs'][role]=max(episodes[-1]['maxMs'][role],row['maxMs'][role])
        else:episodes.append({'start':row['serverAt'],'end':row['serverAt']+width,'bins':1,'maxMs':dict(row['maxMs'])})
    return {'overlapSeconds':end-start,'binSeconds':width,'thresholdMs':threshold,'bins':len(rows),'bothHighBins':sum(r['bothHigh'] for r in rows),'anyHighBins':sum(r['anyHigh'] for r in rows),'episodes':episodes,
            'variationMs':{role:stats([p['variationMs'] for p in points if start<=p['serverAt']<=end]) for role,points in streams.items()}}


def run(pcap,public_ip,pc_path,laptop_path,output):
    public=ipaddress.IPv4Address(public_ip).packed;egress={}
    for at,frame,link in pcap_records(pcap):
        r=udp_record(frame,link)
        if r and r[2]==public:egress[r[0]]=min(at,egress.get(r[0],at))
    streams={};coverage={}
    for role,path in [('pc',pc_path),('laptop',laptop_path)]:
        report=json.loads(Path(path).read_text(encoding='utf-8-sig'))
        if report['error'] is not None:raise ValueError('Incomplete trial')
        rx=report['trace']['Receives'];streams[role]=variation(rx,egress);coverage[role]={'received':len(rx),'matched':len(streams[role])}
    out=compare(streams);out['coverage']=coverage
    out['notes']='Relative per-packet delay variation above the minimum observed within each stream, not absolute one-way latency. Host clock offsets cancel; clock-rate drift is not calibrated. Common server-time bins compare two independent RTP streams; overlapping bins are not independent failures. Software egress capture precedes physical transmission. No specific network component attribution.'
    Path(output).write_text(json.dumps(out,indent=2),encoding='utf-8');print(json.dumps(out,indent=2))


def self_test():
    rx=[{'Ssrc':1,'Timestamp':i,'Sequence':i,'At':500+i*.1+(.1 if 30<=i<35 else 0)} for i in range(200)]
    e={(1,i,i):1000+i*.1 for i in range(200)}
    a=variation(rx,e);b=variation([r|{'At':r['At']+20000} for r in rx],e)
    assert max(abs(x['variationMs']-y['variationMs']) for x,y in zip(a,b))<1e-7
    out=compare({'pc':a,'laptop':b});assert out['bothHighBins']>0 and out['episodes']
    flat=[p|{'variationMs':0} for p in a];assert compare({'pc':a,'laptop':flat})['bothHighBins']==0
    print('PASS: clock-offset invariance, shared variation detection, and unilateral control')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:run(*sys.argv[1:])
    else:raise SystemExit('PCAP PUBLIC_IPV4 PC_REPORT LAPTOP_REPORT OUTPUT | --self-test')
