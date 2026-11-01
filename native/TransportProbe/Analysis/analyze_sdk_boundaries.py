"""Join opt-in diagnostic SDK metadata with a paired automatic incident capsule.

Only subtract QPC on the SAME host. Cross-host comparison uses packet intervals,
never absolute timestamps. Missing metadata is not labelled physical packet loss.
Usage: python analyze_sdk_boundaries.py PC.json LAPTOP.json OUTPUT.json
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path


def key(r):
    return r['Ssrc'], r.get('Timestamp', r.get('RtpTimestamp')), r['Sequence']


def index(records, stage, full=True):
    result = defaultdict(list)
    for r in records:
        if r['Stage'] == stage:
            result[key(r) if full else key(r)[:2]].append(r)
    return result


def summary(values):
    ordered = sorted(values)
    if not ordered:
        return {'n': 0}
    return {'n': len(ordered), 'medianMs': ordered[len(ordered)//2],
            'p95Ms': ordered[min(len(ordered)-1, int(len(ordered)*.95))], 'maxMs': ordered[-1]}


def analyze(local, peer):
    sdk = local.get('sdkBoundary') or {}
    if not sdk.get('enabled'):
        return {'enabled': False}
    data = sdk['data']; records = data['Records']
    incident = local['baselineIncident']; app = incident['Records']
    receives = index(records, 'udp-receive-end')
    enters = index(records, 'srtp-unprotect-enter')
    returns = index(records, 'srtp-unprotect-return')
    callbacks = index(app, 'authenticated-rtp-callback')
    posts = index(records, 'socket-send-post', False)
    api = index(app, 'send-api-enter', False)
    values = defaultdict(list); missing = Counter(); matched = 0
    for identity, group in callbacks.items():
        for callback in group:
            # Choose preceding observations; repeated identities remain visible in counts.
            before = lambda items: max((r for r in items if r['At'] <= callback['At']), key=lambda r:r['At'], default=None)
            udp = before(receives.get(identity, [])); enter = before(enters.get(identity, [])); end = before(returns.get(identity, []))
            if udp and enter and end:
                matched += 1
                values['udpReturnToUnprotectMs'].append(1000*(enter['At']-udp['At']))
                values['unprotectWorkMs'].append(1000*(end['At']-enter['At']))
                values['unprotectToAppMs'].append(1000*(callback['At']-end['At']))
                values['udpReturnToAppMs'].append(1000*(callback['At']-udp['At']))
            else:
                missing['callbackWithoutAllSdkObservations'] += 1
    for identity, group in api.items():
        for entry in group:
            post = min((p for p in posts.get(identity, []) if p['At'] >= entry['At']), key=lambda p:p['At'], default=None)
            if post: values['apiEntryToSocketPostMs'].append(1000*(post['At']-entry['At']))
            else: missing['apiWithoutSocketPost'] += 1
    completions = index(records, 'socket-send-end-observed')
    for identity, group in index(records, 'socket-send-post').items():
        for post in group:
            end = min((r for r in completions.get(identity, []) if r['At'] >= post['At']),key=lambda r:r['At'],default=None)
            if end: values['socketPostToCompletionObservationMs'].append(1000*(end['At']-post['At']))
            else: missing['socketPostWithoutCompletionObservation'] += 1
    window = (min((r['At'] for r in app),default=0),max((r['At'] for r in app),default=0))
    incomplete = []
    for identity, group in receives.items():
        for udp in group:
            if window[0] <= udp['At'] < window[1]-.1 and identity not in callbacks:
                incomplete.append({'ssrc':udp['Ssrc'],'timestamp':udp['Timestamp'],'sequence':udp['Sequence'],
                                   'unprotectStatuses':[r['Status'] for r in returns.get(identity, [])]})
    peer_data = (peer.get('sdkBoundary') or {}).get('data', {})
    peer_posts = index(peer_data.get('Records', []), 'socket-send-post')
    peer_completions = index(peer_data.get('Records', []), 'socket-send-end-observed')
    # Only test a peer's send interval bounded by identities actually seen here.
    # This is metadata non-observation, never a loss diagnosis.
    unobserved = []
    matched_peer = [r for identity, group in peer_posts.items() if identity in receives for r in group]
    if matched_peer:
        low = min(r['At'] for r in matched_peer); high = max(r['At'] for r in matched_peer)
        for identity, group in peer_posts.items():
            if identity not in receives:
                for r in group:
                    if low <= r['At'] <= high:
                        unobserved.append({'ssrc':r['Ssrc'], 'timestamp':r['Timestamp'], 'sequence':r['Sequence']})
    gaps = []
    by_ssrc = defaultdict(list)
    for r in app:
        if r['Stage'] == 'authenticated-rtp-callback': by_ssrc[r['Ssrc']].append(r)
    for group in by_ssrc.values():
        group.sort(key=lambda r:r['At'])
        for previous, current in zip(group, group[1:]):
            gap = 1000*(current['At']-previous['At'])
            if gap <= 50: continue
            item = {'ssrc':current['Ssrc'], 'previousSequence':previous['Sequence'],
                    'sequence':current['Sequence'], 'appGapMs':gap, 'localAt':current['At']}
            for name, mapping in [('udpGapMs', receives), ('peerSocketPostGapMs', peer_posts),
                                  ('peerSocketCompletionGapMs', peer_completions)]:
                a, b = mapping.get(key(previous), []), mapping.get(key(current), [])
                if len(a) == len(b) == 1: item[name] = 1000*(b[0]['At']-a[0]['At'])
                else: item[name] = None
            if item['udpGapMs'] is not None:
                item['appMinusUdpGapMs'] = gap-item['udpGapMs']
            if item['udpGapMs'] is not None and item['peerSocketPostGapMs'] is not None:
                item['udpMinusPeerPostGapMs'] = item['udpGapMs']-item['peerSocketPostGapMs']
            gaps.append(item)
    return {'enabled':True, 'sdkStages':dict(Counter(r['Stage'] for r in records)),
            'sdkSkipped':data['Skipped'], 'sdkOverwritten':data['Overwritten'],
            'appSkipped':incident['SkippedContention'], 'eventId':incident.get('EventId'),
            'callbacks':sum(map(len, callbacks.values())), 'matchedCallbacks':matched,
            'missingObservations':dict(missing), 'durations':{k:summary(v) for k,v in values.items()},
            'udpWithoutAppObservationInCapsuleWindow':incomplete,
            'unprotectStatus':dict(Counter(str(r['Status']) for r in records if r['Stage']=='srtp-unprotect-return')),
            'duplicateUdpIdentities':sum(max(0,len(v)-1) for v in receives.values()),
            'peerPostsWithoutUdpObservationInMatchedWindow':unobserved,
            'gapsOver50Ms':sorted(gaps, key=lambda g:g['appGapMs'], reverse=True)}


def main():
    a, b = [json.loads(Path(p).read_text(encoding='utf-8-sig')) for p in sys.argv[1:3]]
    if a['run'] != b['run'] or a['role'] == b['role']: raise ValueError('Mismatched pair')
    result = {'run':a['run'], 'roles':{a['role']:analyze(a,b), b['role']:analyze(b,a)},
              'limits':'QPC deltas only within host; cross-host values are same-packet interval differences, not one-way latency. UDP return is a scheduled software boundary, not OS arrival or physical RF. Missing records include window limits and contention; no physical loss attribution. Send completion is callback observation, not wire departure. PT111 only.'}
    Path(sys.argv[3]).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')


if __name__ == '__main__': main()
