"""Build a payload-free schedule from validated same-tuple OS send metadata."""
import hashlib
import ipaddress
import json
import struct
import sys
from pathlib import Path
from analyze_windows_network import load_host


def port(value):
    value=int(value);return ((value&255)<<8)|(value>>8)


def ip(value):
    return str(ipaddress.IPv4Address(struct.pack('<I',int(value))))


def run(app_path,udp_path,anchor_path,summary_path,output_path):
    host=load_host(app_path,udp_path,anchor_path,summary_path)
    if host['metadata']['clock_source']!='ETW raw QPC':
        raise ValueError('Raw QPC required')
    app=host['app'];udp=json.loads(Path(udp_path).read_text(encoding='utf-8-sig'))
    peer=next(s['Peers'][0] for s in app['samples'] if s['Peers'][0]['Route']=='직접 연결')
    address,remote_port=peer['RemoteEndpoint'].rsplit(':',1)
    local_port=int(peer['LocalEndpoint'].rsplit(':',1)[1])
    epoch=min(s['SendStart'] for s in app['trace']['Sends'])
    items=[]
    for row in udp['rows']:
        f=row['fields'];at=row['qpc']-epoch
        if row['id']==42 and 0<=at<120 and ip(f['daddr'])==address and port(f['dport'])==int(remote_port) and port(f['sport'])==local_port:
            size=int(f['size'])
            if not 32<=size<=1200:
                raise ValueError('Out-of-bounds packet size; do not silently drop control traffic')
            items.append(dict(At=round(at,8),Bytes=size))
    items.sort(key=lambda x:x['At'])
    if not 10800<=len(items)<=24000 or items[-1]['At']<119:
        raise ValueError('Insufficient original OS schedule')
    result=dict(role=app['role'],seconds=120,items=items,
        source_app_sha256=hashlib.sha256(Path(app_path).read_bytes()).hexdigest(),
        source_udp_sha256=hashlib.sha256(Path(udp_path).read_bytes()).hexdigest(),
        note='All same-peer media-tuple OS send events in a 120s window, including control packets. Only relative time/length; no original packet or voice bytes. Each host epoch is independent; original bidirectional absolute phase is not preserved.')
    Path(output_path).write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    print(app['role'],'packets',len(items),'last',items[-1]['At'],'size range',min(i['Bytes'] for i in items),max(i['Bytes'] for i in items))


if __name__=='__main__':
    if len(sys.argv)==6:
        run(*sys.argv[1:])
    else:
        raise SystemExit('Usage: make_udp_pattern.py APP_JSON UDP_JSON ANCHOR_JSON SUMMARY_TXT OUTPUT_JSON')
