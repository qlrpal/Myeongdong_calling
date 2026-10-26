"""Join exact RTP identities across sender QPC Tx and receiver capture intervals.

No absolute clock subtraction across hosts or from pcap epoch to QPC.
Software Tx/Rx observations are not hardware transmission/arrival times.
"""
import collections
import csv
import json
import sys
from decimal import Decimal
from pathlib import Path

from analyze_nic_rtp import parse_rtp
from analyze_packet_pair import trace
from export_raw_network import frequency


def key(packet):
    return packet["Ssrc"], packet["Timestamp"], packet["Sequence"]


def load_tx(path):
    packets = []
    freq = None
    with Path(path).open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.reader(handle, skipinitialspace=True)
        header = [v.strip() for v in next(reader)]
        payload_at, clock_at = header.index("User Data"), header.index("Clock-Time")
        for row in reader:
            if not row:
                continue
            if row[0].strip() == "EventTrace" and row[1].strip() == "Header":
                freq = frequency(row[payload_at:], row[clock_at])
            if row[0].strip() != "Microsoft-Windows-PktMon" or row[2].strip() != "160":
                continue
            data = row[payload_at:]
            if freq is None or row[3].strip() != "0" or len(data) != 13:
                raise ValueError("Unsupported raw-QPC Pktmon packet schema")
            if data[3].strip() != "Tx":
                continue
            raw = bytes.fromhex(data[12].strip().removeprefix("0x"))
            if len(raw) != int(data[11]):
                raise ValueError("Truncated export record")
            packet = parse_rtp(raw, packet_type=data[4].strip())
            if packet:
                packets.append(dict(Ssrc=packet["ssrc"], Timestamp=packet["timestamp"],
                                    Sequence=packet["sequence"], Component=int(data[5]),
                                    At=int(row[clock_at]) / freq))
    return packets


