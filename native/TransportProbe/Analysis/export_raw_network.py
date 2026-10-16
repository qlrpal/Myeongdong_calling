"""Normalize tracerpt -rts UDPv4 metadata; preserve raw host QPC timestamps."""
import csv
import ipaddress
import json
import struct
import sys
from pathlib import Path


def frequency(data, clock):
    # EventTrace Header V2 layout emitted by this Windows tracerpt version.
    if len(data) != 22 or int(data[18], 0) != 1:
        raise ValueError("Unsupported ETW header/clock type; QPC required")
    if int(clock) == int(data[17]):
        raise ValueError("UTC-converted input: rerun tracerpt with -rts")
    value = int(data[16])
    if not 1_000_000 <= value <= 1_000_000_000_000:
        raise ValueError("Invalid QPC frequency")
    return value


def fields(data):
    if len(data) != 8:
        raise ValueError("Unexpected UDPv4 event schema")
    values = dict(zip(("PID", "size", "daddr", "saddr", "dport", "sport", "seqnum", "connid"), data))
    # Legacy XML exporter exposes raw little-endian address / network-order port integers.
    for name in ("saddr", "daddr"):
        values[name] = str(struct.unpack("<I", ipaddress.IPv4Address(values[name]).packed)[0])
    for name in ("sport", "dport"):
        value = int(values[name]); values[name] = str(((value & 255) << 8) | (value >> 8))
    return values


def self_test():
    data = ["0"] * 22; data[16] = "10000000"; data[17] = "134000000000000000"; data[18] = "0x1"
    assert frequency(data, "50000000000") == 10_000_000
    try:
        frequency(data, data[17])
        raise AssertionError("Converted UTC must be rejected")
    except ValueError:
        pass
    converted = fields(["123", "50", "172.30.1.88", "172.30.1.30", "60000", "50000", "0", "0"])
    assert struct.pack("<I", int(converted["saddr"])) == ipaddress.IPv4Address("172.30.1.30").packed
    assert int(converted["dport"]) == ((60000 & 255) << 8) | (60000 >> 8)
    print("PASS: raw QPC frequency, converted-UTC rejection, IPv4/port normalization")


def run(path, process_id, output):
    rows = []; seen = 0; freq = None; session = None
    with Path(path).open(encoding="utf-8-sig", newline="") as handle:
        reader = csv.reader(handle, skipinitialspace=True)
        header = [v.strip() for v in next(reader)]
        payload_at, clock_at = header.index("User Data"), header.index("Clock-Time")
        for row in reader:
            if not row:
                continue
            if row[0].strip() == "EventTrace" and row[1].strip() == "Header":
                if int(row[3]) != 2:
                    raise ValueError("Unsupported trace header version")
                freq = frequency(row[payload_at:], row[clock_at])
                session = row[payload_at + 20]
            if row[0].strip() != "Microsoft-Windows-Kernel-Network" or int(row[2]) not in (42, 43):
                continue
            if freq is None:
                raise ValueError("UDP events before QPC header")
            seen += 1
            values = fields(row[payload_at:])
            if int(values["PID"]) == process_id:
                rows.append(dict(id=int(row[2]), qpc=int(row[clock_at]) / freq, fields=values))
    if not rows:
        raise ValueError("No matching process UDPv4 records")
    Path(output).write_text(json.dumps(dict(processId=process_id, clockSource="ETW raw QPC", frequency=freq, sessionName=session,
        udpEventsSeen=seen, rows=rows), ensure_ascii=False, indent=2), encoding="utf-8")
    print("Exported", len(rows), "raw-QPC UDPv4 events, frequency", freq)


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    elif len(sys.argv) == 4:
        run(sys.argv[1], int(sys.argv[2]), sys.argv[3])
    else:
        raise SystemExit("Usage: export_raw_network.py RAW_CSV PROCESS_ID OUTPUT_JSON")
