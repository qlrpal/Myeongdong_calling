import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizeObservation, compareObservations } from './pair-observation.mjs';
import { expectedChecks } from './pair-summary.mjs';
function report() {
  return { completed: true, error: null, role: 'a', shared: false, route: 'direct', observeSeconds: 5, windows: [{ phase: 'baseline',
    elapsedSeconds: 5, cpuCores: .1, allocatedBytes: 1048576, gcPauseMs: 1,
    samples: Array.from({ length: 6 }, (_, i) => ({ MonotonicSeconds: 10 + i, FrameMs: 10, PlaybackPolicy: 'LowLatency',
      Peers: [{ Id: 'same', State: 'connected', Route: '직접 연결', Audio: { QueueMs: i, UnderfillMs: i, RtpPlayout: { ConcealedMs: i * 10 } },
        Precision: { RtcpRttMs: 3, RttAgeSeconds: i === 5 ? 0 : i } }] })) }] };
}
test('summaries use counter deltas/audio time and distinct fresh RTCP, preserve missing data', () => {
  const r = summarizeObservation(report());
  assert.equal(r.queueMs.median, 2); assert.equal(r.queueMs.p95, 5);
  assert.equal(r.concealedMs, 50); assert.equal(r.concealedMsPerObservationPercent, 1);
  assert.equal(r.rtcpRttMs.samples, 2); assert.equal(r.latePackets, null);
  assert.equal(r.allocatedMiBPerSecond, .2);
});
test('reject failed/short/changing peer/counter reset/missing resources', () => {
  const bad = [];
  let r = report(); r.error = 'failed'; bad.push(r);
  r = report(); r.windows[0].samples.pop(); bad.push(r);
  r = report(); r.windows[0].samples[2].Peers[0].Id = 'other'; bad.push(r);
  r = report(); r.windows[0].samples[3].Peers[0].Audio.RtpPlayout.ConcealedMs = 0; bad.push(r);
  r = report(); delete r.windows[0].cpuCores; bad.push(r);
  for (const input of bad) assert.throws(() => summarizeObservation(input));
});
function pairs() {
  return Array.from({ length: 3 }, (_, trial) => ['a', 'b'].map(role => {
    const data = report();
    for (const sample of data.windows[0].samples) sample.SharedOutput = trial === 1 ? { Sources: 1, OutputStreams: 1 } : null;
    return { ...data, schemaVersion: 1, role,
    shared: trial === 1, run: String(trial + 1).repeat(32), buildHash: 'a'.repeat(64), probeHash: 'b'.repeat(64),
    fixtureHash: 'c'.repeat(64), hostFingerprint: (role === 'a' ? 'd' : 'e').repeat(64),
    checks: expectedChecks.map(name => ({ name, ok: true })) }; }));
}
test('A/B/A requires matched controls and never declares physical latency/quality', () => {
  const result = compareObservations(pairs());
  assert.equal(result.rows.length, 6); assert.equal(result.physicalLatencyMeasured, false); assert.equal(result.qualityQualified, false);
  let input = pairs(); input[1][0].shared = false; assert.throws(() => compareObservations(input));
  input = pairs(); input[2][1].windows[0].samples[2].FrameMs = 5; assert.throws(() => compareObservations(input));
  input = pairs(); input[2][1].mediaAddress = '192.0.2.10'; assert.throws(() => compareObservations(input));
  input = pairs(); input[1][0].windows[0].samples[2].SharedOutput = null; assert.throws(() => compareObservations(input));
});
test('reserve comparisons require actual application and reject unplanned knob changes', () => {
  const input = pairs();
  for (let i = 0; i < 3; i++) for (const r of input[i]) {
    r.shared = false; r.reserveMs = i === 1 ? 15 : 20;
    for (const s of r.windows[0].samples) { s.SharedOutput = null; s.Peers[0].Audio.RtpPlayout.TargetReserveMs = r.reserveMs; }
  }
  const result = compareObservations(input, 'reserve');
  assert.equal(result.factor, 'reserve'); assert.equal(result.qualityQualified, false);
  assert.equal(result.assessment[0].followUpEligible, false); // No queue improvement in identical samples.
  for (let i = 0; i < 3; i++) for (const r of input[i]) for (const [j, s] of r.windows[0].samples.entries()) {
    s.Sender = { DroppedFrames: 0 };
    s.Peers[0].Audio.Render = { EmptyEvents: 0 };
    s.Peers[0].Audio.QueueMs = (i === 1 ? 5 : 10) + j;
    Object.assign(s.Peers[0].Audio.RtpPlayout, { LatePackets: 0, Rebuffers: 0, InvalidPackets: 0 });
  }
  assert.equal(compareObservations(input, 'reserve').assessment[0].followUpEligible, true);
  input[1][0].windows[0].samples.at(-1).Peers[0].Audio.RtpPlayout.ConcealedMs += 10;
  assert.equal(compareObservations(input, 'reserve').assessment[0].followUpEligible, false);
  input[1][1].windows[0].samples[0].Peers[0].Audio.RtpPlayout.TargetReserveMs = 20;
  assert.throws(() => compareObservations(input, 'reserve'));
});
