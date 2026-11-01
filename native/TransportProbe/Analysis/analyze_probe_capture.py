"""Join nonce-identified STUN/DNS probe RTT with a same-host scoped capture.
Compare durations only; never subtract pcap epoch from app monotonic time.
Usage: python analyze_probe_capture.py RAW.json CAPTURE.pcapng OUTPUT.json
"""
from pathlib import Path
from collections import defaultdict,Counter
from decimal import Decimal
import csv,io,json,struct,subprocess,sys
from analyze_directional_load import validate_identity


def matched_duration(outgoing,incoming):
    if len(outgoing)!=1 or len(incoming)!=1:return None
    return float((incoming[0]-outgoing[0])*1000)


def main():
    raw=json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
    validate_identity(raw)
    ports={str(x['port']):x['lane'] for x in raw['sources'] if x['lane'].startswith('wifi-')}
    nonce=bytes.fromhex(raw['nonce']);seed=raw['dnsSeed'];observed={'out':defaultdict(list),'in':defaultdict(list)};rcodes=Counter()
    fields=['frame.time_epoch','udp.srcport','udp.dstport','udp.payload']
    command=['C:/Program Files/Wireshark/tshark.exe','-n','-r',sys.argv[2],'-Y','udp','-T','fields','-E','header=y','-E','separator=,','-E','quote=d','-E','occurrence=f']
    for field in fields:command+=['-e',field]
    result=subprocess.run(command,capture_output=True,encoding='utf-8',check=True)
    for row in csv.DictReader(io.StringIO(result.stdout)):
        source,dest=row['udp.srcport'],row['udp.dstport']
        direction='out' if source in ports else 'in' if dest in ports else None
        if direction is None:continue
        lane=ports[source if direction=='out' else dest];data=bytes.fromhex(row['udp.payload'].replace(':',''))
        if lane.endswith('gateway'):
            if len(data)<12 or bool(data[2]&128)!=(direction=='in'):continue
            sequence=(struct.unpack_from('!H',data)[0]-seed)&65535
            if direction=='in':rcodes[str(data[3]&15)]+=1
        else:
            if len(data)<20 or data[8:16]!=nonce or data[:2]!=(b'\0\1' if direction=='out' else b'\1\1'):continue
            sequence=struct.unpack_from('!I',data,16)[0]
        observed[direction][(lane,sequence)].append(Decimal(row['frame.time_epoch']))
    records=[];missing=Counter();unmatched_shapes=Counter()
    for r in raw['records']:
        if not r['lane'].startswith('wifi-') or 'rttMs' not in r:continue
        key=(r['lane'],r['sequence']);duration=matched_duration(observed['out'].get(key,[]),observed['in'].get(key,[]))
        if duration is None:
            missing[r['lane']]+=1
            unmatched_shapes[str((len(observed['out'].get(key,[])),len(observed['in'].get(key,[]))))]+=1
            continue
        phase=next((p['name'] for p in raw['phases'] if p['start']+3<=r['at']<p['end']-3),None)
        records.append(dict(lane=r['lane'],sequence=r['sequence'],at=r['at'],phase=phase,appRttMs=r['rttMs'],
                            captureRttMs=duration,residualMs=r['rttMs']-duration))
    summary=[]
    for phase in raw['phases']:
        for lane in sorted(set(ports.values())):
            rows=[r for r in records if r['phase']==phase['name'] and r['lane']==lane]
            values=sorted(r['captureRttMs'] for r in rows);residual=sorted(r['residualMs'] for r in rows)
            if not values:continue
            summary.append(dict(phase=phase['name'],lane=lane,n=len(values),captureMedianMs=values[len(values)//2],
                                captureP95Ms=values[min(len(values)-1,int(.95*len(values)))],captureMaxMs=values[-1],
                                residualMinMs=residual[0],residualMaxMs=residual[-1]))
    output=dict(summary=summary,missingObservations=dict(missing),unmatchedCountsOutIn=dict(unmatched_shapes),dnsResponseCodes=dict(rcodes),
                largest=sorted([r for r in records if r['phase'] is not None],key=lambda r:r['appRttMs'],reverse=True)[:12],
                limits='Same-host durations, no epoch/QPC origin subtraction. Npcap Tx/Rx are software boundaries, not wire/RF time. Residual includes unobserved app/socket scheduling and clock/capture error. Missing/ambiguous observations are not physical packet loss. Capture counters must be checked separately.')
    Path(sys.argv[3]).write_text(json.dumps(output,indent=2),encoding='utf-8')


if __name__=='__main__':main()
