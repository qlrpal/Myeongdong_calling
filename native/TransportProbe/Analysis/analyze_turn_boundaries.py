"""Decompose same-packet TURN interval changes without synchronizing host clocks."""
import json
import ipaddress
import struct
import sys
from pathlib import Path
from analyze_nic_rtp import stats
from turn_payload import rtp_payload


def udp_record(frame, link):
    if link==1:
        if len(frame)<14:return None
        offset=14 if frame[12:14]==b'\x08\x00' else 18 if frame[12:14]==b'\x81\x00' and frame[16:18]==b'\x08\x00' else -1
    elif link==113:offset=16 if len(frame)>=16 and frame[14:16]==b'\x08\x00' else -1
    elif link==276:offset=20 if len(frame)>=20 and frame[:2]==b'\x08\x00' else -1
    else:raise ValueError('Unsupported pcap link type')
    if offset<0 or len(frame)<offset+20 or frame[offset]>>4!=4 or frame[offset+9]!=17:return None
    ihl=(frame[offset]&15)*4
    if ihl<20 or int.from_bytes(frame[offset+6:offset+8],'big')&0x3fff:return None
    udp=offset+ihl
    if len(frame)<udp+8:return None
    sport,dport,length=struct.unpack_from('!HHH',frame,udp)
    raw=frame[udp+8:]
    channel=int.from_bytes(raw[:2],'big') if len(raw)>=4 and raw[0]&0xc0==0x40 else None
    body=rtp_payload(raw)
    if body is None:return None
    if len(body)<12 or body[0]>>6!=2 or body[1]&127!=111:return None
    seq,stamp,ssrc=struct.unpack_from('!HII',body,2)
    return (ssrc,stamp,seq),frame[offset+12:offset+16],frame[offset+16:offset+20],sport,dport,channel


def pcap_records(path):
    with Path(path).open('rb') as f:
        header=f.read(24)
        if len(header)!=24:raise ValueError('Short pcap')
        if header[:4]==bytes.fromhex('d4c3b2a1'):endian='<';scale=1e6
        elif header[:4]==bytes.fromhex('a1b2c3d4'):endian='>';scale=1e6
        elif header[:4]==bytes.fromhex('4d3cb2a1'):endian='<';scale=1e9
        else:raise ValueError('Unsupported pcap magic')
        major,minor,zone,sig,snap,link=struct.unpack(endian+'HHiIII',header[4:])
        if (major,minor)!=(2,4) or not 64<=snap<=128:raise ValueError('Unexpected pcap schema/snaplen')
        while True:
            record=f.read(16)
            if not record:break
            if len(record)!=16:raise ValueError('Short pcap record')
            sec,frac,count,original=struct.unpack(endian+'IIII',record)
            if count>snap or count>original:raise ValueError('Invalid captured length')
            payload=f.read(count)
            if len(payload)!=count:raise ValueError('Truncated pcap payload')
            yield sec+frac/scale,payload,link