def load_rx(path, receiver):
    ports = {int(p["LocalEndpoint"].rsplit(":", 1)[1])
             for sample in receiver.get("samples", []) for p in sample["Peers"]}
    if not ports:
        raise ValueError("Receiver endpoint history required")
    packets = []
    with Path(path).open(encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            if int(row["udp.dstport"]) in ports:
                packets.append(dict(Ssrc=int(row["rtp.ssrc"], 0), Timestamp=int(row["rtp.timestamp"]),
                                    Sequence=int(row["rtp.seq"]), At=Decimal(row["frame.time_epoch"])))
    return packets


def compare(sender, receiver, tx_packets, rx_packets):
    source, destination = trace(sender), trace(receiver)
    sent = {}
    for packet in source["Sends"]:
        identity = packet["Ssrc"], packet["Timestamp"]
        if identity in sent:
            raise ValueError("Ambiguous sender RTP timestamp")
        sent[identity] = packet
    transmitted = collections.defaultdict(lambda: collections.defaultdict(list))
    captured = collections.defaultdict(list)
    for packet in tx_packets:
        transmitted[key(packet)][packet["Component"]].append(packet["At"])
    for packet in rx_packets:
        captured[key(packet)].append(packet["At"])
    streams = collections.defaultdict(list)
    observed = collections.Counter()
    for packet in destination["Receives"]:
        streams[packet["Ssrc"]].append(packet)
        observed[key(packet)] += 1
    if len(set(sent) & {identity[:2] for identity in observed}) < 2:
        raise ValueError("No overlapping source/receiver RTP boundaries; check direction and trace windows")
    components = sorted({c for p in transmitted.values() for c in p})
    coverage = {c: len({identity[:2] for identity, entries in transmitted.items()
                        if c in entries and identity[:2] in sent}) for c in components}
    matched = {identity for identity in observed if observed[identity] == 1 and len(captured[identity]) == 1}
    gaps = []
    for stream in streams.values():
        stream.sort(key=lambda p: p["At"])
        for a, b in zip(stream, stream[1:]):
            gap = (b["At"] - a["At"]) * 1000
            if gap <= 30:
                continue
            ka, kb = key(a), key(b)
            sa, sb = sent.get(ka[:2]), sent.get(kb[:2])
            event = dict(ssrc=b["Ssrc"], timestamp=b["Timestamp"], callbackGapMs=gap,
                         sourceIdentityMatched=bool(sa and sb),
                         receiverCaptureMatched=ka in matched and kb in matched, tx={})
            forward = sa and sb and sb["Sequence"] > sa["Sequence"]
            if sa and sb:
                event["sourceFrameStep"] = sb["Sequence"] - sa["Sequence"]
                if forward:
                    event["sourceApiGapMs"] = (sb["SendStart"] - sa["SendStart"]) * 1000
            if event["receiverCaptureMatched"]:
                event["receiverCaptureGapMs"] = float((captured[kb][0] - captured[ka][0]) * 1000)
            for component in components:
                if not forward or component not in transmitted[ka] or component not in transmitted[kb]:
                    continue
                left, right = transmitted[ka][component], transmitted[kb][component]
                tx_gap = (min(right) - min(left)) * 1000
                entry = dict(gapMs=tx_gap, firstPacketObservationSpanMs=(max(left) - min(left)) * 1000,
                             secondPacketObservationSpanMs=(max(right) - min(right)) * 1000,
                             firstApiStartToTxMs=(min(left) - sa["SendStart"]) * 1000,
                             secondApiStartToTxMs=(min(right) - sb["SendStart"]) * 1000)
                if event["receiverCaptureMatched"]:
                    entry["intervalGrowthAfterTxMs"] = event["receiverCaptureGapMs"] - tx_gap
                event["tx"][component] = entry
            gaps.append(event)
    return dict(sourceFrames=len(sent), matchedSourceFramesByComponent=coverage,
                receiverFrames=len(destination["Receives"]), uniquelyMatchedReceiverFrames=len(matched), gaps=gaps,
                limits="Exact positive packet matches only; missing Tx observations are not packet loss. "
                       "Each interval uses its own host/capture clock; comparable clock rates are assumed. "
                       "Tx-to-Rx interval growth is not absolute one-way latency. Multiple Tx observations retain spans. "
                       "ETW loss/circular cap/stop state, pcap drops and collector overhead require separate validation. "
                       "Only callback gaps over 30ms are listed; other PLC/output-underfill events remain unclassified.")


def self_test():
    def fixtures(source_offset=0, callback_offset=0, capture_offset=0):
        tx = [dict(Ssrc=7, Timestamp=n * 120, Sequence=n, Component=13, At=source_offset + n * .01 + .001)
              for n in range(2)]
        source = dict(Sends=[dict(Ssrc=7, Timestamp=n * 120, Sequence=n, SendStart=source_offset + n * .01)
                             for n in range(2)], Receives=[])
        receiver = dict(Sends=[], Receives=[dict(Ssrc=7, Timestamp=n * 120, Sequence=n, At=callback_offset + n * .13)
                                            for n in range(2)])
        capture = [dict(Ssrc=7, Timestamp=n * 120, Sequence=n,
                        At=Decimal(capture_offset) + Decimal(n) * Decimal(".13")) for n in range(2)]
        return source, receiver, tx, capture
    first = compare(*fixtures())["gaps"][0]
    shifted = compare(*fixtures(10000, -20000, 1800000000))["gaps"][0]
    assert abs(first["tx"][13]["intervalGrowthAfterTxMs"] - 120) < 1e-6
    assert abs(first["tx"][13]["intervalGrowthAfterTxMs"] - shifted["tx"][13]["intervalGrowthAfterTxMs"]) < 1e-6
    s, r, tx, rx = fixtures()
    assert not compare(s, r, tx[:1], rx)["gaps"][0]["tx"], "Missing endpoint became zero-delay evidence"
    rx.append(dict(rx[-1]))
    assert not compare(s, r, tx, rx)["gaps"][0]["receiverCaptureMatched"]
    s, r, tx, rx = fixtures()
    s["Sends"][1]["Sequence"] = -1
    assert not compare(s, r, tx, rx)["gaps"][0]["tx"], "Reordering became forward delay"
    s, r, tx, rx = fixtures()
    s["EvictedRecords"] = 1
    try:
        compare(s, r, tx, rx)
    except ValueError:
        pass
    else:
        raise AssertionError("Evicted trace accepted")
    print("PASS: exact identities, independent clock origins, missing/duplicate/reordered/evicted data")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 6:
        source, receiver, tx_csv, rx_csv, output = map(Path, sys.argv[1:])
        if output.resolve() in {p.resolve() for p in (source, receiver, tx_csv, rx_csv)}:
            raise ValueError("Preserve input data")
        left, right = [json.loads(p.read_text(encoding="utf-8-sig")) for p in (source, receiver)]
        result = compare(left, right, load_tx(tx_csv), load_rx(rx_csv, right))
        output.write_text(json.dumps(result, indent=2), encoding="utf-8")
        print("COMPARED", len(result["gaps"]), "large callback gaps;", result["matchedSourceFramesByComponent"])
    else:
        raise SystemExit("Usage: analyze_tx_rx_intervals.py SENDER.json RECEIVER.json TX-RAW.csv RX-RTP.csv OUTPUT.json | --self-test")
