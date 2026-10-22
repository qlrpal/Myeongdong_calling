"""Join bounded slow-render observations to same-host RTP lock/read/decode timings."""
import json
import sys
from pathlib import Path


def correlate(report):
    observations = {}
    for sample in report['samples']:
        for peer in sample['Peers']:
            execution = (peer['Audio'].get('Render') or {}).get('Execution') or {}
            for entry in execution.get('RecentWork') or []:
                observations[(entry['At'], entry['Phase'])] = entry
    trace = report.get('trace') or {}; reads = trace.get('Reads', []); plays = trace.get('Playout', [])
    rows = []
    for entry in sorted(observations.values(), key=lambda e:e['At']):
        begin = entry['At']; end = begin + entry['ElapsedMs'] / 1000
        # A wake entry describes the previous wake-to-wake interval, not a source call.
        overlapping_reads = [] if entry['Phase'] == 'wake-empty' else [r for r in reads if r['CalledAt'] < end and r['ReturnedAt'] > begin]
        decodes = [] if entry['Phase'] == 'wake-empty' else [r for r in plays if r.get('DecodeStart') is not None and r.get('DecodeEnd') is not None and r['DecodeStart'] < end and r['DecodeEnd'] > begin]
        rows.append(entry | {'matchingReads':len(overlapping_reads),
            'readLockWaitMs':sum((r['LockAt']-r['CalledAt'])*1000 for r in overlapping_reads),
            'readInsideMs':sum((r['ReturnedAt']-r['LockAt'])*1000 for r in overlapping_reads),
            'decodeMs':sum((r['DecodeEnd']-r['DecodeStart'])*1000 for r in decodes),
            'relativeFirstSnapshotSeconds':begin-report['samples'][0]['MonotonicSeconds']})
    return {'observations':len(rows),'work':rows,
            'notes':'Missing matchingReads means the event is outside the bounded packet trace, not zero read/decode cost. ThreadCpuMs is coarse Windows accounting; zero is not zero CPU and values can exceed wall time. GC counts are process-wide counters, not pause durations. Wake elapsed is wake-to-wake, not pure scheduler wait.'}


def self_test():
    work={'At':1.,'ElapsedMs':4.,'Phase':'slow-supply'}
    report={'samples':[{'MonotonicSeconds':0.,'Peers':[{'Audio':{'Render':{'Execution':{'RecentWork':[work,work]}}}}]}],
            'trace':{'Reads':[{'CalledAt':1.,'LockAt':1.001,'ReturnedAt':1.003},{'CalledAt':2.,'LockAt':2.,'ReturnedAt':2.01}],
                     'Playout':[{'DecodeStart':1.001,'DecodeEnd':1.002}]}}
    out=correlate(report); entry=out['work'][0]
    assert out['observations']==1 and entry['matchingReads']==1
    assert abs(entry['readLockWaitMs']-1)<1e-8 and abs(entry['readInsideMs']-2)<1e-8 and abs(entry['decodeMs']-1)<1e-8
    work['Phase']='wake-empty';assert correlate(report)['work'][0]['matchingReads']==0
    report['trace']={}; assert correlate(report)['work'][0]['matchingReads']==0
    print('PASS: observation deduplication, same-host interval joins, lock/decode separation, wake and missing-trace handling')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==3:
        source,target=map(Path,sys.argv[1:]);out=correlate(json.loads(source.read_text(encoding='utf-8-sig')))
        target.write_text(json.dumps(out,indent=2),encoding='utf-8')
        print(json.dumps({'observations':out['observations'],'slowest':sorted(out['work'],key=lambda r:r['ElapsedMs'],reverse=True)[:3]},indent=2))
    else:raise SystemExit('REPORT OUTPUT | --self-test')
