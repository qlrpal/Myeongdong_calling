"""Extract only the visible media prefix from TURN ChannelData or Send/Data Indications."""
import struct


def rtp_payload(data):
    if len(data)>=20 and data[:2] in (b'\x00\x16',b'\x00\x17') and data[4:8]==b'\x21\x12\xa4\x42':
        limit=20+int.from_bytes(data[2:4],'big');cursor=20
        while cursor+4<=min(limit,len(data)):
            kind,size=struct.unpack_from('!HH',data,cursor);cursor+=4
            if kind==0x0013:return data[cursor:min(cursor+size,len(data))]
            cursor+=(size+3)&~3
        return None
    if len(data)>=4 and data[0]&0xc0==0x40:
        size=int.from_bytes(data[2:4],'big')
        return data[4:4+size] if size>=12 else None
    return data if len(data)>=12 and data[0]>>6==2 else None
