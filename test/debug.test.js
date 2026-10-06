import test from 'node:test';
import assert from 'node:assert/strict';
import { intervalStats } from '../public/debug-stats.js';
import { createVoiceServer } from '../server.js';

test('debug page and modules are served without exposing server source', async t => {
  const server = createVoiceServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => { server.shutdown(); server.closeAllConnections(); });
  const base = `http://127.0.0.1:${server.address().port}`;
  for (const path of ['/debug', '/debug.js', '/debug-source.js', '/debug-stats.js', '/debug.css']) {
    const response = await fetch(base + path);
    assert.equal(response.status, 200, path);
    assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
    assert.ok((await response.text()).length > 0);
  }
  assert.equal((await fetch(base + '/server.js')).status, 404);
});

test('diagnostics calculates interval values instead of lifetime averages', () => {
  const before = { timestamp: 1000, bytesReceived: 1000, jitterBufferDelay: 726652.8, jitterBufferTargetDelay: 672038.4,
    jitterBufferEmittedCount: 5230080, packetsReceived: 5565, packetsLost: 0, interruptionCount: 1, totalInterruptionDuration: 0.325 };
  const after = { timestamp: 2000, bytesReceived: 31000, jitterBufferDelay: 1014393.6, jitterBufferTargetDelay: 865152,
    jitterBufferEmittedCount: 10057920, packetsReceived: 10695, packetsLost: 0, interruptionCount: 1, totalInterruptionDuration: 0.325 };
  const result = intervalStats(after, before);
  assert.equal(result.bitrateKbps, 240);
  assert.ok(Math.abs(result.bufferMs - 59.600318) < 0.000001);
  assert.ok(Math.abs(result.targetMs - 40) < 0.000001);
  assert.equal(result.received, 5130);
  assert.equal(result.lossPercent, 0);
  assert.equal(result.interruptions, 0);
  assert.equal(result.interruptionMs, 0);
});

test('diagnostics does not invent zero for missing, first, or reset counters', () => {
  assert.equal(intervalStats({ timestamp: 1000, bytesReceived: 10 }).bitrateKbps, null);
  assert.equal(intervalStats({ timestamp: 2000, bytesReceived: 10 }, { timestamp: 1000, bytesReceived: 20 }).bitrateKbps, null);
  assert.equal(intervalStats({}, {}).interruptions, null);
  assert.equal(intervalStats({ packetsReceived: 100, packetsLost: 4 }, { packetsReceived: 2, packetsLost: 2 }).lossPercent, 2);
});
