"""Compare metadata-only playout-target runs. Uses only receiver-local clocks."""
import json
import sys
from pathlib import Path
from analyze_pipeline import key, stats


def summarize(report):
    trace = report['trace']
    span = report['samples'][-1]['MonotonicSeconds'] - report['samples'][0]['MonotonicSeconds'] if report['samples'] else 0
    # A peer may finish its 120s trace just before this peer takes its last sample.
    # Retain the original error and analyze only the interior window; never waive an early disconnect.
    boundary_error = (report.get('error') or '').startswith('System.InvalidOperationException: Peer disconnected during pipeline trace.') and span >= 118
    if (report.get('error') and not boundary_error) or trace['EvictedRecords']:
        raise ValueError('Incomplete/evicted trace cannot support comparison')
    receives = sorted(trace['Receives'], key=lambda row: row['At'])
    if not receives:
        raise ValueError('No received packets')
    played = {key(row): row for row in trace['Playout'] if row['Outcome'] == 'played'}
    start, end = receives[0]['At'], receives[-1]['At']
    windows = {}
    for label, trim in [('after5s', 5), ('after60s', 60)]:
        low, high = start + trim, end - 1
        packets = [row for row in receives if low <= row['At'] <= high]
        reads = [row for row in trace['Reads'] if low <= row['CalledAt'] <= high]
        samples = [row for row in report['samples'] if low <= row['MonotonicSeconds'] <= high and len(row['Peers']) == 1 and row['Peers'][0]['State'] == 'connected']
        delays = [(played[key(row)]['DecodeStart'] - row['At']) * 1000 for row in packets if key(row) in played]
        audio = [row['Peers'][0]['Audio'] for row in samples]
        details = [row['RtpPlayout'] for row in audio]
        def difference(field, rows):
            return rows[-1][field] - rows[0][field] if len(rows) >= 2 else None
        windows[label] = dict(
            duration_seconds=round(high - low, 3),
            receive_to_decode_ms=stats(delays),
            read_queue_before_ms=stats(row['QueueBeforeMs'] for row in reads),
            queue_after_read_ms=stats(row.get('QueueAfterReadMs') for row in details),
            clock_target_ms=stats(row.get('ClockTargetMs') for row in details),
            filtered_queue_ms=stats(row.get('ClockFilteredQueueMs') for row in details),
            read_request_ms=stats(row['RequestedSamples'] / 48 for row in reads),
            receive_interval_ms=stats((b['At'] - a['At']) * 1000 for a, b in zip(packets, packets[1:])),
            plc_ms=difference('ConcealedMs', details),
            late_packets=difference('LatePackets', details),
            rebuffers=difference('Rebuffers', details),
            underruns=difference('Underruns', audio),
            underfill_ms=difference('UnderfillMs', audio),
            device_restarts=difference('DeviceRestarts', audio),
            device_errors=sorted({row['DeviceError'] for row in audio if row.get('DeviceError')}),
            counter_window_seconds=round(samples[-1]['MonotonicSeconds'] - samples[0]['MonotonicSeconds'], 3) if len(samples) >= 2 else None)
    final = next(row['Peers'][0]['Audio'] for row in reversed(report['samples']) if len(row['Peers']) == 1)
    return dict(role=report['role'], fixed_target=report['fixedReserveClockTarget'],
        transient_hold=report.get('transientEmptyQueueHold', False), startup_recorded=report.get('traceIncludesStartup', False),
        fresh_restart=report.get('freshRestart', False),
        generated_at=report['generatedAt'], boundary_error=report.get('error'), recorder_max_ms=trace['MaxRecorderMs'],
        device=final['Device'], format=final['DeviceFormat'], engine_period_ms=final['EnginePeriodMs'],
        low_latency_active=final['LowLatencyActive'], windows=windows)


if __name__ == '__main__':
    if len(sys.argv) < 3:
        raise SystemExit('Usage: analyze_playout_target.py OUTPUT_JSON INPUT_JSON...')
    result = dict(notes='Receiver-local receive callback to decode; not mouth-to-ear latency. Counter differences use available 1s samples inside each window. Sequential trials have different network conditions.',
        runs=[summarize(json.loads(Path(path).read_text(encoding='utf-8-sig'))) for path in sys.argv[2:]])
    Path(sys.argv[1]).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    for row in result['runs']:
        w = row['windows']['after60s']
        print(row['role'], 'fixed=' + str(row['fixed_target']), 'delay', w['receive_to_decode_ms'],
              'PLC', w['plc_ms'], 'late', w['late_packets'], 'rebuffer', w['rebuffers'])
