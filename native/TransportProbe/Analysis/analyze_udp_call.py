"""Counterbalanced synthetic UDP/call analysis with payload-free OS event boundaries."""
import ipaddress
import json
import re
import struct
import sys
from collections import Counter
from pathlib import Path
from analyze_pipeline import direction, stats
from analyze_windows_network import compare_gap, load_host
from analyze_dual_os import classify
from analyze_traffic_control import episodes

PHASES = ('udp1','call1','call2','udp2')


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def load(root, name):
    folder = Path(root) / name
    app, udp, meta, phase = [read(folder / f) for f in ('test.json','udp.json','network.etl.json','phase.json')]
    if any(phase.get(k) != 0 for k in ('testExit','icmpExit','traceExit')) or udp['processId'] != phase['testPid']:
        raise ValueError('Failed process or mismatched PID')
    if udp.get('clockSource') != 'ETW raw QPC':
        raise ValueError('Both modes require raw QPC OS exports')
    if name.startswith('call'):
        host = load_host(folder/'test.json',folder/'udp.json',folder/'network.etl.json',folder/'summary.txt')
        host['low'] = min(r['At'] for r in app['trace']['Receives']) + 5
        host['high'] = max(r['At'] for r in app['trace']['Receives']) - 1
        host['send_intervals_ms'] = stats((b['SendEnd']-a['SendEnd'])*1000 for a,b in zip(app['trace']['Sends'],app['trace']['Sends'][1:]) if host['low'] <= a['SendEnd'] <= b['SendEnd'] <= host['high'])
        selected=[s for s in app['trace']['Sends'] if host['low']<=s['SendEnd']<=host['high']]
        capture=[s['Capture'] for s in selected if s.get('Capture')]
        callbacks=sorted(set(c['CallbackAt'] for c in capture))
        host['capture']=dict(callback_intervals_ms=stats((b-a)*1000 for a,b in zip(callbacks,callbacks[1:])),
            encode_ms=stats((c['EncodeEnd']-c['EncodeStart'])*1000 for c in capture),
            send_queue_ms=stats((s['DequeuedAt']-s['QueuedAt'])*1000 for s in selected),
            recorder_max_ms=app['trace']['MaxRecorderMs'])
    else:
        lost = re.search(r'Total Events\s+Lost\s+(\d+)',(folder/'summary.txt').read_text(encoding='utf-8-sig'))
        if app['error'] or app['overflow'] or meta['stopExitCode'] != 0 or not lost or int(lost[1]):
            raise ValueError('Incomplete or lost UDP trace')
        if udp['clockSource'] != 'ETW raw QPC' or udp['sessionName'] != meta['session'] or (folder/'network.etl').stat().st_size >= 32*1024*1024:
            raise ValueError('Mismatched/possibly wrapped OS capture')
        def port(v):
            v=int(v);return ((v&255)<<8)|(v>>8)
        def ip(v):
            return str(ipaddress.IPv4Address(struct.pack('<I',int(v))))
        tx=[];rx=[]
        for row in udp['rows']:
            f=row['fields']
            if int(f['PID']) != phase['testPid']:
                raise ValueError('Mixed PID export')
            if row['id']==42 and ip(f['daddr'])==app['peer'] and port(f['dport'])==app['peerPort'] and port(f['sport'])==app['localPort']:
                tx.append(row['qpc'])
            if row['id']==43 and ip(f['saddr'])==app['peer'] and port(f['sport'])==app['peerPort'] and port(f['dport'])==app['localPort']:
                rx.append(row['qpc'])
        tx.sort();rx.sort()
        low=max(min(s['Start'] for s in app['sends']),min(r['At'] for r in app['receives']))+5
        high=min(max(s['End'] for s in app['sends']),max(r['At'] for r in app['receives']))-1
        if high-low<100 or any(not v or v[0]>low or v[-1]<high for v in (tx,rx)):
            raise ValueError('Insufficient synthetic/OS coverage')
        intervals=[(b['Start']-a['Start'])*1000 for a,b in zip(app['sends'],app['sends'][1:]) if low<=a['Start']<=b['Start']<=high]
        host=dict(app=app,sends=tx,receives=rx,low=low,high=high,send_intervals_ms=stats(intervals),metadata=dict(clock_source='ETW raw QPC',etw_events_lost=0))
        if not 8<=host['send_intervals_ms']['p50']<=12 or host['send_intervals_ms']['count']<9000:
            raise ValueError('Synthetic pacing does not approximate 100pps')
    host['size_histogram'] = dict(Counter(int(r['fields']['size']) for r in udp['rows'] if r['id']==42))
    icmp=read(folder/'icmp.json'); controls=[]
    low=max(host['low'],max(r['samples'][0]['startedAt'] for r in icmp['rows'])+5)
    high=min(host['high'],min(r['samples'][-1]['completedAt'] for r in icmp['rows'])-1)
    if high-low<100:
        raise ValueError('Insufficient same-host ICMP overlap')
    for row in icmp['rows']:
        samples=[s for s in row['samples'] if low<=s['startedAt']<=s['completedAt']<=high]
        over=sum(s.get('kernelRttMs') is not None and s['kernelRttMs']>=20 for s in samples)
        groups=episodes(samples)
        controls.append(dict(target=row['target'],count=len(samples),failed=sum(s.get('status')!='Success' for s in samples),
            rtt_ms=stats(s.get('kernelRttMs') for s in samples),over_20ms=over,over_20ms_percent=round(over/len(samples)*100,3),
            episodes=[dict(at=round(g['start']-low,3),span_ms=round((g['end']-g['start'])*1000,3),samples=g['samples'],max_rtt_ms=g['max_rtt_ms']) for g in groups]))
    host['controls']=controls;host['control_seconds']=round(high-low,3)
    return host


