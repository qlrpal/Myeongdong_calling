"""Correlate both hosts' gateway controls and allowlisted WLAN events with RTP gaps."""
import json
import sys
from pathlib import Path
from collections import Counter
from analyze_pipeline import stats


def scan_intervals(rows):
    pending = {}; intervals = []; unmatched = []
    for row in sorted(rows, key=lambda r: r['at']):
        if row['id'] not in (20022, 20023):
            continue
        key = row['fields']['InterfaceGuid'].lower()
        if row['id'] == 20022:
            pending.setdefault(key, []).append(row)
        elif key in pending:
            starts = pending.pop(key)
            if len(starts) == 1:
                start = starts[0]
                intervals.append(dict(start=start['at'], end=row['at'], interface=key,
                                      duration_ms=(row['at'] - start['at']) * 1000, status=row['fields']['Status']))
            else:
                unmatched.extend(starts + [row])
        else:
            unmatched.append(row)
    return intervals, unmatched + [row for starts in pending.values() for row in starts]


def overlaps(start, end, other_start, other_end):
    return start <= other_end and end >= other_start


def controls(icmp, start, end):
    result = []
    for row in icmp['rows']:
        nearby = [s for s in row['samples'] if overlaps(start, end, s['startedAt'], s['completedAt'])]
        result.append(dict(target=row['target'], count=len(nearby),
            failed=sum(s.get('status') != 'Success' for s in nearby),
            kernel_rtt_ms=stats(s.get('kernelRttMs') for s in nearby),
            managed_ms=stats((s['completedAt'] - s['startedAt']) * 1000 for s in nearby)))
    return result


def analyze(pipeline, hosts):
    result = dict(note='Each correlation uses its own host QPC. Window is gap edges plus/minus100ms. Correlation is not causation; ICMP differs from RTP. No absolute clock subtraction across PCs. No scan event does not exclude firmware scans/power behavior.', hosts={}, gaps=[])
    for role, (icmp, wlan) in hosts.items():
        if wlan['clockSource'] != 'ETW raw QPC' or wlan.get('capture', {}).get('eventsLost') != 0 or wlan['capture']['stopExitCode'] != 0:
            raise ValueError('Completed zero-loss raw QPC WLAN required')
        scans, unmatched = scan_intervals(wlan['rows'])
        result['hosts'][role] = dict(event_counts=dict(Counter(r['id'] for r in wlan['rows'])),
            scans=scans, unmatched_scans=unmatched,
            controls=controls(icmp, float('-inf'), float('inf')),
            heartbeat_ms=stats(h['intervalMs'] for h in icmp.get('heartbeat', [])))
    for direction in pipeline['directions']:
        source, target = direction['direction'].split(' -> ')
        for gap in direction['receive_gaps']:
            records = {}
            for role, at, span in ((source, gap['source_send_end'], gap['send_gap_ms']),
                                    (target, gap['target_received_at'], gap['receive_gap_ms'])):
                icmp, wlan = hosts[role]
                start, end = at - span / 1000 - .1, at + .1
                if any(not r['samples'] or r['samples'][0]['startedAt'] > start or
                       r['samples'][-1]['completedAt'] < end for r in icmp['rows']):
                    raise ValueError('ICMP does not cover an analyzed gap')
                scans = [s for s in result['hosts'][role]['scans'] if overlaps(start, end, s['start'], s['end'])]
                events = [r for r in wlan['rows'] if start <= r['at'] <= end]
                heartbeat = [h['intervalMs'] for h in icmp.get('heartbeat', [])
                             if overlaps(start, end, h['at'] - h['intervalMs'] / 1000, h['at'])]
                records[role] = dict(start=start, end=end, controls=controls(icmp, start, end),
                    overlapping_scans=scans, nearby_events=events, heartbeat_ms=stats(heartbeat))
            result['gaps'].append(dict(direction=direction['direction'], gap=gap, hosts=records))
    return result


def self_test():
    rows = [dict(id=20022, at=1, fields=dict(InterfaceGuid='A')),
            dict(id=20023, at=1.05, fields=dict(InterfaceGuid='a', Status='0'))]
    scans, unmatched = scan_intervals(rows)
    assert not unmatched and abs(scans[0]['duration_ms'] - 50) < .000001
    assert overlaps(1, 2, 1.5, 3) and not overlaps(1, 2, 3, 4)
    assert len(scan_intervals(rows[1:])[1]) == 1
    assert len(scan_intervals(rows[:1] * 2)[1]) == 2
    assert not scan_intervals(rows[:1] * 2 + rows[1:])[0]
    print('PASS: interface scan pairing, missing events, overlap windows')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 7:
        pipeline, pc_icmp, pc_wlan, laptop_icmp, laptop_wlan = [json.loads(Path(p).read_text(encoding='utf-8-sig')) for p in sys.argv[1:6]]
        result = analyze(pipeline, dict(pc=(pc_icmp, pc_wlan), laptop=(laptop_icmp, laptop_wlan)))
        Path(sys.argv[6]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        for role, host in result['hosts'].items():
            print(role, 'events', host['event_counts'], 'scans', len(host['scans']), 'unmatched', len(host['unmatched_scans']))
        print('Correlated gaps:', len(result['gaps']))
    else:
        raise SystemExit('Usage: analyze_wlan_control.py PIPELINE PC_ICMP PC_WLAN LAPTOP_ICMP LAPTOP_WLAN OUTPUT')
