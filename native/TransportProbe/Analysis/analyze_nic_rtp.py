"""Exact RTP identity matching at a same-host Pktmon NIC boundary. No cross-host clock subtraction."""
import csv
import ipaddress
import json
import struct
import sys
from collections import Counter
from pathlib import Path
from export_raw_network import frequency
from turn_payload import rtp_payload


def parse_rtp(prefix, packet_type=None):
    """Ethernet/VLAN, Wi-Fi SNAP, or explicitly declared Pktmon raw IP.

    A raw IP header is accepted only when the event declares PacketType=IP;
    never guess it from a MAC address's first nibble.
    """
    snap = prefix.find(bytes.fromhex('aaaa030000000800'), 0, 64)
    if packet_type == 'IP':
        offset = 0
    elif snap >= 0:
        offset = snap + 8
    elif len(prefix) >= 14 and prefix[12:14] == b'\x08\x00':
        offset = 14
    elif len(prefix) >= 18 and prefix[12:14] in (b'\x81\x00', b'\x88\xa8') and prefix[16:18] == b'\x08\x00':
        offset = 18
    else:
        return None
    if len(prefix) < offset + 20 or prefix[offset] >> 4 != 4:
        return None
    ihl = (prefix[offset] & 15) * 4
    if ihl < 20 or prefix[offset + 9] != 17 or int.from_bytes(prefix[offset + 6:offset + 8], 'big') & 0x3fff:
        return None  # Fragmented datagrams are not reassembled by this analyzer.
    udp = offset + ihl
    if len(prefix) < udp + 20 or int.from_bytes(prefix[udp + 4:udp + 6], 'big') < 20:
        return None
    payload = rtp_payload(prefix[udp + 8:])
    if payload is None or len(payload)<12 or payload[0]>>6!=2 or payload[1]&127!=111:
        return None
    seq, timestamp, ssrc = struct.unpack_from('!HII', payload, 2)
    return dict(src=str(ipaddress.IPv4Address(prefix[offset + 12:offset + 16])),
                dst=str(ipaddress.IPv4Address(prefix[offset + 16:offset + 20])),
                sport=int.from_bytes(prefix[udp:udp + 2], 'big'), dport=int.from_bytes(prefix[udp + 2:udp + 4], 'big'),
                sequence=seq, timestamp=timestamp, ssrc=ssrc)


def stats(values):
    x = sorted(values)
    return None if not x else {'count': len(x), 'p50': x[int((len(x)-1)*.5)], 'p95': x[int((len(x)-1)*.95)], 'max': x[-1], 'min': x[0]}


