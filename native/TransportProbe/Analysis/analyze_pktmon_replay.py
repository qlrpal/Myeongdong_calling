"""Analyze MCP-decoded Pktmon generic events by exact synthetic identity, no MAC output.

Run with the ETW MCP Python environment (pandas/parquet required). Does not modify MCP.
"""
import hashlib
import json
import re
import socket
import struct
import sys
from collections import Counter,defaultdict
from pathlib import Path
import pandas as pd
from analyze_pipeline import stats
from analyze_udp_call import load,compare,read
from analyze_udp_pattern import validate

TOKEN=hashlib.sha256(b'etw-mcp-054').digest()[:8]
IPS={'172.30.1.88':'pc','172.30.1.30':'laptop'}


def decode(data):
    candidates=[]
    for at in range(min(49,len(data))):
        if data[at]>>4!=4:continue
        ihl=(data[at]&15)*4
        if ihl<20 or at+ihl+8>len(data) or data[at+9]!=17:continue
        src=socket.inet_ntoa(data[at+12:at+16]);dst=socket.inet_ntoa(data[at+16:at+20])
        if src not in IPS or dst not in IPS or src==dst:continue
        if struct.unpack_from('!HH',data,at+ihl)!=(42000,42000):continue
        if struct.unpack_from('!H',data,at+6)[0]&0x1fff:continue
        total=struct.unpack_from('!H',data,at+2)[0];ident=struct.unpack_from('!H',data,at+4)[0]
        payload=data[at+ihl+8:];seq=None
        if len(payload)>=13:
            if payload[:8]!=TOKEN or payload[12]!=(1 if IPS[src]=='pc' else 2):
                raise ValueError('Captured flow does not contain the expected synthetic token/role')
            seq=struct.unpack_from('<I',payload,8)[0]
        candidates.append(dict(role=IPS[src],seq=seq,key=(src,dst,ident,total)))
    if len(candidates)>1:raise ValueError('Ambiguous IPv4 header')
    return candidates[0] if candidates else None


def packet_events(folder,low,high):
    meta=read(folder/'pktmon.etl.json')
    summary=(folder/'pktmon.summary.txt').read_text(encoding='utf-8-sig')
    lost=re.search(r'Total Events\s+Lost\s+(\d+)',summary)
    if meta['stopExitCode']!=0 or meta['captureBytes']!=64 or not lost or int(lost[1]) or (folder/'pktmon.etl').stat().st_size>=64*1024*1024:
        raise ValueError('Incomplete, lost or potentially wrapped Pktmon capture')
    cache=folder/'.etw-export-pktmon'
    freq=int(pd.read_parquet(cache/'trace_metadata.parquet').iloc[0]['PerfFreq'])
    df=pd.read_parquet(cache/'tracelogging_events.parquet')
    rows=[];lookup=defaultdict(set);counts=Counter()
    for row in df.itertuples():
        if row.ProviderName!='Microsoft-Windows-PktMon':continue
        counts[row.EventName]+=1
        if row.EventName!='EventID(160)':continue
        f=json.loads(row.FieldsJson);packet=decode(bytes.fromhex(f['Payload']))
        if packet is None:counts['unsupported_flow_header']+=1;continue
        packet.update(at=int(row.TimeStampQpc)/freq,component=f['ComponentId'],edge=f['EdgeId'],packet_type=f['PacketType'])
        if packet['seq'] is not None:lookup[packet['key']].add(packet['seq'])
        rows.append(packet)
    groups=defaultdict(list);ambiguous=0;direct=0;inferred=0
    for row in rows:
        if row['seq'] is None:
            choices=lookup[row['key']]
            if len(choices)!=1:ambiguous+=1;continue
            row['seq']=next(iter(choices));inferred+=1
        else:direct+=1
        if low<=row['at']<=high:groups[row['role'],row['seq']].append(row)
    summary={}
    for role in IPS.values():
        selected=[sorted(v,key=lambda r:r['at']) for (r,_),v in groups.items() if r==role]
        multi=[v for v in selected if len(v)>1]
        paths=Counter(' > '.join(f"{r['component']}:{r['edge']}:{r['packet_type']}" for r in v) for v in selected)
        summary[role]=dict(groups=len(selected),multi_point_groups=len(multi),
            visible_span_ms=stats((v[-1]['at']-v[0]['at'])*1000 for v in multi),
            paths=[dict(path=p,count=n) for p,n in paths.most_common(5)])
    return groups,dict(event_counts=dict(counts),qpc_frequency=freq,direct_identity_events=direct,
        ip_id_linked_events=inferred,ambiguous_ip_id_events_excluded=ambiguous,roles=summary)