def udp_direction(source,target):
    tx={r['Sequence']:r for r in source['app']['sends']}
    if len(tx)!=len(source['app']['sends']):
        raise ValueError('Duplicate synthetic send identity')
    received=sorted(target['app']['receives'],key=lambda r:r['At'])
    pairs=[(tx[r['Sequence']],r) for r in received if r['Sequence'] in tx and source['low']<=tx[r['Sequence']]['End']<=source['high'] and target['low']<=r['At']<=target['high']]
    if len(pairs)<9000:
        raise ValueError('Insufficient paired synthetic coverage')
    gaps=[];reordered=0
    for (a,ar),(b,br) in zip(pairs,pairs[1:]):
        if b['Sequence']<=a['Sequence']:
            reordered+=1;continue
        rg=(br['At']-ar['At'])*1000
        if rg>30:
            gaps.append(dict(source_send_end=b['End'],target_received_at=br['At'],send_gap_ms=(b['End']-a['End'])*1000,
                receive_gap_ms=rg,at_seconds=round(br['At']-pairs[0][1]['At'],3),sequence_step=b['Sequence']-a['Sequence']))
    ids=[r['Sequence'] for _,r in pairs]
    return dict(direction=source['app']['role']+' -> '+target['app']['role'],matched_packets=len(pairs),receive_gaps=gaps,
        duplicates=len(ids)-len(set(ids)),reordered=reordered,missing_sequences_in_paired_range=len(set(range(min(ids),max(ids)+1))-set(ids)))


def compare(source,target,call):
    paired=direction(source['app'],target['app']) if call else udp_direction(source,target)
    gaps=[]
    for g in paired['receive_gaps']:
        s,r=g['source_send_end'],g['target_received_at']
        os_send=compare_gap(source['sends'],s-g['send_gap_ms']/1000,s,0)
        os_receive=compare_gap(target['receives'],r-g['receive_gap_ms']/1000,r,0)
        gaps.append({**g,'source_os':os_send,'receiver_os':os_receive,'category':classify(os_send,os_receive)})
    return dict(direction=paired['direction'],matched=paired['matched_packets'],gap_count=len(gaps),
        categories=dict(Counter(g['category'] for g in gaps)),
        gaps=sorted(gaps,key=lambda g:g['receive_gap_ms'],reverse=True),
        synthetic_identity={k:paired[k] for k in ('duplicates','reordered','missing_sequences_in_paired_range') if k in paired})


def run(pc_root,laptop_root,output):
    for root,role in ((pc_root,'pc'),(laptop_root,'laptop')):
        meta=read(Path(root)/'run.json')
        if not meta['complete'] or meta['role']!=role or [p['name'] for p in meta['phases']]!=list(PHASES):
            raise ValueError('Incomplete/misordered run')
    phases=[]
    for name in PHASES:
        pc,lap=load(pc_root,name),load(laptop_root,name)
        if pc['app']['role']!='pc' or lap['app']['role']!='laptop':
            raise ValueError('Mismatched app roles')
        hosts={h['app']['role']:dict(metadata=h['metadata'],send_intervals_ms=h['send_intervals_ms'],
            size_histogram=h['size_histogram'],control_seconds=h['control_seconds'],controls=h['controls'],capture=h.get('capture')) for h in (pc,lap)}
        comparisons=[compare(pc,lap,name.startswith('call')),compare(lap,pc,name.startswith('call'))]
        phases.append(dict(name=name,hosts=hosts,directions=comparisons))
        print(name,[(d['direction'],d['gap_count'],d['categories']) for d in comparisons])
        for role,h in hosts.items():
            print(role,'pacing',h['send_intervals_ms'],'gateway',h['controls'][1])
    Path(output).write_text(json.dumps(dict(note='Intervals only, no cross-host absolute timestamps. OS events are not NIC wire times. Fixed synthetic payloads approximate RTP size/cadence but omit encryption, RTCP/STUN, variable codec payload and audio processing. Counterbalanced but not randomized; background traffic uncontrolled. ICMP differs from voice UDP.',phases=phases),ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    sends=[dict(Sequence=i,Start=i*.01,End=i*.01+.0001) for i in range(12000)]
    source=dict(app=dict(role='pc',sends=sends),low=5,high=118,sends=[r['End'] for r in sends])
    results=[]
    for offset in (1000,90000):
        receives=[dict(Sequence=i,At=offset+i*.01+(.05 if i>=5000 else 0)) for i in range(12000)]
        target=dict(app=dict(role='laptop',receives=receives),low=offset+5,high=offset+118,receives=[r['At'] for r in receives])
        result=compare(source,target,False)
        assert result['gap_count']==1 and result['categories']=={'variation-between-os-send-and-os-receive':1}
        assert result['synthetic_identity']==dict(duplicates=0,reordered=0,missing_sequences_in_paired_range=0)
        results.append(result)
    assert results[0]['matched']==results[1]['matched']
    print('PASS: paired UDP identity, independent clock offsets, OS gap localization')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:
        self_test()
    elif len(sys.argv)==4:
        run(*sys.argv[1:])
    else:
        raise SystemExit('Usage: analyze_udp_call.py PC_RESULTS LAPTOP_RESULTS OUTPUT_JSON')
