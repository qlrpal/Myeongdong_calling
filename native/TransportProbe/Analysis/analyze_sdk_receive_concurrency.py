"""Summarize diagnostic SDK receive ownership; never infer physical packet loss."""
import json
from pathlib import Path
import sys

def analyze(snapshot):
    if not snapshot or not snapshot.get('receivers'):
        raise ValueError('SDK receive instrumentation unavailable (stock/legacy report)')
    rows = []
    for receiver in snapshot['receivers']:
        if receiver['starts'] - receiver['finishes'] != receiver['activeOwners']:
            raise ValueError('Inconsistent receive ownership counters')
        if not 0 <= receiver['outstanding'] <= receiver['activeOwners']:
            raise ValueError('Inconsistent outstanding IO count')
        for operation in receiver.get('operationLifetimes', []):
            begin, io, end = (operation[k] for k in ('beginAt', 'ioCompletedAt', 'finishedAt'))
            if (io and io < begin) or (end and (not io or end < io)):
                raise ValueError('Invalid same-QPC operation lifetime')
        rows.append({k: receiver[k] for k in (
            'local', 'buffer', 'starts', 'finishes', 'outstanding', 'activeOwners',
            'maxOutstanding', 'maxOwners', 'overlapStarts')})
    return dict(gate=snapshot['gate'], receivers=rows,
                overlappingIoReceivers=sum(r['maxOutstanding'] > 1 for r in rows),
                overlappingOwnershipReceivers=sum(r['maxOwners'] > 1 for r in rows),
                outstandingAtSnapshot=sum(r['outstanding'] for r in rows),
                scope='SDK async receive IO/handler ownership, not physical receive timestamps or network loss')

def self_test():
    receiver = dict(local='test', buffer=1, starts=3, finishes=3, outstanding=0,
                    activeOwners=0, maxOutstanding=2, maxOwners=2, overlapStarts=1,
                    operationLifetimes=[dict(beginAt=1, ioCompletedAt=2, finishedAt=3)])
    result = analyze(dict(gate='original', receivers=[receiver]))
    assert result['overlappingIoReceivers'] == 1
    for bad in (None, {}, dict(gate='serialized', receivers=[]), dict(gate='original', receivers=[dict(receiver, finishes=2)])):
        try:
            analyze(bad)
        except ValueError:
            continue
        raise AssertionError('Invalid/absent diagnostics accepted')
    print('PASS: ownership invariants, operation lifetimes, legacy absence rejection')

if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 3:
        if Path(sys.argv[1]).resolve() == Path(sys.argv[2]).resolve():
            raise ValueError('Preserve source diagnostics')
        report = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
        if 'results' in report:
            result, previous_count = [], 0
            for row in report['results']:
                snapshot = row.get('sdkReceiveDiagnostics')
                cumulative = analyze(snapshot)
                new_receivers = snapshot['receivers'][previous_count:]
                if len(new_receivers) != 1:
                    raise ValueError('Expected one new SDK receiver per raw stress phase')
                result.append(dict(mode=row['mode'],
                                   newReceiverSummary=analyze(dict(snapshot, receivers=new_receivers)),
                                   cumulativeSummary=cumulative))
                previous_count = len(snapshot['receivers'])
        else:
            result = analyze(report.get('sdkReceiveDiagnostics'))
        Path(sys.argv[2]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    else:
        raise SystemExit('Usage: analyze_sdk_receive_concurrency.py REPORT.json OUTPUT.json | --self-test')
