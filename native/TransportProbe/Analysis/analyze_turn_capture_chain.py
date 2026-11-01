"""Same-RTP-pair interval chain: two Npcap captures + OCI TURN capture + SDK.

All absolute subtraction stays within one clock. Packet interval differences
across clocks are NOT one-way delay or physical NIC/RF timestamps.
Usage: python analyze_turn_capture_chain.py A.json B.json A.pcapng B.pcapng OCI.pcap OUTPUT.json
"""
from pathlib import Path
from collections import defaultdict, Counter
from decimal import Decimal
import csv, io, json, struct, subprocess, sys
from turn_payload import rtp_payload
from analyze_sdk_boundaries import analyze


def identity(record):
    return record['Ssrc'],record.get('Timestamp',record.get('RtpTimestamp')),record['Sequence']


def sdk_index(report, stage):
    result=defaultdict(list)
    for r in report['sdkBoundary']['data']['Records']:
        if r['Stage']==stage:result[identity(r)].append(Decimal(str(r['At'])))
    return result


def load_capture(path, local_ip=None):
    fields=['frame.number','frame.time_epoch','ip.src','ip.dst','udp.srcport','udp.dstport','udp.payload']
    command=['C:/Program Files/Wireshark/tshark.exe','-n','-r',str(path),'-Y','udp','-T','fields','-E','header=y','-E','separator=,','-E','quote=d','-E','occurrence=f']
    for field in fields:command+=['-e',field]
    process=subprocess.run(command,capture_output=True,encoding='utf-8',check=True)
    result={'in':defaultdict(list),'out':defaultdict(list)};ignored=Counter()
    for row in csv.DictReader(io.StringIO(process.stdout)):
        try:
            payload=rtp_payload(bytes.fromhex(row['udp.payload'].replace(':','')))
            if payload is None or len(payload)<12 or payload[0]>>6!=2 or payload[1]&127!=111:
                ignored['nonOpusOrIncomplete']+=1;continue
            if len(payload)<12+4*(payload[0]&15):ignored['incompleteHeader']+=1;continue
            sequence,timestamp,ssrc=struct.unpack_from('!HII',payload,2)
            if local_ip:
                direction='out' if row['ip.src']==local_ip else 'in' if row['ip.dst']==local_ip else None
            else:
                direction='in' if row['udp.dstport']=='3478' else 'out' if row['udp.srcport']=='3478' else None
            if direction is None:ignored['directionUnknown']+=1;continue
            result[direction][(ssrc,timestamp,sequence)].append(Decimal(row['frame.time_epoch']))
        except (ValueError,struct.error):ignored['parseRejected']+=1
    metadata={'uniqueIn':len(result['in']),'uniqueOut':len(result['out']),
              'ambiguousIn':sum(len(v)!=1 for v in result['in'].values()),
              'ambiguousOut':sum(len(v)!=1 for v in result['out'].values()),'ignored':dict(ignored)}
    return result,metadata


def gap(index, previous, current):
    a,b=index.get(previous,[]),index.get(current,[])
    return float(1000*(b[0]-a[0])) if len(a)==len(b)==1 else None


def local_dwell(incoming,outgoing,key):
    a,b=incoming.get(key,[]),outgoing.get(key,[])
    return float(1000*(b[0]-a[0])) if len(a)==len(b)==1 else None


