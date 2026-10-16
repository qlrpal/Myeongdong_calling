"""Payload-free WLAN ETW correlation during repeated UDP replay without WLAN polling."""
import json
import sys
import tempfile
from collections import Counter
from pathlib import Path
from analyze_udp_call import load,compare,read
from analyze_udp_pattern import validate
from analyze_wlan_observer import intervals,clusters,control_window
from analyze_wlan_control import analyze as correlate
from export_raw_wlan import FIELDS

ORDER=('replay1','replay2')


def verify_wlan(folder,phase,low,high):
    wlan=read(folder/'wlan-events.json');meta=read(folder/'wlan.etl.json')
    if phase.get('wlanPolling') is not False or phase.get('wlanTraceExit')!=0 or 'metricsPid' in phase:
        raise ValueError('Polling enabled or WLAN collector failed')
    if any((folder/f).exists() for f in ('wlan.json','wlan.stdout.log','wlan.stderr.log')):
        raise ValueError('Unexpected WLAN polling output')
    cap=wlan.get('capture',{})
    if wlan['clockSource']!='ETW raw QPC' or cap.get('eventsLost')!=0 or cap.get('stopExitCode')!=0 or cap['session']!=meta['session']:
        raise ValueError('Invalid or mismatched WLAN trace')
    if set(cap.get('eventIds',[]))!=set(FIELDS) or not cap.get('startedQpc') or not cap.get('endedQpc'):
        raise ValueError('Expected expanded metadata filter and active coverage')
    if cap['startedQpc']>low or cap['endedQpc']<high:
        raise ValueError('WLAN trace does not cover UDP interval')
    return wlan


def run(pc_root,laptop_root,pc_plan,laptop_plan,output):
    roots=dict(pc=Path(pc_root),laptop=Path(laptop_root));plans={}
    for role,path in (('pc',pc_plan),('laptop',laptop_plan)):
        data=Path(path).read_bytes();plan=json.loads(data.decode('utf-8-sig'));plans[role]=(plan,data)
        meta=read(roots[role]/'run.json')
        if not meta['complete'] or meta['role']!=role or plan['role']!=role or [p['name'] for p in meta['phases']]!=list(ORDER):
            raise ValueError('Incomplete/misordered run or role mismatch')
    phases=[]
    for name in ORDER:
        hosts={role:load(root,name) for role,root in roots.items()}
        directions=[{**compare(hosts[a],hosts[b],False),**intervals(hosts[a],hosts[b])} for a,b in (('pc','laptop'),('laptop','pc'))]
        summaries={};inputs={}
        for role,h in hosts.items():
            folder=roots[role]/name;phase=read(folder/'phase.json');plan,data=plans[role]
            if h['app']['role']!=role:raise ValueError('App role mismatch')
            check=validate(h['app'],plan,data,'replay')
            if Counter(h['size_histogram'])!=Counter(i['Bytes'] for i in plan['items']):
                raise ValueError('OS packet-length multiset mismatch')
            wlan=verify_wlan(folder,phase,h['low'],h['high']);inputs[role]=(read(folder/'icmp.json'),wlan)
            summaries[role]=dict(replay_check=check,send_intervals_ms=h['send_intervals_ms'],metadata=h['metadata'],
                controls=h['controls'],control_seconds=h['control_seconds'],wlan_capture=wlan['capture'],
                wlan_event_counts_in_udp_interval=dict(Counter(r['id'] for r in wlan['rows'] if h['low']<=r['at']<=h['high'])))
        correlated=correlate(dict(directions=[dict(direction=d['direction'],receive_gaps=d['gaps']) for d in directions]),inputs)
        for role in roots:
            nearby={json.dumps(e,sort_keys=True) for g in correlated['gaps'] for e in g['hosts'][role]['nearby_events']}
            correlated['hosts'][role]['unique_near_gap_event_counts']=dict(Counter(json.loads(e)['id'] for e in nearby))
        for d in directions:
            d['gap_clusters']=clusters(d['gaps']);largest=d['gap_clusters']['largest']
            if largest:
                a,b=d['direction'].split(' -> ')
                chosen=[g for g in d['gaps'] if largest['start']<=g['target_received_at']<=largest['end']]
                start=min(g['source_send_end']-g['send_gap_ms']/1000 for g in chosen);end=max(g['source_send_end'] for g in chosen)
                d['largest_cluster_controls']=dict(source=control_window(roots[a]/name/'icmp.json',start-.1,end+.1),
                    receiver=control_window(roots[b]/name/'icmp.json',largest['start']-.1,largest['end']+.1))
        phases.append(dict(name=name,hosts=summaries,directions=directions,wlan_correlation=correlated))
        print(name,[(d['direction'],d['gap_count'],d['categories']) for d in directions])
        print('WLAN',[(role,h['event_counts'],h['unique_near_gap_event_counts']) for role,h in correlated['hosts'].items()])
    Path(output).write_text(json.dumps(dict(note='No WLAN statistics/quality API polling. WPR/kernel UDP ETW and ICMP still observe the system. Same-host QPC only. Filter includes expected-roaming notification, not complete roaming or actual NIC sleep telemetry. Absent events do not exclude firmware behavior; OS boundaries are not wire timestamps.',phases=phases),ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    scratch=(Path(__file__).resolve().parents[2]/'.local').resolve()
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='wlan-check-',dir=scratch) as name:
        folder=Path(name)
        assert folder.resolve().parent==scratch
        cap=dict(eventsLost=0,stopExitCode=0,session='test',eventIds=list(FIELDS),startedQpc=1,endedQpc=200)
        wlan=dict(clockSource='ETW raw QPC',capture=cap,rows=[])
        (folder/'wlan.etl.json').write_text(json.dumps(dict(session='test')),encoding='utf-8')
        (folder/'wlan-events.json').write_text(json.dumps(wlan),encoding='utf-8')
        phase=dict(wlanPolling=False,wlanTraceExit=0)
        assert verify_wlan(folder,phase,5,120)['rows']==[]
        for bad,low,high in ((dict(phase,wlanPolling=True),5,120),(dict(phase,metricsPid=1),5,120),(phase,0,120),(phase,5,201)):
            try:verify_wlan(folder,bad,low,high);raise AssertionError('Invalid capture must fail')
            except ValueError:pass
        (folder/'wlan.json').write_text('{}',encoding='utf-8')
        try:verify_wlan(folder,phase,5,120);raise AssertionError('Polling file must fail')
        except ValueError:pass
    print('PASS: zero-event trace coverage, polling rejection and capture bounds')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:run(*sys.argv[1:])
    else:raise SystemExit('Usage: analyze_wlan_replay.py PC_ROOT LAPTOP_ROOT PC_PLAN LAPTOP_PLAN OUTPUT')
