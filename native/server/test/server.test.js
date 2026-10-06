import test from 'node:test';
import assert from 'node:assert/strict';
import { createVoiceServer } from '../server.js';

async function setup(t) {
  const server = createVoiceServer({ iceServers: [] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => { server.shutdown(); server.closeAllConnections(); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const post = async (path, data, headers = {}) => {
    const response = await fetch(`${base}${path}`, { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(data) });
    return { status: response.status, data: await response.json() };
  };
  const join = async (name, room = 'test-room') => {
    const response = await post('/api/join', { name, room });
    assert.equal(response.status, 200);
    return response.data;
  };
  const connect = async member => {
    const abort = new AbortController();
    t.after(() => abort.abort());
    const response = await fetch(`${base}/api/events?id=${member.id}&token=${member.token}`, { signal: abort.signal });
    assert.equal(response.status, 200);
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    return { abort, async next(event) {
      const deadline = setTimeout(() => abort.abort(), 3000);
      try {
        while (true) {
          const boundary = buffer.indexOf('\n\n');
          if (boundary >= 0) {
            const frame = buffer.slice(0, boundary); buffer = buffer.slice(boundary + 2);
            if (frame.startsWith(`event: ${event}\n`)) return JSON.parse(frame.split('\ndata: ')[1]);
            continue;
          }
          const chunk = await reader.read();
          assert.equal(chunk.done, false, `SSE ended before ${event}`);
          buffer += decoder.decode(chunk.value, { stream: true });
        }
      } finally { clearTimeout(deadline); }
    } };
  };
  return { base, post, join, connect };
}

test('serves health and ICE configuration without web assets', async t => {
  const { base } = await setup(t);
  const response = await fetch(`${base}/health`);
  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { ok: true, service: 'native-signaling' });
  assert.equal((await fetch(base)).status, 404);
  assert.equal((await fetch(`${base}/app.js`)).status, 404);
  assert.deepEqual(await (await fetch(`${base}/api/config`)).json(), { iceServers: [] });
  assert.equal((await fetch(`${base}/../server.js`)).status, 404);
});

test('joins, relays offer/answer/candidates, and notifies leave', async t => {
  const { post, join, connect } = await setup(t);
  const alice = await join('Alice');
  assert.deepEqual(alice.peers, []);
  const a = await connect(alice); await a.next('ready');
  const bob = await join('Bob');
  assert.deepEqual(bob.peers, [{ id: alice.id, name: 'Alice' }]);
  assert.deepEqual(await a.next('peer-joined'), { id: bob.id, name: 'Bob' });
  // Signal before the recipient subscribes: it must be queued, not dropped.
  const offer = { type: 'offer', sdp: 'test-offer' };
  assert.equal((await post('/api/signal', { ...alice, to: bob.id, signal: offer })).status, 200);
  const b = await connect(bob);
  assert.deepEqual(await b.next('signal'), { from: alice.id, name: 'Alice', signal: offer });
  await b.next('ready');
  for (const signal of [{ type: 'answer', sdp: 'test-answer' }, { type: 'candidate', candidate: { candidate: 'test-candidate' } }]) {
    assert.equal((await post('/api/signal', { ...bob, to: alice.id, signal })).status, 200);
    assert.deepEqual(await a.next('signal'), { from: bob.id, name: 'Bob', signal });
  }
  assert.equal((await post('/api/leave', bob)).status, 200);
  assert.deepEqual(await a.next('peer-left'), { id: bob.id });
  assert.equal((await post('/api/signal', { ...bob, to: alice.id, signal: offer })).status, 401);
  const replacement = await join('Bob again');
  assert.deepEqual(replacement.peers, [{ id: alice.id, name: 'Alice' }]);
});

test('isolates rooms and requires member credentials', async t => {
  const { base, post, join } = await setup(t);
  const alice = await join('Alice', 'room-a');
  const bob = await join('Bob', 'room-b');
  assert.deepEqual(bob.peers, []);
  const signal = { type: 'offer', sdp: 'test' };
  assert.equal((await post('/api/signal', { ...alice, to: bob.id, signal })).status, 404);
  assert.equal((await post('/api/leave', { id: alice.id, token: 'wrong' })).status, 401);
  assert.equal((await fetch(`${base}/api/events?id=${alice.id}&token=wrong`)).status, 401);
  assert.equal((await post('/api/join', { name: 'Mallory', room: 'room-a' }, { Origin: 'https://other.example' })).status, 403);
});

test('rejects invalid input and enforces six participants', async t => {
  const { post, join } = await setup(t);
  for (const data of [{ room: 'room', name: '' }, { room: '../room', name: 'Alice' }, { room: 'room', name: 'a'.repeat(25) }]) {
    assert.equal((await post('/api/join', data)).status, 400);
  }
  for (let i = 0; i < 6; i++) await join(`Member ${i}`);
  assert.equal((await post('/api/join', { name: 'Extra', room: 'test-room' })).status, 409);
});

test('removes participants when their event connection closes', async t => {
  const { join, connect } = await setup(t);
  const alice = await join('Alice');
  const a = await connect(alice); await a.next('ready');
  const bob = await join('Bob');
  await a.next('peer-joined');
  const b = await connect(bob); await b.next('ready');
  b.abort.abort();
  assert.deepEqual(await a.next('peer-left'), { id: bob.id });
  const charlie = await join('Charlie');
  assert.deepEqual(charlie.peers, [{ id: alice.id, name: 'Alice' }]);
});
