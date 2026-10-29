import test from 'node:test';
import assert from 'node:assert/strict';
import { summarizePair, expectedChecks } from './pair-summary.mjs';
function report(role) {
  return { schemaVersion: 1, completed: true, error: null, role, run: 'a'.repeat(32), route: 'direct', shared: false,
    buildHash: 'a'.repeat(64), probeHash: 'b'.repeat(64), fixtureHash: 'c'.repeat(64),
    hostFingerprint: (role === 'a' ? 'd' : 'e').repeat(64), sdk: { version: 'same' },
    checks: expectedChecks.map(name => ({ name, ok: true })) };
}
test('both completed matching reports are required', () => {
  assert.equal(summarizePair(report('a'), report('b')).status, 'pass');
  for (const patch of [{ error: 'failed' }, { completed: false }, { role: 'a' }, { checks: [] }, { run: 'b'.repeat(32) },
    { route: 'relay' }, { shared: true }, { observeSeconds: 60 }, { reserveMs: 15 }, { traceBaseline: true }, { buildHash: 'f'.repeat(64) }, { probeHash: '' }, { fixtureHash: '' }, { sdk: {} }])
    assert.equal(summarizePair(report('a'), { ...report('b'), ...patch }).status, 'fail');
  assert.equal(summarizePair(report('a'), null).status, 'fail');
});
test('same host cannot qualify as independent PCs', () => {
  const a = report('a'), b = report('b'); b.hostFingerprint = a.hostFingerprint;
  const result = summarizePair(a, b);
  assert.equal(result.functionalPassed, true); assert.equal(result.status, 'incomplete');
});
test('each host may select its own interface; keep both choices', () => {
  const a = { ...report('a'), mediaAddress: '192.0.2.10' }, b = { ...report('b'), mediaAddress: '192.0.2.11' };
  const result = summarizePair(a, b);
  assert.equal(result.status, 'pass'); assert.deepEqual(result.mediaAddresses, { a: '192.0.2.10', b: '192.0.2.11' });
});

test('requested trace requires bounded nonempty records on both sides', () => {
  const trace = { EvictedRecords: 0, Reads: [{}], Admissions: [{}], Receives: [{}] };
  const a = { ...report('a'), traceBaseline: true, observeSeconds: 30, baselineTrace: trace };
  const b = { ...report('b'), traceBaseline: true, observeSeconds: 30, baselineTrace: trace };
  assert.equal(summarizePair(a, b).status, 'pass');
  for (const baselineTrace of [null, { ...trace, EvictedRecords: 1 }, { ...trace, Reads: [] }])
    assert.equal(summarizePair(a, { ...b, baselineTrace }).status, 'fail');
  assert.equal(summarizePair({ ...a, observeSeconds: 46 }, { ...b, observeSeconds: 46 }).status, 'fail');
});