def dwell_summary(server):
    values=sorted(v for key in server['in'] if (v:=local_dwell(server['in'],server['out'],key)) is not None)
    if not values:return {'n':0}
    return {'n':len(values),'negative':sum(v<0 for v in values),'minMs':values[0],
            'medianMs':values[len(values)//2],'p95Ms':values[min(len(values)-1,int(.95*len(values)))],'maxMs':values[-1]}


def direction(sender,receiver,tx,rx,server):
    base=analyze(receiver,sender);groups=defaultdict(list)
    for r in receiver['baselineIncident']['Records']:
        if r['Stage']=='authenticated-rtp-callback':groups[r['Ssrc']].append(r)
    stages=[('socketPost',sdk_index(sender,'socket-send-post')),('senderNpcap',tx['out']),
            ('serverIngress',server['in']),('serverEgress',server['out']),('receiverNpcap',rx['in']),
            ('udpReturn',sdk_index(receiver,'udp-receive-end'))]
    app_index=defaultdict(list)
    for group in groups.values():
        for r in group:app_index[identity(r)].append(Decimal(str(r['At'])))
    def event(previous,current):
        intervals={name:gap(index,previous,current) for name,index in stages}
        intervals['appCallback']=gap(app_index,previous,current)
        growth={}
        for left,right in zip(intervals,list(intervals)[1:]):
            x,y=intervals[left],intervals[right]
            growth[left+'To'+right[0].upper()+right[1:]]=None if x is None or y is None else y-x
        return {'ssrc':current[0],'previousTimestamp':previous[1],'timestamp':current[1],
                'previousSequence':previous[2],'sequence':current[2],'sequenceStep':(current[2]-previous[2])&65535,
                'intervalsMs':intervals,'intervalGrowthMs':growth,
                'serverPreviousDwellMs':local_dwell(server['in'],server['out'],previous),
                'serverCurrentDwellMs':local_dwell(server['in'],server['out'],current),
                'allBoundariesUnique':all(v is not None for v in intervals.values()),
                'captureBoundariesUnique':all(intervals[k] is not None for k in ['senderNpcap','serverIngress','serverEgress','receiverNpcap'])}
    events=[]
    for group in groups.values():
        group.sort(key=lambda r:r['At'])
        for a,b in zip(group,group[1:]):
            if (b['At']-a['At'])*1000<=50:continue
            events.append(event(identity(a),identity(b)))
    capture_groups=defaultdict(list)
    for key,values in rx['in'].items():
        if len(values)==1:capture_groups[key[0]].append((values[0],key))
    capture_events=[];max_gap=0
    for group in capture_groups.values():
        group.sort()
        for (at,previous),(bt,current) in zip(group,group[1:]):
            interval=float(1000*(bt-at));max_gap=max(max_gap,interval)
            if interval>50:capture_events.append(event(previous,current))
    return {'senderRole':sender['role'],'receiverRole':receiver['role'],'sdkAnalysis':base,
            'events':sorted(events,key=lambda e:e['intervalsMs']['appCallback'],reverse=True),
            'captureEvents':sorted(capture_events,key=lambda e:e['intervalsMs']['receiverNpcap'],reverse=True),
            'maxReceiverCaptureGapMs':max_gap}


def main():
    a,b=[json.loads(Path(p).read_text(encoding='utf-8-sig')) for p in sys.argv[1:3]]
    if a['run']!=b['run'] or a['role']==b['role'] or a['route']!='relay' or b['route']!='relay':raise ValueError('Expected matched TURN pair')
    if not a['sdkBoundary']['enabled'] or not b['sdkBoundary']['enabled']:raise ValueError('SDK diagnostics missing')
    ca,ma=load_capture(sys.argv[3],a['mediaAddress']);cb,mb=load_capture(sys.argv[4],b['mediaAddress']);server,ms=load_capture(sys.argv[5])
    result={'run':a['run'],'captureMetadata':{a['role']:ma,b['role']:mb,'server':ms},
            'serverDwell':dwell_summary(server),
            'directions':[direction(a,b,ca,cb,server),direction(b,a,cb,ca,server)],
            'limits':'Same-packet interval differences, not one-way or mouth-to-ear latency. Npcap/ens3 are software capture boundaries, not physical departure/arrival. Server ingress-to-egress dwell uses one server clock and includes all processing between those observations. SDK callback scheduling is included. Capture drops, truncation, recording contention and partial windows must be checked separately. No cross-host absolute subtraction.'}
    Path(sys.argv[6]).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__=='__main__':main()
