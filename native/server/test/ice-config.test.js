import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { createIceConfig } from '../ice-config.js';
import { createVoiceServer } from '../server.js';

test('TURN issues unique expiring HMAC credentials without disclosing the secret', async t => {
  const secret = 'test-only-secret-01234567890123456789';
  const provider = createIceConfig({ TURN_URLS: 'turn:example.com:3478?transport=udp', TURN_SECRET: secret });
  const server = createVoiceServer({ iceConfig: provider });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.shutdown());
  const response = await fetch(`http://127.0.0.1:${server.address().port}/api/config`);
  assert.equal(response.headers.get('cache-control'), 'no-store');
  const first = await response.json(), second = provider();
  const turn = first.iceServers[1];
  assert.deepEqual(turn.urls, ['turn:example.com:3478?transport=udp']);
  assert.notEqual(turn.username, second.iceServers[1].username);
  assert.equal(turn.credential, createHmac('sha1', secret).update(turn.username).digest('base64'));
  assert.equal(Number(turn.username.split(':')[0]), first.turnCredentialsExpireAt);
  assert.ok(Math.abs(first.turnCredentialsExpireAt - Date.now() / 1000 - 3600) < 2);
  assert.ok(!JSON.stringify(first).includes(secret));
});

test('ICE defaults remain compatible and invalid TURN configuration fails closed', () => {
  assert.deepEqual(createIceConfig({ ICE_SERVERS: '[]' })(), { iceServers: [] });
  assert.throws(() => createIceConfig({ ICE_SERVERS: '{}' }));
  assert.throws(() => createIceConfig({ TURN_URLS: 'https://example.com', TURN_SECRET: 'a'.repeat(32) }));
  assert.throws(() => createIceConfig({ TURN_URLS: 'turn:example.com', TURN_SECRET: 'short' }));
  assert.throws(() => createIceConfig({ TURN_URLS: 'turn:example.com', TURN_SECRET: 'a'.repeat(32), TURN_TTL_SECONDS: '0' }));
});
