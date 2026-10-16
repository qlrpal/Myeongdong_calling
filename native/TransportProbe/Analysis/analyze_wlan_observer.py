"""Off/on/on/off WLAN query observer comparison, identical synthetic UDP plans."""
import json
import sys
from collections import Counter
from pathlib import Path
from analyze_pipeline import stats
from analyze_udp_call import load, compare, read
from analyze_udp_pattern import validate
from analyze_wlan_radio import radio

ORDER = ('off1', 'on1', 'on2', 'off2')


def clusters(gaps):
    groups=[]
    for g in sorted(gaps,key=lambda x:x['target_received_at']):
        end=g['target_received_at'];start=end-g['receive_gap_ms']/1000
        if groups and start-groups[-1]['end']<=.25:
            groups[-1]['end']=max(groups[-1]['end'],end)
            groups[-1]['gaps']+=1
        else:
            groups.append(dict(start=start,end=end,gaps=1))
    return dict(count=len(groups),max_gaps=max((g['gaps'] for g in groups),default=0),
        largest=max(groups,key=lambda g:g['gaps']) if groups else None,
        note='Gap windows separated by <=250ms grouped descriptively; not independent root-cause incidents or time occupancy.')


def control_window(path,start,end):
    result=[]
    for row in read(path)['rows']:
        samples=[s for s in row['samples'] if s['startedAt']<=end and s['completedAt']>=start]
        result.append(dict(target=row['target'],samples=len(samples),failed=sum(s['status']!='Success' for s in samples),
            rtt_ms=stats(s.get('kernelRttMs') for s in samples),
            at_least_20ms=sum(s.get('kernelRttMs') is not None and s['kernelRttMs']>=20 for s in samples)))
    return result


def intervals(source, target):
    tx={s['Sequence']:s for s in source['app']['sends']}
    pairs=[(tx[r['Sequence']],r) for r in sorted(target['app']['receives'],key=lambda r:r['At'])
        if r['Sequence'] in tx and source['low']<=tx[r['Sequence']]['End']<=source['high'] and target['low']<=r['At']<=target['high']]
    if len(pairs)<9000:
        raise ValueError('Insufficient paired intervals')
    receive=[];added=[]
    for (a,ar),(b,br) in zip(pairs,pairs[1:]):
        if b['Sequence']!=a['Sequence']+1:
            raise ValueError('Non-contiguous paired sequence')
        rg=(br['At']-ar['At'])*1000
        sg=(b['End']-a['End'])*1000
        receive.append(rg);added.append(rg-sg)
    return dict(receive_interval_ms=stats(receive),
        receive_over_20ms=sum(x>20 for x in receive),receive_over_30ms=sum(x>30 for x in receive),receive_over_50ms=sum(x>50 for x in receive),
        extra_interval_over_20ms=sum(x>20 for x in added),extra_interval_over_50ms=sum(x>50 for x in added),
        note='Extra interval = receiver interval minus paired sender interval; varying clocks are not subtracted. This is interval variation, not one-way or mouth-to-ear latency.')


