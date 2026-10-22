"""Join exact RTP boundary intervals; never subtract different hosts' absolute clocks."""
import json
import sys
from pathlib import Path


def combine(server,nic):
    if server['receiverProcessId']!=nic['processId'] or server['receiverPackets']!=nic['appReceiveCount']:
        raise ValueError('Receiver PID/trace coverage mismatch')
    gaps={r['receiveAt']:r for r in server['gaps'] if r['complete']};rows=[]
    for n in nic['gaps']:
        if not n['exactBoundaries'] or n['at'] not in gaps:continue
        s=gaps[n['at']]
        if abs(s['receiveGapMs']-n['appGapMs'])>0.001:raise ValueError('Different receive boundaries')
        rows.append({'receiveAt':n['at'],'senderApiGapMs':s['senderApiGapMs'],'serverIngressGapMs':s['serverIngressGapMs'],
                     'serverEgressGapMs':s['serverEgressGapMs'],'nicGapMs':n['nicGapMs'],'callbackGapMs':n['appGapMs'],
                     'apiToServerIntervalExpansionMs':s['serverIngressGapMs']-s['senderApiGapMs'],
                     'serverIntervalExpansionMs':s['serverEgressGapMs']-s['serverIngressGapMs'],
                     'serverToNicIntervalExpansionMs':n['nicGapMs']-s['serverEgressGapMs'],
                     'nicToCallbackIntervalExpansionMs':n['appGapMs']-n['nicGapMs']})
    return {'matchedGapPairs':len(rows),'receiverProcessId':nic['processId'],'nicMatchPercent':nic['receiveMatchPercent'],'rows':rows,
            'notes':'Signed interval changes for specific exact packet pairs, not absolute one-way delay. Partial NIC coverage forbids asserting absence of every interior packet; it does not prevent measuring interval change of two captured exact boundary packets. NIC is a software indication. No specific physical device, router or ISP attribution.'}


def self_test():
    server={'receiverProcessId':1,'receiverPackets':5,'gaps':[{'receiveAt':10.,'complete':True,'receiveGapMs':229.,'senderApiGapMs':9.,'serverIngressGapMs':11.,'serverEgressGapMs':10.}]}
    nic={'processId':1,'appReceiveCount':5,'receiveMatchPercent':80.,'gaps':[{'at':10.,'exactBoundaries':True,'appGapMs':229.,'nicGapMs':230.}]}
    row=combine(server,nic)['rows'][0]
    assert row['serverToNicIntervalExpansionMs']==220 and row['nicToCallbackIntervalExpansionMs']==-1
    assert sum(row[k] for k in ('apiToServerIntervalExpansionMs','serverIntervalExpansionMs','serverToNicIntervalExpansionMs','nicToCallbackIntervalExpansionMs'))==220
    nic['processId']=2
    try:combine(server,nic);raise AssertionError('Wrong receiver accepted')
    except ValueError:pass
    print('PASS: exact same-receiver interval joins, signed changes, partial-coverage limits and mismatch rejection')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==4:
        server,nic,output=map(Path,sys.argv[1:]);out=combine(json.loads(server.read_text(encoding='utf-8-sig')),json.loads(nic.read_text(encoding='utf-8-sig')))
        output.write_text(json.dumps(out,indent=2),encoding='utf-8');print(json.dumps(out,indent=2))
    else:raise SystemExit('SERVER_ANALYSIS NIC_ANALYSIS OUTPUT | --self-test')