def bounds(rows):
    if not rows:return None
    first=min(rows,key=lambda x:x['at']);last=max(rows,key=lambda x:x['at'])
    return dict(first=first['at'],last=last['at'],span_ms=(last['at']-first['at'])*1000,
        first_component=first['component'],last_component=last['component'],points=len(rows))


def run(pc_root,laptop_root,output):
    roots={'pc':Path(pc_root),'laptop':Path(laptop_root)}
    hosts={r:load(p,'replay1') for r,p in roots.items()};events={};summaries={}
    for r,h in hosts.items():
        plan_path=Path(__file__).resolve().parents[2]/'.local/udp-pattern-054'/f'{r}-plan.json'
        raw=plan_path.read_bytes();check=validate(h['app'],json.loads(raw.decode('utf-8-sig')),raw,'replay')
        if Counter(h['size_histogram'])!=Counter(x['Bytes'] for x in json.loads(raw.decode('utf-8-sig'))['items']):
            raise ValueError('OS send lengths do not match plan')
        events[r],summary=packet_events(roots[r]/'replay1',h['low'],h['high'])
        summaries[r]=dict(pktmon=summary,replay_check=check,controls=h['controls'])
    directions=[]
    for a,b in (('pc','laptop'),('laptop','pc')):
        d=compare(hosts[a],hosts[b],False);by_at={x['At']:x['Sequence'] for x in hosts[b]['app']['receives']}
        for g in d['gaps']:
            seq=by_at[g['target_received_at']];g['sequence']=seq
            for role,label in ((a,'source_pktmon'),(b,'receiver_pktmon')):
                end=bounds(events[role].get((a,seq),[]));prev=bounds(events[role].get((a,seq-1),[]))
                g[label]=dict(current=end,previous=prev,
                    first_gap_ms=(end['first']-prev['first'])*1000 if end and prev else None,
                    last_gap_ms=(end['last']-prev['last'])*1000 if end and prev else None)
        directions.append(d)
        print(d['direction'],d['gap_count'],d['categories'])
    result=dict(note='Raw same-host QPC from MCP generic events; exact synthetic seq/token on complete UDP header+payload. Truncated Wi-Fi rows linked only by unique src/dst/IP-ID/IP-length identity learned from exact rows; ambiguous keys excluded. Visible span is between observed components, not hardware airtime or full kernel residence. No cross-host absolute timestamp subtraction.',hosts=summaries,directions=directions)
    Path(output).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')


def self_test():
    ip=bytearray(20);ip[0]=0x45;ip[9]=17;ip[12:16]=socket.inet_aton('172.30.1.88');ip[16:20]=socket.inet_aton('172.30.1.30')
    struct.pack_into('!H',ip,2,41);struct.pack_into('!H',ip,4,15)
    packet=bytes(14)+ip+struct.pack('!HHHH',42000,42000,21,0)+TOKEN+struct.pack('<I',731)+b'\x01'
    assert decode(packet)['seq']==731 and decode(packet)['key'][2]==15
    assert decode(packet[:45])['seq'] is None
    try:decode(packet[:-1]+b'\x02');raise AssertionError('Wrong role must fail')
    except ValueError:pass
    print('PASS: Ethernet/IP offsets, exact synthetic identity, truncated header and role rejection')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==4:run(*sys.argv[1:])
    else:raise SystemExit('Usage: analyze_pktmon_replay.py PC_ROOT LAPTOP_ROOT OUTPUT_JSON')
