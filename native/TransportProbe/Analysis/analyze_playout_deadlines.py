"""Join late admissions with earlier PLC decisions using one receiver's clock."""
import json
import math
import sys
from bisect import bisect_right
from collections import defaultdict
from pathlib import Path
from analyze_pipeline import key, stats
from analyze_playout_target import summarize


def validate(report):
    if 'options' not in report:
        summarize(report)  # Preserve the legacy trial's boundary-error rule.
        return
    trace = report['trace']
    reads = trace['Reads']
    if (report.get('error') or trace['EvictedRecords'] or len(reads) < 2
            or not trace['Admissions'] or len(report['samples']) < 2
            or any(len(s['Peers']) != 1 or s['Peers'][0]['State'] != 'connected' for s in report['samples'])
            or any(not math.isfinite(r['LockAt']) for r in reads)
            or reads[-1]['LockAt'] - reads[0]['LockAt'] < 8):
        raise ValueError('Incomplete/evicted extreme trace cannot support deadline analysis')


def inspect(report):
    trace = report['trace']
    losses = defaultdict(list)
    played = defaultdict(list)
    for row in sorted(trace['Playout'], key=lambda p: p['At']):
        if row['Outcome'] == 'plc-loss':
            losses[key(row)].append(row)
        elif row['Outcome'] == 'played':
            played[key(row)].append(row['At'])
    matches, after_played, unmatched = [], 0, 0
    for row in trace['Admissions']:
        if row['Outcome'] != 'late':
            continue
        ident = key(row)
        if any(at <= row['LockAt'] for at in played[ident]):
            after_played += 1
            continue
        decisions = losses[ident]
        index = bisect_right([p['At'] for p in decisions], row['LockAt']) - 1
        if index < 0:
            unmatched += 1
            continue
        decision = decisions[index]
        matches.append(dict(overrun_ms=(row['LockAt'] - decision['At']) * 1000,
                            empty_queue=decision['QueueMs'] == 0))
    first = trace['Reads'][0] if trace['Reads'] else None
    first_admission = min(trace['Admissions'], key=lambda row: row['LockAt'], default=None)
    if first and (not first_admission or first['LockAt'] < first_admission['LockAt']):
        initial = dict(kind='before-read', queue_ms=first['QueueBeforeMs'])
    elif first_admission:
        initial = dict(kind='after-admission', queue_ms=first_admission.get('QueueMs'))
    else:
        initial = None
    return dict(role=report.get('role'), original_error=report.get('error'),
        late_after_plc=len(matches), late_after_played=after_played, late_without_observed_prior_decision=unmatched,
        plc_to_late_admission_ms=stats(row['overrun_ms'] for row in matches),
        within_5ms=sum(row['overrun_ms'] <= 5 for row in matches),
        empty_queue_decisions=sum(row['empty_queue'] for row in matches),
        first_observed_buffer_event=initial,
        first_recorded_read=dict(started_after_read=first['PlayoutStarted'], queue_before_ms=first['QueueBeforeMs'],
                                 requested_ms=first['RequestedSamples'] / 48) if first else None)


def self_test():
    def fixture(offset):
        return dict(role='test', trace=dict(
            Playout=[dict(Ssrc=1, Timestamp=480, At=offset + 2, Outcome='plc-loss', QueueMs=0),
                     dict(Ssrc=1, Timestamp=960, At=offset + 2, Outcome='played', QueueMs=10)],
            Admissions=[dict(Ssrc=1, Timestamp=t, LockAt=offset + 2.003, Outcome='late') for t in (480, 960, 1440)],
            Reads=[dict(LockAt=offset + 2, PlayoutStarted=True, QueueBeforeMs=20, RequestedSamples=480)]))
    a, b = inspect(fixture(0)), inspect(fixture(90000))
    assert a == b
    assert a['plc_to_late_admission_ms']['p50'] == 3
    assert a['late_after_played'] == a['late_without_observed_prior_decision'] == 1
    assert a['empty_queue_decisions'] == a['within_5ms'] == 1
    extreme = dict(options=None, error=None, samples=[dict(Peers=[dict(State='connected')])] * 2,
        trace=dict(EvictedRecords=0, Reads=[dict(LockAt=1), dict(LockAt=11)], Admissions=[{}]))
    validate(extreme)
    for patch in [dict(error='failed'), dict(trace={**extreme['trace'], 'EvictedRecords': 1}),
                  dict(trace={**extreme['trace'], 'Reads': [dict(LockAt=1), dict(LockAt=2)]})]:
        try:
            validate({**extreme, **patch})
        except ValueError:
            continue
        raise AssertionError('Invalid extreme trace accepted')
    print('PASS: receiver-local deadline join, clock-offset invariance and late duplicates/unknown decisions')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    else:
        if len(sys.argv) < 3:
            raise SystemExit('Usage: analyze_playout_deadlines.py OUTPUT_JSON INPUT_JSON...')
        results = []
        for path in sys.argv[2:]:
            report = json.loads(Path(path).read_text(encoding='utf-8-sig'))
            validate(report)
            results.append(dict(source=Path(path).name, **inspect(report)))
        Path(sys.argv[1]).write_text(json.dumps(dict(
            notes='Observed decoder PLC deadline to subsequent admission; one receiver clock only. Not a physical output deadline or proof of network loss. Full trace counts include startup and shutdown boundaries.',
            results=results), ensure_ascii=False, indent=2), encoding='utf-8')
        for row in results:
            print(row['source'], 'matched', row['late_after_plc'], 'overrun', row['plc_to_late_admission_ms'])
