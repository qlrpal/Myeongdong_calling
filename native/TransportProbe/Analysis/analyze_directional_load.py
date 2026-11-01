"""Summarize bounded upload/download probes using sender-local monotonic time.

Usage: python analyze_directional_load.py RAW.json OUTPUT.json
RTT is application request/response time, not one-way or mouth-to-ear latency.
"""
from pathlib import Path
import json,sys


def validate_identity(raw):
    seen=set()
    for r in raw['records']:
        if 'sequence' not in r:raise ValueError('Probe sequence missing')
        key=(r['lane'],r['sequence'])
        if key in seen:raise ValueError('Reused probe identity; invalid trial')
        seen.add(key)


def summarize(raw,trim=3):
    validate_identity(raw)
    output=[]
    for phase in raw['phases']:
        low,high=phase['start']+trim,phase['end']-trim
        if high<=low:raise ValueError('Phase too short after trimming')
        lanes={}
        for name in sorted({r['lane'] for r in raw['records']}):
            selected=[r for r in raw['records'] if r['lane']==name and low<=r['at']<high]
            values=sorted(r['rttMs'] for r in selected if 'rttMs' in r)
            sends=sorted(r['at'] for r in selected)
            spacing=sorted((b-a)*1000 for a,b in zip(sends,sends[1:]))
            errors={e:sum(r.get('error')==e for r in selected) for e in sorted({r['error'] for r in selected if 'error' in r})}
            lanes[name]={'responses':len(values),'errors':errors}
            lanes[name]['observedSendsPerSecond']=len(selected)/(high-low)
            if spacing:lanes[name].update(sendSpacingP95Ms=spacing[min(len(spacing)-1,int(.95*len(spacing)))],sendSpacingMaxMs=spacing[-1])
            if values:
                lanes[name].update(medianMs=values[len(values)//2],p95Ms=values[min(len(values)-1,int(len(values)*.95))],
                                   maxMs=values[-1],over30Ms=sum(v>30 for v in values),over50Ms=sum(v>50 for v in values))
        item={'name':phase['name'],'analyzedSeconds':high-low,'lanes':lanes}
        if phase['name'] in ['upload','download']:
            item['load']={k:phase.get(k) for k in ['requestedBytesPerSecond','bytes','remoteReceivedBytes','transferSeconds','exit']}
            if phase.get('bytes') is not None and phase.get('transferSeconds',0)>0:
                item['load']['observedMbps']=phase['bytes']*8/phase['transferSeconds']/1e6
        output.append(item)
    contrasts=[]
    for i,phase in enumerate(output):
        if phase['name'] not in ['upload','download']:continue
        if i==0 or i+1==len(output):raise ValueError('Load requires flanking baselines')
        for name,lane in phase['lanes'].items():
            before,after=output[i-1]['lanes'][name],output[i+1]['lanes'][name]
            if not all('p95Ms' in r for r in [lane,before,after]):continue
            contrasts.append({'phase':phase['name'],'lane':name,'p95Ms':lane['p95Ms'],
                              'baselineP95Ms':[before['p95Ms'],after['p95Ms']],
                              'p95AboveBothBaselinesMs':lane['p95Ms']-max(before['p95Ms'],after['p95Ms'])})
    return {'label':raw['label'],'trimSecondsEachEnd':trim,'phases':output,'contrasts':contrasts,
            'limits':'Application RTT on one host monotonic clock. Self echo is process-local UDP, not LAN RTT. Errors/nonresponses are not physical loss attribution. Finite paced SSH traffic does not prove link saturation; no bufferbloat exclusion from a negative result. Sequential A/load/A is a single exploratory contrast, not independent samples or universal causation.'}


if __name__=='__main__':
    raw=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    Path(sys.argv[2]).write_text(json.dumps(summarize(raw),ensure_ascii=False,indent=2),encoding='utf-8')
