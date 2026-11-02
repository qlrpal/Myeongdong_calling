"""Separate render drains and concealment using one host's existing metadata.

Usage: python analyze_playout_failures.py PARTICIPANT.json OUTPUT.json
No cross-host clock subtraction, packet-loss inference or scheduler root-cause claim.
"""
import json
import math
import sys
from pathlib import Path


def number(value):
    if not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError('Missing/nonfinite timing or counter')
    return value


def analyze(report):
    if report.get('error') or not report.get('completed') or report.get('stage') != 'complete':
        raise ValueError('Incomplete functional trial')
    window = next(w for w in report['windows'] if w['phase'] == 'baseline')
    samples = window['samples']
    if window.get('completed') is False:
        raise ValueError('Incomplete baseline window')
    if len(samples) < 2:
        raise ValueError('Insufficient baseline samples')
    start = number(samples[0]['MonotonicSeconds'])
    peer_id = samples[0]['Peers'][0].get('Id') if len(samples[0]['Peers']) == 1 else None
    if peer_id is None:
        raise ValueError('Missing peer identity')
    def audio(sample):
        peers = sample['Peers']
        if len(peers) != 1 or peers[0].get('Id') != peer_id:
            raise ValueError('Expected one stable receiver')
        return peers[0]['Audio']
    def counters(sample):
        a = audio(sample)
        return dict(plcMs=number(a['RtpPlayout']['ConcealedMs']),
                    renderEmpty=number(a['Render']['EmptyEvents']),
                    underfillMs=number(a['UnderfillMs']),
                    rebuffers=number(a['RtpPlayout']['Rebuffers']))
    events = []
    for before, after in zip(samples, samples[1:]):
        begin, end = [number(s['MonotonicSeconds']) for s in (before, after)]
        if end <= begin:
            raise ValueError('Nonmonotonic baseline')
        a, b = counters(before), counters(after)
        delta = {key: b[key] - a[key] for key in a}
        if any(value < 0 for value in delta.values()):
            raise ValueError('Counter reset during baseline')
        if any(delta.values()):
            execution = audio(after)['Render'].get('Execution') or {}
            events.append(dict(beginSeconds=begin-start, endSeconds=end-start, **delta,
                               observedWakeGapMaxMs=execution.get('MaxWakeGapMs'),
                               observedTimeoutWakes=execution.get('TimeoutWakes')))
    cap = report.get('baselineIncident')
    rows = sorted((cap or {}).get('Records', []), key=lambda r: number(r['At']))
    drains = []
    for event in rows:
        if event['Stage'] != 'render-empty':
            continue
        at = event['At']
        prior = [r for r in rows if r['At'] <= at and r['Stage'] == 'render-submit']
        reads_before = [r for r in rows if r['At'] <= at and r['Stage'] == 'playout-read']
        reads_after = [r for r in rows if r['At'] > at and r['Stage'] == 'playout-read']
        submit = prior[-1] if prior else None
        before = reads_before[-1] if reads_before else None
        after = reads_after[0] if reads_after else None
        # This is a description of observed app state, not proof of OS/driver cause.
        buffered = (event.get('Outcome') == 'wake' and submit is not None
                    and before is not None and after is not None
                    and before['Outcome'] == after['Outcome'] == 'read'
                    and before['DurationMs'] == after['DurationMs'] == 0
                    and before['QueueMs'] > 0 and after['QueueMs'] > 0
                    and (at-before['At'])*1000 <= before['QueueMs'])
        drains.append(dict(atSeconds=at-start, phase=event.get('Outcome'),
                           observation='wake-empty-with-source-reserve' if buffered else 'insufficient-or-other-state',
                           previousObservedSubmitGapMs=(at-submit['At'])*1000 if submit else None,
                           previousSubmitWorkMs=submit.get('WorkMs') if submit else None,
                           previousReportedDevicePaddingMs=submit.get('QueueMs') if submit else None,
                           previousCommitAudioMs=submit['Samples']*1000/submit['SampleRate']
                               if submit and submit.get('SampleRate', 0) > 0 else None,
                           previousSourceQueueMs=before['QueueMs'] if before else None,
                           nextSourceQueueMs=after['QueueMs'] if after else None,
                           nextReadDelayMs=(after['At']-at)*1000 if after else None,
                           nextReadPlcMs=after['DurationMs'] if after else None))
    first, last = counters(samples[0]), counters(samples[-1])
    def covered(event):
        # Full enclosing snapshot interval must fit; partial overlap is insufficient.
        return bool(rows and rows[0]['At'] <= start+event['beginSeconds']
                    and start+event['endSeconds'] <= rows[-1]['At'])
    for event in events:
        event['snapshotIntervalInsideCapsule'] = covered(event)
    return dict(schemaVersion=1, reserveMs=report.get('reserveMs'),
                observedSeconds=number(samples[-1]['MonotonicSeconds'])-start,
                totalDelta={key: last[key]-first[key] for key in first},
                counterEvents=events, renderDrains=drains,
                capsule=None if cap is None else dict(state=cap['State'], trigger=cap.get('Trigger'),
                    firstSeconds=rows[0]['At']-start if rows else None,
                    lastSeconds=rows[-1]['At']-start if rows else None,
                    skippedContention=cap['SkippedContention'], overwritten=cap['Overwritten'],
                    concealedMsInRecords=sum(r['DurationMs'] for r in rows if r['Stage'] == 'playout-conceal')),
                limits='Single-host metadata only. Counter events are snapshot intervals, not exact packet times. '
                    'Previous/next records may omit contended writes; missing records do not prove inactivity. '
                    'Positive source queue and PLC-free surrounding reads distinguish an observed device drain '
                    'from observed source starvation, but do not prove scheduler, driver, GC or event-delivery cause. '
                    'Reported device padding predates commit and is not physical output timing. '
                    'A frozen capsule cannot explain a later PLC outside its window. Not mouth-to-ear latency.')


if __name__ == '__main__':
    result = analyze(json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig')))
    Path(sys.argv[2]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