def analyze(pcap,public_ip,source_path,receiver_path,output):
    source=json.loads(Path(source_path).read_text(encoding='utf-8-sig'));receiver=json.loads(Path(receiver_path).read_text(encoding='utf-8-sig'))
    if source['error'] is not None or receiver['error'] is not None or source['route']!='relay' or receiver['route']!='relay':raise ValueError('Complete forced-relay pair required')
    if Path(pcap).stat().st_size>64*1024*1024:raise ValueError('Capture exceeds declared bound')
    ingress={};egress={};public=ipaddress.IPv4Address(public_ip).packed;packets=0
    for at,frame,link in pcap_records(pcap):
        packets+=1;r=udp_record(frame,link)
        if r is None:continue
        key,src,dst,sport,dport,channel=r
        if src==public:ingress[key]=min(at,ingress.get(key,at))
        if dst==public:egress[key]=min(at,egress.get(key,at))
    sends={(r['Ssrc'],r['Timestamp']):r for r in source['trace']['Sends']}
    rx=sorted(receiver['trace']['Receives'],key=lambda r:r['At']);residence=[];matched=0;gaps=[]
    for r in rx:
        key=(r['Ssrc'],r['Timestamp'],r['Sequence'])
        if key in ingress and key in egress:
            matched+=1;residence.append((egress[key]-ingress[key])*1000)
    for a,b in zip(rx,rx[1:]):
        if (b['At']-a['At'])*1000<=30:continue
        ak=(a['Ssrc'],a['Timestamp'],a['Sequence']);bk=(b['Ssrc'],b['Timestamp'],b['Sequence'])
        sk=(a['Ssrc'],a['Timestamp']);tk=(b['Ssrc'],b['Timestamp'])
        row={'receiveGapMs':(b['At']-a['At'])*1000,'receiveAt':b['At'],'complete':all(k in ingress and k in egress for k in (ak,bk)) and sk in sends and tk in sends}
        if row['complete']:
            row|={'senderApiGapMs':(sends[tk]['SendStart']-sends[sk]['SendStart'])*1000,
                  'serverIngressGapMs':(ingress[bk]-ingress[ak])*1000,'serverEgressGapMs':(egress[bk]-egress[ak])*1000,
                  'serverBeforeResidenceMs':(egress[ak]-ingress[ak])*1000,'serverAfterResidenceMs':(egress[bk]-ingress[bk])*1000,
                  'senderCallBeforeMs':(sends[sk]['SendEnd']-sends[sk]['SendStart'])*1000,'senderCallAfterMs':(sends[tk]['SendEnd']-sends[tk]['SendStart'])*1000}
        gaps.append(row)
    out={'receiverProcessId':receiver['processId'],'receiverBuild':receiver['engineBuild'],'pcapPackets':packets,'receiverPackets':len(rx),'matchedServerBothEdges':matched,
         'matchPercent':100*matched/len(rx) if rx else 0,'serverResidenceMs':stats(residence),'gaps':gaps,
         'notes':'Intervals are within each host; clock offsets cancel but clock-rate errors are not calibrated. Pcap time is server software wall clock. Before-server expansion includes sender OS/socket and uplink; after-server expansion includes downlink/receiver OS/library. No NIC hardware or physical voice delay claim. Capture drop counters must be checked separately.'}
    Path(output).write_text(json.dumps(out,indent=2),encoding='utf-8');print(json.dumps(out,indent=2))


def self_test():
    rtp=bytes.fromhex('806f1234fffffffeabcdef01');udp=struct.pack('!HHHH',50000,3478,24,0)+b'\x40\x01\x00\x0c'+rtp
    ip=bytes.fromhex('4500002c0000000040110000afc435870a0000bd')
    eth=b'\0'*12+b'\x08\x00'+ip+udp
    r=udp_record(eth,1);assert r[0]==(0xabcdef01,0xfffffffe,0x1234) and r[-1]==0x4001
    assert udp_record(eth[:-1],1) is None
    assert udp_record(b'\x08\x00'+b'\0'*18+ip+udp,276)[0]==r[0]
    assert udp_record(b'\0'*14+b'\x08\x00'+ip+udp,113)[0]==r[0]
    bad=bytearray(eth);bad[42]=0;assert udp_record(bad,1) is None
    indication=b'\x00\x16\x00\x1c\x21\x12\xa4\x42'+b'\0'*12+b'\x00\x12\x00\x08'+b'\0'*8+b'\x00\x13\x00\x0c'+rtp
    packet=b'\0'*12+b'\x08\x00'+ip+struct.pack('!HHHH',50000,3478,len(indication)+8,0)+indication
    assert udp_record(packet,1)[0]==r[0]
    assert udp_record(packet[:-1],1) is None
    bad=bytearray(packet);bad[43]=1;assert udp_record(bad,1) is None
    print('PASS: TURN ChannelData/Indication RTP identity, truncation, Ethernet/Linux cooked headers and authentication rejection')


if __name__=='__main__':
    if sys.argv[1:]==['--self-test']:self_test()
    elif len(sys.argv)==6:analyze(*sys.argv[1:])
    else:raise SystemExit('PCAP PUBLIC_IPV4 SOURCE_REPORT RECEIVER_REPORT OUTPUT | --self-test')
