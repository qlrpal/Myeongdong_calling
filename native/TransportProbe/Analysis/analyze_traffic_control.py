"""Compare sequential idle/call/idle controls using each host's own monotonic clock."""
import json
import sys
from pathlib import Path
from analyze_pipeline import stats

PHASES = ('idle-before', 'call', 'idle-after')
ERROR_COUNTERS = ('ReceivedPacketErrors', 'OutboundPacketErrors',
                  'ReceivedDiscardedPackets', 'OutboundDiscardedPackets')


def episodes(samples, threshold=20):
    groups = []
    for sample in samples:
        if sample.get('kernelRttMs') is None or sample['kernelRttMs'] < threshold:
            continue
        if groups and sample['startedAt'] - groups[-1]['end'] <= .25:
            groups[-1]['end'] = sample['completedAt']
            groups[-1]['samples'] += 1
            groups[-1]['max_rtt_ms'] = max(groups[-1]['max_rtt_ms'], sample['kernelRttMs'])
        else:
            groups.append(dict(start=sample['startedAt'], end=sample['completedAt'], samples=1,
                               max_rtt_ms=sample['kernelRttMs']))
    return groups


def counter_changes(before, after):
    old = {row['Name']: row for row in before['statistics']}
    changes = []
    for row in after['statistics']:
        if row['Name'] not in old:
            continue
        counters = {}
        for name in ERROR_COUNTERS + ('ReceivedBytes', 'SentBytes'):
            a, b = old[row['Name']].get(name), row.get(name)
            counters[name] = b - a if isinstance(a, (int, float)) and isinstance(b, (int, float)) and b >= a else None
        changes.append(dict(adapter=row['Name'], counters=counters))
    return changes


def excluding_largest_episode(samples, groups):
    if not groups:
        return None
    largest = max(groups, key=lambda g: g['samples'])
    remaining = [s for s in samples if s['completedAt'] < largest['start'] or s['startedAt'] > largest['end']]
    over = sum(s.get('kernelRttMs') is not None and s['kernelRttMs'] >= 20 for s in remaining)
    return dict(note='Post-hoc sensitivity only; primary results retain this episode.',
        excluded_high_samples=largest['samples'], remaining_samples=len(remaining),
        rtt_at_least_20ms=over, over_20ms_percent=round(over / len(remaining) * 100, 3) if remaining else None,
        kernel_rtt_ms=stats(s.get('kernelRttMs') for s in remaining))


def host(directory):
    root = Path(directory)
    run = json.loads((root / 'run.json').read_text(encoding='utf-8-sig'))
    if not run['complete'] or [p['phase'] for p in run['phases']] != list(PHASES):
        raise ValueError('Incomplete or incorrectly ordered comparison')
    app = json.loads((root / 'call.json').read_text(encoding='utf-8-sig'))
    if app.get('error') or app['trace']['EvictedRecords'] or app['role'] != run['role']:
        raise ValueError('Incomplete/mismatched call trace')
    results = []
    for phase in run['phases']:
        icmp = json.loads((root / (phase['phase'] + '-icmp.json')).read_text(encoding='utf-8-sig'))
        low = max(row['samples'][0]['startedAt'] for row in icmp['rows']) + 5
        high = min(row['samples'][-1]['completedAt'] for row in icmp['rows']) - 1
        if phase['phase'] == 'call':
            low = max(low, min(r['At'] for r in app['trace']['Receives']) + 5)
            high = min(high, max(r['At'] for r in app['trace']['Receives']) - 1)
        if high - low < 100 or low < phase['startedAt'] - 1 or high > phase['endedAt'] + 1:
            raise ValueError('Insufficient or out-of-phase control coverage')
        targets = []
        for row in icmp['rows']:
            samples = [s for s in row['samples'] if low <= s['startedAt'] <= s['completedAt'] <= high]
            if len(samples) < 100:
                raise ValueError('Insufficient target samples')
            over = sum(s.get('kernelRttMs') is not None and s['kernelRttMs'] >= 20 for s in samples)
            groups = episodes(samples)
            targets.append(dict(target=row['target'], samples=len(samples),
                failed=sum(s.get('status') != 'Success' for s in samples),
                kernel_rtt_ms=stats(s.get('kernelRttMs') for s in samples),
                managed_ms=stats((s['completedAt'] - s['startedAt']) * 1000 for s in samples),
                rtt_at_least_20ms=over, over_20ms_percent=round(over / len(samples) * 100, 3),
                excluding_largest_episode=excluding_largest_episode(samples, groups),
                episodes=[dict(start_seconds=round(g['start'] - low, 3),
                    end_seconds=round(g['end'] - low, 3), samples=g['samples'], max_rtt_ms=g['max_rtt_ms']) for g in groups]))
        results.append(dict(phase=phase['phase'], analyzed_seconds=round(high-low, 3),
            targets=targets, counter_changes=counter_changes(phase['before'], phase['after']),
            snapshots={key: dict(wlan=phase[key]['wlan'], wlan_fields_available=bool(phase[key]['wlan']), routes=phase[key]['routes'],
                adapters=phase[key]['adapters'], errors=phase[key]['errors']) for key in ('before','after')}))
    return dict(role=run['role'], phases=results)


def self_test():
    samples = [dict(startedAt=a, completedAt=a+.03, kernelRttMs=r) for a, r in ((1,30),(1.1,40),(2,30),(3,0))]
    groups = episodes(samples)
    assert len(groups) == 2 and groups[0]['samples'] == 2 and groups[0]['max_rtt_ms'] == 40
    assert not episodes([dict(startedAt=1, completedAt=2, kernelRttMs=None)])
    sensitivity=excluding_largest_episode(samples, groups)
    assert sensitivity['remaining_samples'] == 2 and sensitivity['rtt_at_least_20ms'] == 1
    before = dict(statistics=[dict(Name='test', ReceivedPacketErrors=3, SentBytes=10)])
    after = dict(statistics=[dict(Name='test', ReceivedPacketErrors=2, SentBytes=30)])
    delta = counter_changes(before, after)[0]['counters']
    assert delta['ReceivedPacketErrors'] is None and delta['SentBytes'] == 20 and delta['ReceivedBytes'] is None
    print('PASS: adjacent samples grouped, counter reset/missing values not treated as zero')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 4:
        hosts = [host(p) for p in sys.argv[1:3]]
        if [h['role'] for h in hosts] != ['pc','laptop']:
            raise ValueError('Expected PC then laptop')
        result = dict(note='Sequential A/B/A is not randomized. Idle means no call probe from this run. Other traffic is uncontrolled. Same ICMP in all phases can itself affect Wi-Fi power behavior. 20ms threshold and 250ms grouping describe samples, not independent incidents or radio airtime. Adapter counters are not 802.11 retransmission counters.', hosts=hosts)
        Path(sys.argv[3]).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        for h in hosts:
            for p in h['phases']:
                print(h['role'],p['phase'],[(t['target'],t['kernel_rtt_ms'],t['over_20ms_percent']) for t in p['targets']])
    else:
        raise SystemExit('Usage: analyze_traffic_control.py PC_DIRECTORY LAPTOP_DIRECTORY OUTPUT_JSON')
