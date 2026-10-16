"""Export only allowlisted WLAN metadata with host raw QPC, never UTC alignment."""
import csv
import io
import json
import re
import sys
from pathlib import Path
from export_raw_network import frequency

FIELDS = {
    14000: ('InterfaceGuid', 'InterfaceDescription', 'Connected'),
    14008: ('InterfaceGuid', 'InterfaceDescription', 'PowerSetting'),
    14011: ('InterfaceGuid', 'InterfaceDescription', 'PHY', 'SoftwareState', 'HardwareState', 'PHYCount'),
    14025: ('InterfaceGuid', 'InterfaceDescription', 'State'),
    14036: ('Setting',), 14040: ('InterfaceGuid', 'InterfaceDescription', 'ScanType', 'FlushBSSList'),
    14054: ('InterfaceGuid', 'InterfaceDescription', 'Enabled'),
    14067: ('IsOn',), 14068: ('IsEnabled',),
    20022: ('InterfaceGuid',), 20023: ('InterfaceGuid', 'Status'),
    14071: ('InterfaceGuid', 'ExpeditedScanTrigger'), 14072: ('InterfaceGuid', 'DisconnectTrigger'),
    30019: ('InterfaceGuid',),
}
LEGACY_FILTER = {14008,14036,14040,14054,14067,14068,20022,20023}


def parse(handle):
    reader = csv.reader(handle, skipinitialspace=True)
    header = [v.strip() for v in next(reader)]
    payload_at, clock_at = header.index('User Data'), header.index('Clock-Time')
    freq = None; rows = []
    for row in reader:
        if not row:
            continue
        if row[0].strip() == 'EventTrace' and row[1].strip() == 'Header':
            if int(row[3]) != 2:
                raise ValueError('Unsupported header version')
            freq = frequency(row[payload_at:], row[clock_at])
        if row[0].strip() != 'Microsoft-Windows-WLAN-AutoConfig':
            continue
        event_id = int(row[2]); values = row[payload_at:]
        if freq is None or event_id not in FIELDS or len(values) != len(FIELDS[event_id]):
            raise ValueError('Unexpected WLAN event or schema; do not analyze')
        rows.append(dict(id=event_id, at=int(row[clock_at]) / freq,
                         fields=dict(zip(FIELDS[event_id], values))))
    if freq is None:
        raise ValueError('Missing QPC trace header')
    return dict(clockSource='ETW raw QPC', frequency=freq, rows=rows,
                note='No events does not prove no firmware scan/power change. Scan start/completion is OS metadata, not radio airtime.')


def self_test():
    output = io.StringIO(); writer = csv.writer(output)
    writer.writerow(['Event Name', 'Type', 'Event ID', 'Version', 'Clock-Time', 'User Data'])
    data = ['0'] * 22; data[16] = '10000000'; data[17] = '134000000000000000'; data[18] = '0x1'
    writer.writerow(['EventTrace', 'Header', '0', '2', '50000000000', *data])
    writer.writerow(['Microsoft-Windows-WLAN-AutoConfig', '0', '20022', '0', '50010000000', '{interface}'])
    result = parse(io.StringIO(output.getvalue()))
    assert result['rows'][0]['at'] == 5001
    try:
        parse(io.StringIO(output.getvalue().replace('20022', '99999')))
        raise AssertionError('Unknown events must fail')
    except ValueError:
        pass
    metadata = dict(stopExitCode=0, eventIds=list(FIELDS))
    validate_capture(metadata, 'Total Events Lost 0')
    validate_capture(dict(metadata,eventIds=list(LEGACY_FILTER)), 'Total Events Lost 0')
    for bad_metadata, summary in ((dict(metadata, stopExitCode=1), 'Total Events Lost 0'),
                                   (metadata, 'Total Events Lost 1'), (metadata, ''),
                                   (dict(metadata,eventIds=list(FIELDS)+[40001]), 'Total Events Lost 0')):
        try:
            validate_capture(bad_metadata, summary)
            raise AssertionError('Incomplete/lost capture must fail')
        except ValueError:
            pass
    print('PASS: WLAN raw QPC, event/schema allowlist, stop status and loss rejection')


def validate_capture(metadata, summary):
    lost = re.search(r'Total Events\s+Lost\s+(\d+)', summary)
    if metadata.get('stopExitCode') != 0 or set(metadata.get('eventIds', [])) not in (LEGACY_FILTER,set(FIELDS)):
        raise ValueError('Incomplete capture or unexpected event filter')
    if not lost or int(lost[1]):
        raise ValueError('Missing or nonzero lost-event evidence')


if __name__ == '__main__':
    if sys.argv[1:] == ['--self-test']:
        self_test()
    elif len(sys.argv) == 5:
        metadata = json.loads(Path(sys.argv[2]).read_text(encoding='utf-8-sig'))
        validate_capture(metadata, Path(sys.argv[3]).read_text(encoding='utf-8-sig'))
        with Path(sys.argv[1]).open(encoding='utf-8-sig', newline='') as handle:
            result = parse(handle)
        if any(r['id'] not in metadata['eventIds'] for r in result['rows']):
            raise ValueError('Observed event outside recorded include filter')
        result['capture'] = dict(session=metadata['session'], seconds=metadata['seconds'], stopExitCode=0, eventsLost=0,
            startedQpc=metadata.get('captureStartedQpc'),endedQpc=metadata.get('captureAfterQpc'),eventIds=metadata['eventIds'])
        Path(sys.argv[4]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
        print('Exported WLAN events:', len(result['rows']))
    else:
        raise SystemExit('Usage: export_raw_wlan.py RAW_CSV CAPTURE_JSON SUMMARY_TXT OUTPUT_JSON')
