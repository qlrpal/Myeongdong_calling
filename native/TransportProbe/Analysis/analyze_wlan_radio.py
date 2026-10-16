"""Correlate coarse, whole-adapter WLAN deltas with same-host UDP gap windows."""
import json
import sys
from collections import Counter
from pathlib import Path
from analyze_pipeline import stats
from analyze_udp_call import load, compare, read
from analyze_udp_pattern import validate

ORDER = ('replay1', 'replay2')
COUNTERS = ('tx', 'rx', 'retry', 'multipleRetry', 'ackFailure', 'failed', 'fcsError')


def delta(a, b):
    # Counter reset/wrap is unknown, never silently a zero or guessed wrap.
    return b-a if b >= a else None


def overlaps(a, b, windows):
    return any(a <= end and start <= b for start, end in windows)


def summarize_intervals(rows):
    valid = [r for r in rows if all(r['delta'][k] is not None for k in COUNTERS)]
    totals = {k: sum(r['delta'][k] for r in valid) for k in COUNTERS}
    return dict(intervals=len(rows), valid_intervals=len(valid),
        seconds=sum(r['end']-r['start'] for r in valid), totals=totals,
        frames_retried_per_tx_percent=100*totals['retry']/totals['tx'] if totals['tx'] else None,
        note='Retry counts successfully transmitted frames needing >=1 retry, not all retry attempts; adapter-wide, not RTP loss.')


def radio(path, low, high, windows):
    root = read(path)
    if root['openCode'] != 0 or root['closeCode'] != 0:
        raise ValueError('WLAN handle failed')
    snapshots = root['snapshots']
    if not snapshots or snapshots[0]['startedAt'] > low or snapshots[-1]['endedAt'] < high:
        raise ValueError('Insufficient WLAN coverage')
    inside = [s for s in snapshots if low <= s['startedAt'] <= s['endedAt'] <= high]
    interfaces = sorted({i['id'] for s in inside for i in s['interfaces']})
    result = dict(snapshots=len(inside), enumerate_codes=dict(Counter(s['enumerateCode'] for s in inside)),
        snapshot_ms=stats((s['endedAt']-s['startedAt'])*1000 for s in inside),
        poll_interval_ms=stats((b['startedAt']-a['startedAt'])*1000 for a,b in zip(inside,inside[1:])),
        system_power_states=[json.loads(x) for x in sorted({json.dumps(s['systemPower'],sort_keys=True) for s in inside})], interfaces=[])
    for iid in interfaces:
        points = [(s, i) for s in snapshots for i in s['interfaces'] if i['id']==iid]
        queries = {}
        for key in ('statistics','quality','rssi','backgroundScanEnabled','mediaStreamingMode','radio'):
            values=[i[key] for s,i in points if key in i and low<=s['startedAt']<=high]
            queries[key]=dict(codes=dict(Counter(v['Code'] for v in values)),
                decode_errors=dict(Counter(v['DecodeError'] for v in values if v['DecodeError'])),
                duration_ms=stats((v['EndedAt']-v['StartedAt'])*1000 for v in values))
        good=[(s,i['statistics']) for s,i in points if i.get('statistics',{}).get('Data') is not None]
        slots=sorted({p['index'] for _,q in good for p in q['Data']['phys']})
        identical=bool(good) and all(len(q['Data']['phys'])>1 and all(p['counters']==q['Data']['phys'][0]['counters'] for p in q['Data']['phys']) for _,q in good)
        # Duplicated driver slots must not be added together. Otherwise preserve every slot.
        selected=slots[:1] if identical else slots
        slot_results=[]
        for slot in selected:
            samples=[(q,p['counters']) for _,q in good for p in q['Data']['phys'] if p['index']==slot]
            intervals=[]
            for (a,ac),(b,bc) in zip(samples,samples[1:]):
                # Native sampling time is somewhere inside each query; widen to query edges.
                start,end=a['StartedAt'],b['EndedAt']
                if low<=start<=end<=high:
                    intervals.append(dict(start=start,end=end,delta={k:delta(ac[k],bc[k]) for k in COUNTERS}))
            near=[r for r in intervals if overlaps(r['start'],r['end'],windows)]
            away=[r for r in intervals if not overlaps(r['start'],r['end'],windows)]
            slot_results.append(dict(slot=slot,all=summarize_intervals(intervals),
                gap_overlapping=summarize_intervals(near),outside_gap_windows=summarize_intervals(away),
                reset_intervals=sum(any(v is None for v in r['delta'].values()) for r in intervals)))
        state_values={}
        for key in ('quality','rssi','backgroundScanEnabled','mediaStreamingMode','radio'):
            vals=[i[key]['Data'] for s,i in points if low<=s['startedAt']<=high and i.get(key,{}).get('Data') is not None]
            state_values[key]=[dict(value=json.loads(v),samples=n) for v,n in Counter(json.dumps(v,sort_keys=True) for v in vals).items()]
        mac={}
        bounded=[q for s,q in good if low<=q['StartedAt']<=q['EndedAt']<=high]
        if len(bounded)>1:
            mac={k:delta(bounded[0]['Data'][k],bounded[-1]['Data'][k]) for k in ('unicastTx','unicastRx','multicastTx','multicastRx')}
        result['interfaces'].append(dict(id=iid,queries=queries,mac_counter_change=mac,all_phy_slots_identical=identical,
            original_slot_count=len(slots),slots=slot_results,states=state_values))
    return result