def analyze(csv_path, report_path, metadata_path, summary_path, output):
    report = json.loads(Path(report_path).read_text(encoding='utf-8-sig'))
    meta = json.loads(Path(metadata_path).read_text(encoding='utf-8-sig'))
    summary = Path(summary_path).read_text(encoding='utf-8-sig')
    import re
    if not re.search(r'Total Events\s+Lost\s+0\b', summary):
        raise ValueError('Missing zero-loss ETW summary')
    etl = Path(metadata_path).with_suffix('')
    if meta['stopExitCode'] != 0 or not etl.exists() or etl.stat().st_size >= meta['maximumMiB'] * 1024 * 1024:
        raise ValueError('Capture not cleanly stopped, missing, or reached its circular cap')
    if report['processId'] != meta['processId']  or report['error'] is not None:
        raise ValueError('Wrong host/report/PID or trial failure')
    local_ip = str(ipaddress.IPv4Address(report['mediaAddress']))
    remote_ips = {s['Peers'][0]['RemoteEndpoint'].rsplit(':',1)[0] for s in report['samples']}
    relay = report['route'] == 'relay'
    pairs = {(int(s['Peers'][0]['RemoteEndpoint'].rsplit(':',1)[1]), int(s['Peers'][0]['LocalEndpoint'].rsplit(':',1)[1])) for s in report['samples']}
    incoming = {}; outgoing = {}; components = Counter(); freq = None; clocks = []; retained = 0
    with Path(csv_path).open(encoding='utf-8-sig', newline='') as handle:
        reader = csv.reader(handle, skipinitialspace=True); h = [x.strip() for x in next(reader)]
        payload_at, clock_at = h.index('User Data'), h.index('Clock-Time')
        for row in reader:
            if not row: continue
            if row[0].strip() == 'EventTrace' and row[1].strip() == 'Header':
                freq = frequency(row[payload_at:], row[clock_at])
            if freq is None: continue
            if row[0].strip() != 'Microsoft-Windows-PktMon' or row[2].strip() != '160': continue
            if row[3].strip() != '0' or len(row[payload_at:]) != 13:
                raise ValueError('Unsupported Pktmon Event160 V0 schema')
            data = row[payload_at:]; at = int(row[clock_at])/freq; clocks.append(at)
            if int(data[11]) != len(bytes.fromhex(data[12].strip().removeprefix('0x'))):
                raise ValueError('LoggedPayloadSize mismatch')
            packet = parse_rtp(bytes.fromhex(data[12].strip().removeprefix('0x')), packet_type=data[4].strip())
            if packet is None: continue
            retained += 1; components[data[5].strip()] += 1
            key = (packet['ssrc'], packet['timestamp'], packet['sequence'])
            if packet['src'] in remote_ips and packet['dst'] == local_ip and data[3].strip() == 'Rx' and (relay or (packet['sport'],packet['dport']) in pairs):
                incoming[key] = min(incoming.get(key, at), at)
            elif packet['src'] == local_ip and packet['dst'] in remote_ips and data[3].strip() == 'Tx' and (relay or (packet['dport'],packet['sport']) in pairs):
                key = (packet['ssrc'], packet['timestamp'])
                outgoing[key] = min(outgoing.get(key, at), at)
    receives = sorted(report['trace']['Receives'], key=lambda r:r['At'])
    if not clocks or not receives or min(clocks) > receives[0]['At'] or max(clocks) < receives[-1]['At']:
        raise ValueError('NIC capture does not cover the retained app trace')
    matches=[]; gaps=[]
    for receive in receives:
        key=(receive['Ssrc'],receive['Timestamp'],receive['Sequence'])
        if key in incoming: matches.append((receive,incoming[key]))
    for a,b in zip(receives,receives[1:]):
        appgap=(b['At']-a['At'])*1000
        if appgap<=30:continue
        ak=(a['Ssrc'],a['Timestamp'],a['Sequence']);bk=(b['Ssrc'],b['Timestamp'],b['Sequence'])
        if ak not in incoming or bk not in incoming:
            gaps.append({'appGapMs':appgap,'exactBoundaries':False});continue
        left,right=incoming[ak],incoming[bk]
        interior=sum(left<t<right and key[0]==a['Ssrc'] for key,t in incoming.items())
        gaps.append({'appGapMs':appgap,'exactBoundaries':True,'nicGapMs':(right-left)*1000,
                     'beforeResidualMs':(a['At']-left)*1000,'afterResidualMs':(b['At']-right)*1000,
                     'interiorSameSsrcPackets':interior,'at':b['At']})
    sender=[(outgoing[(s['Ssrc'],s['Timestamp'])]-s['SendStart'])*1000 for s in report['trace']['Sends'] if (s['Ssrc'],s['Timestamp']) in outgoing]
    out={'processId':report['processId'],'frequency':freq,'etlBytes':etl.stat().st_size,'eventsLost':0,
         'components':dict(components),'retainedRtpObservations':retained,'appReceiveCount':len(receives),'matchedReceives':len(matches),'receiveMatchPercent':100*len(matches)/len(receives),'completeForGapAttribution':len(matches)==len(receives),
         'nicToCallbackMs':stats([(r['At']-at)*1000 for r,at in matches]),'senderApiToNicMs':stats(sender),'gaps':gaps,
         'notes':'Exact clear RTP base-header identities; NIC timestamps are software indications, not hardware wire times. Incomplete matching forbids attributing packet absence to the NIC boundary; zero ETW event loss does not prove per-datagram capture completeness. Sender API delta is same-host; negative values are preserved. Only Opus PT111/unfragmented IPv4; TURN Send/Data Indications and ChannelData are unwrapped without decrypting SRTP. Direct ports are verified; relay uses outer server IP and exact RTP identity because relay candidate ports differ from TURN transport ports. No physical mouth-to-ear claim.'}
    if not matches:raise ValueError('No exact RTP matches')
    Path(output).write_text(json.dumps(out,indent=2),encoding='utf-8'); print(json.dumps(out,indent=2))


def self_test():
    rtp=bytes.fromhex('806f1234fffffffeabcdef01')
    udp=struct.pack('!HHHH',50000,50001,20,0)+rtp
    ip=bytes.fromhex('450000280000000040110000ac1e0158ac1e011e')
    eth=b'\0'*12+b'\x08\x00'+ip+udp
    wifi=b'\0'*26+bytes.fromhex('aaaa030000000800')+ip+udp
    for x in (eth,wifi):
        p=parse_rtp(x);assert p['sequence']==0x1234 and p['timestamp']==0xfffffffe and p['ssrc']==0xabcdef01
    assert parse_rtp(wifi[:54]) is None
    assert parse_rtp(wifi[:73]) is None
    assert parse_rtp(ip+udp, packet_type='IP')['timestamp'] == 0xfffffffe
    assert parse_rtp(ip+udp) is None
    assert parse_rtp((ip+udp)[:-1], packet_type='IP') is None
    assert parse_rtp(b'\x60'+(ip+udp)[1:], packet_type='IP') is None
    bad=bytearray(eth);bad[14+6]=0x20;assert parse_rtp(bad) is None
    assert parse_rtp(b'\0'*128) is None
    options=bytearray(ip);options[0]=0x46
    assert parse_rtp(b'\0'*12+b'\x08\x00'+options+b'\0'*4+udp)['sequence']==0x1234
    indication=b'\x00\x16\x00\x1c\x21\x12\xa4\x42'+b'\0'*12+b'\x00\x12\x00\x08'+b'\0'*8+b'\x00\x13\x00\x0c'+rtp
    turn_udp=struct.pack('!HHHH',50000,3478,len(indication)+8,0)+indication
    turn_wifi=b'\0'*26+bytes.fromhex('aaaa030000000800')+ip+turn_udp
    assert parse_rtp(turn_wifi)['sequence']==0x1234
    assert parse_rtp(turn_wifi[:109]) is None
    assert stats([]) is None and stats([3,1,2])['p50']==2
    print('PASS: native Wi-Fi/Ethernet RTP identity, truncation, IPv4 options, fragments, unsupported packets, statistics')


if __name__ == '__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:analyze(*sys.argv[1:])
    else:raise SystemExit('CSV REPORT ETL.json SUMMARY OUTPUT | --self-test')