def run(pc_root,laptop_root,pc_plan,laptop_plan,output):
    roots=dict(pc=Path(pc_root),laptop=Path(laptop_root));plans={}
    for role,path in (('pc',pc_plan),('laptop',laptop_plan)):
        raw=Path(path).read_bytes();plan=json.loads(raw.decode('utf-8-sig'));plans[role]=(plan,raw)
        meta=read(roots[role]/'run.json')
        if not meta['complete'] or meta['role']!=role or plan['role']!=role or [p['name'] for p in meta['phases']]!=list(ORDER):
            raise ValueError('Incomplete/misordered run or wrong role')
    phases=[]
    for name in ORDER:
        on=name.startswith('on')
        hosts={role:load(root,name) for role,root in roots.items()}
        directions=[{**compare(hosts[a],hosts[b],False),**intervals(hosts[a],hosts[b])} for a,b in (('pc','laptop'),('laptop','pc'))]
        for d in directions:
            d['gap_clusters']=clusters(d['gaps'])
            largest=d['gap_clusters']['largest']
            if largest:
                a,b=d['direction'].split(' -> ')
                chosen=[g for g in d['gaps'] if largest['start']<=g['target_received_at']<=largest['end']]
                source_start=min(g['source_send_end']-g['send_gap_ms']/1000 for g in chosen)
                source_end=max(g['source_send_end'] for g in chosen)
                d['largest_cluster_controls']=dict(
                    source=control_window(roots[a]/name/'icmp.json',source_start-.1,source_end+.1),
                    receiver=control_window(roots[b]/name/'icmp.json',largest['start']-.1,largest['end']+.1),
                    note='Own-host cluster windows padded by100ms; different host clocks are not aligned by subtraction. Descriptive proximity only.')
        summaries={}
        for role,h in hosts.items():
            folder=roots[role]/name;phase=read(folder/'phase.json')
            if h['app']['role']!=role or phase.get('wlanPolling') is not on:
                raise ValueError('Wrong app role/polling assignment')
            if on:
                if phase.get('metricsExit')!=0 or not phase.get('metricsPid'):
                    raise ValueError('WLAN collection failed')
            elif any((folder/f).exists() for f in ('wlan.json','wlan.stdout.log','wlan.stderr.log')) or 'metricsPid' in phase:
                raise ValueError('Polling files/process recorded in off phase')
            plan,raw=plans[role];check=validate(h['app'],plan,raw,'replay')
            if Counter(h['size_histogram'])!=Counter(i['Bytes'] for i in plan['items']):
                raise ValueError('OS length multiset mismatch')
            windows=[]
            for d in directions:
                sender=d['direction'].split(' -> ')[0]==role
                for g in d['gaps']:
                    end=g['source_send_end'] if sender else g['target_received_at']
                    width=g['send_gap_ms'] if sender else g['receive_gap_ms']
                    windows.append((end-width/1000,end))
            summaries[role]=dict(replay_check=check,send_intervals_ms=h['send_intervals_ms'],
                controls=h['controls'],control_seconds=h['control_seconds'],metadata=h['metadata'],
                radio=radio(folder/'wlan.json',h['low'],h['high'],windows) if on else None)
        phases.append(dict(name=name,wlan_polling=on,hosts=summaries,directions=directions))
        print(name,[(d['direction'],d['gap_count'],d['categories'],d['receive_interval_ms']) for d in directions])
    Path(output).write_text(json.dumps(dict(note='Fixed off/on/on/off, not randomized or independent replications. Background traffic/time/radio state uncontrolled; potential polling carryover into final off phase. Polling changes both hosts together. Same ICMP and OS trace in all phases. No off-phase radio metrics by design. No absolute cross-host timestamp subtraction or direct NIC queue/sleep measurement.',phases=phases),ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    # Same gaps with clocks offset by hours must yield identical interval distributions.
    tx=[dict(Sequence=i,End=i*.01) for i in range(10000)]
    source=dict(app=dict(sends=tx),low=0,high=100)
    results=[]
    for offset in (1000,90000):
        rx=[dict(Sequence=i,At=offset+i*.01+(.06 if i>=5000 else 0)) for i in range(10000)]
        target=dict(app=dict(receives=rx),low=offset,high=offset+101)
        result=intervals(source,target)
        assert result['receive_over_50ms']==1 and result['extra_interval_over_50ms']==1
        results.append(result)
    assert results[0]==results[1]
    assert clusters([dict(target_received_at=t,receive_gap_ms=40) for t in (1,1.2,2)])['count']==2
    print('PASS: paired interval variation, independent clocks and single induced tail gap')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:run(*sys.argv[1:])
    else:raise SystemExit('Usage: analyze_wlan_observer.py PC_ROOT LAPTOP_ROOT PC_PLAN LAPTOP_PLAN OUTPUT')