def run(pc_root, laptop_root, pc_plan, laptop_plan, output):
    roots=dict(pc=Path(pc_root),laptop=Path(laptop_root)); plans={}
    for role,path in (('pc',pc_plan),('laptop',laptop_plan)):
        raw=Path(path).read_bytes(); plans[role]=(json.loads(raw.decode('utf-8-sig')),raw)
        if plans[role][0]['role']!=role:
            raise ValueError('Plan role mismatch')
        meta=read(roots[role]/'run.json')
        if not meta['complete'] or meta['role']!=role or [p['name'] for p in meta['phases']]!=list(ORDER):
            raise ValueError('Incomplete/misordered run')
    phases=[]
    for name in ORDER:
        hosts={role:load(root,name) for role,root in roots.items()}
        directions=[compare(hosts['pc'],hosts['laptop'],False),compare(hosts['laptop'],hosts['pc'],False)]
        summaries={}
        for role,h in hosts.items():
            phase=read(roots[role]/name/'phase.json')
            if phase.get('metricsExit')!=0 or h['app']['role']!=role:
                raise ValueError('Metrics failed/role mismatch')
            plan,raw=plans[role]; check=validate(h['app'],plan,raw,'replay')
            if Counter(h['size_histogram'])!=Counter(i['Bytes'] for i in plan['items']):
                raise ValueError('OS length multiset mismatch')
            windows=[]
            for d in directions:
                sender=d['direction'].split(' -> ')[0]==role
                for g in d['gaps']:
                    end=g['source_send_end'] if sender else g['target_received_at']
                    width=g['send_gap_ms'] if sender else g['receive_gap_ms']
                    windows.append((end-width/1000,end))
            summaries[role]=dict(replay_check=check,controls=h['controls'],control_seconds=h['control_seconds'],
                metadata=h['metadata'],radio=radio(roots[role]/name/'wlan.json',h['low'],h['high'],windows))
        phases.append(dict(name=name,hosts=summaries,directions=directions))
        print(name,[(d['direction'],d['gap_count'],d['categories']) for d in directions])
    Path(output).write_text(json.dumps(dict(note='No cross-host absolute clock subtraction. WLAN ~500ms query intervals are coarse and adapter-wide. Each statistics interval counted once even if many gaps overlap. Windows combine both directions, not proof of retry causality. Polling may change radio behavior. No NIC queue residence, actual sleep or AP telemetry.',phases=phases),ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    assert delta(2**63,2**63+7)==7 and delta(50,4) is None
    windows=[(1.1,1.2),(1.3,1.4)]
    rows=[dict(start=1,end=1.5,delta={k:2 for k in COUNTERS}),dict(start=2,end=2.5,delta={k:1 for k in COUNTERS})]
    assert summarize_intervals([r for r in rows if overlaps(r['start'],r['end'],windows)])['totals']['retry']==2
    rows[0]['delta']['retry']=None
    assert summarize_intervals(rows)['valid_intervals']==1
    print('PASS: uint64 delta, reset exclusion, deduplicated overlap windows')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']: self_test()
    elif len(sys.argv)==6: run(*sys.argv[1:])
    else: raise SystemExit('Usage: analyze_wlan_radio.py PC_ROOT LAPTOP_ROOT PC_PLAN LAPTOP_PLAN OUTPUT')
