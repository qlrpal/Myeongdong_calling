"""Validate product lifecycle media counters, not subjective/physical audio quality."""
import collections
import copy
import json
from pathlib import Path
import sys

def analyze(report):
    if report.get('error'):
        raise ValueError('Product flow failed; preserve failure report')
    phases={'baseline','muted','unmuted','peer-muted','peer-restored','reconnected','default-devices'}
    if {r['phase']for r in report['samples']} != phases:
        raise ValueError('Incomplete lifecycle phases')
    if len(report['muteLevels'])<5 or any(v>-119 for v in report['muteLevels']) or not any(v>-50 for v in report['activeLevels']):
        raise ValueError('PCM mute/unmute validation failed')
    sdk=report.get('sdkIceLog')
    if sdk is None or sdk['evicted']:
        raise ValueError('SDK error log unavailable/incomplete')
    groups=collections.defaultdict(list)
    for row in report['samples']:
        call=row['sample']; peers=call['Peers']
        if not call['InCall'] or len(peers)!=1:
            raise ValueError('Call/membership failed')
        p=peers[0]
        if p['State']!='connected' or p['Route']!='TURN 중계' or p['Audio']['DeviceError']:
            raise ValueError('TURN/device/connection failed')
        groups[row['phase'],p['Id']].append(p)
    windows=[]
    for (phase,peer),samples in groups.items():
        if len(samples)<2:
            raise ValueError('Insufficient media window')
        first,last=samples[0],samples[-1]
        tx=last['TxFrames']-first['TxFrames'];rx=last['RxPackets']-first['RxPackets']
        decoded=last['Audio']['DecodedMs']-first['Audio']['DecodedMs']
        if min(tx,rx,decoded)<=0:
            raise ValueError('Connected state without bidirectional decoded media')
        windows.append(dict(phase=phase,peer=peer,txFrames=tx,rxPackets=rx,decodedMs=decoded))
    return dict(role=report['role'],osMediaPorts=report.get('osMediaPorts'),windows=windows,
                allocationMismatchErrors=sum(e['Values'].get('ErrorCode')=='437'for e in sdk['entries']),
                limits='Per-peer per-phase counter progress. No physical latency, subjective quality or long-term network reliability claim.')

def self_test():
    def p(n):return dict(Id='p',State='connected',Route='TURN 중계',TxFrames=n,RxPackets=n,
                         Audio=dict(DeviceError=None,DecodedMs=n*10))
    phases=['baseline','muted','unmuted','peer-muted','peer-restored','reconnected','default-devices']
    report=dict(role='test',error=None,muteLevels=[-120]*5,activeLevels=[-20],sdkIceLog=dict(evicted=0,entries=[]),
                samples=[dict(phase=phase,sample=dict(InCall=True,Peers=[p(n)]))for phase in phases for n in (1,2)])
    assert len(analyze(report)['windows'])==7
    bad=copy.deepcopy(report);bad['samples'][1]['sample']['Peers'][0]['RxPackets']=1
    try:analyze(bad)
    except ValueError:pass
    else:raise AssertionError('Connected but stalled RTP accepted')
    print('PASS: lifecycle phases, PCM mute, TURN media progress and stalled-RTP rejection')

if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==3:
        source,output=map(Path,sys.argv[1:])
        if source.resolve()==output.resolve():raise ValueError('Preserve source')
        result=analyze(json.loads(source.read_text(encoding='utf-8-sig')))
        output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print('PASS:',result['role'],len(result['windows']),'media windows')
    else:raise SystemExit('Usage: analyze_product_smoke.py INPUT.json OUTPUT.json | --self-test')
