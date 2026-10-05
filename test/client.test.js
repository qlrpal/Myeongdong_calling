import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../public/app.js', import.meta.url), 'utf8');
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };

function browser({ microphoneError, joinError } = {}) {
  const elements = new Map();
  const calls = [], connections = [], eventSources = [];
  const track = { stopped: false, stop() { this.stopped = true; } };
  const media = { getAudioTracks: () => [track], getTracks: () => [track] };
  let constraints;
  function element() {
    return {
      value: '', hidden: false, children: [], handlers: {}, textContent: '',
      classList: { toggle() {}, add() {}, remove() {} },
      addEventListener(event, handler) { this.handlers[event] = handler; },
      append(...children) { this.children.push(...children); },
      replaceChildren() { this.children = []; }, remove() { this.removed = true; },
      play() { return Promise.resolve(); },
    };
  }
  const document = { getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); }, createElement: element };
  class FakeEvents {
    constructor(url) { this.url = url; this.handlers = {}; eventSources.push(this); }
    addEventListener(event, handler) { this.handlers[event] = handler; }
    emit(event, data = {}) { this.handlers[event]?.({ data: JSON.stringify(data) }); }
    close() { this.closed = true; }
  }
  class FakeConnection {
    constructor() { this.connectionState = 'new'; this.candidates = []; connections.push(this); }
    addTrack(track) { this.track = track; }
    async createOffer() { return { type: 'offer', sdp: 'local-offer' }; }
    async createAnswer() { return { type: 'answer', sdp: 'local-answer' }; }
    async setLocalDescription(description) { this.localDescription = description; }
    async setRemoteDescription(description) { this.remoteDescription = description; }
    async addIceCandidate(candidate) { assert.ok(this.remoteDescription, 'ICE must wait for remote description'); this.candidates.push(candidate); }
    close() { this.closed = true; }
  }
  const context = vm.createContext({
    document, URL, location: { href: 'http://localhost:3000/' }, history: { replaceState() {} },
    navigator: { mediaDevices: { async getUserMedia(options) { constraints = options; if (microphoneError) throw microphoneError; return media; } } },
    RTCPeerConnection: FakeConnection, EventSource: FakeEvents,
    window: { addEventListener() {} },
    async fetch(path, options) {
      const data = options?.body ? JSON.parse(options.body) : null;
      calls.push({ path, data });
      if (path === '/api/join' && joinError) return { ok: false, json: async () => ({ error: joinError }) };
      const result = path === '/api/config' ? { iceServers: [] } : path === '/api/join' ? { id: 'self', token: 'token', peers: [{ id: 'alice', name: 'Alice' }] } : { ok: true };
      return { ok: true, json: async () => result };
    },
  });
  vm.runInContext(source, context);
  const $ = document.getElementById.bind(document);
  $('name').value = 'Bob'; $('room').value = 'room';
  return { $, calls, track, connections, eventSources, get constraints() { return constraints; }, join: () => $('join-form').handlers.submit({ preventDefault() {} }) };
}

test('client negotiates audio, buffers early ICE, and releases resources on leave', async () => {
  const app = browser();
  await app.join();
  assert.equal(app.constraints.audio.echoCancellation, false);
  assert.equal(app.constraints.audio.noiseSuppression, false);
  assert.equal(app.constraints.audio.autoGainControl, false);
  assert.equal(app.constraints.video, false);
  assert.equal(app.$('call').hidden, false);
  const events = app.eventSources[0];
  events.emit('ready'); await settle();
  const pc = app.connections[0];
  assert.equal(pc.track, app.track);
  assert.equal(app.calls.find(call => call.path === '/api/signal').data.signal.type, 'offer');
  events.emit('signal', { from: 'alice', name: 'Alice', signal: { type: 'candidate', candidate: { candidate: 'early' } } });
  await settle(); assert.equal(pc.candidates.length, 0);
  events.emit('signal', { from: 'alice', name: 'Alice', signal: { type: 'answer', sdp: 'remote-answer' } });
  await settle(); assert.equal(pc.candidates[0].candidate, 'early');
  pc.connectionState = 'connected'; pc.onconnectionstatechange();
  assert.equal(app.$('status').textContent, '통화가 연결되었습니다.');
  app.$('leave').handlers.click();
  assert.equal(pc.closed, true);
  assert.equal(app.track.stopped, true);
  assert.equal(events.closed, true);
  assert.equal(app.$('call').hidden, true);
  assert.equal(app.calls.at(-1).path, '/api/leave');
});

test('client closes microphone when joining fails', async () => {
  const app = browser({ joinError: '방이 가득 찼습니다.' });
  await app.join();
  assert.equal(app.track.stopped, true);
  assert.equal(app.$('status').textContent, '방이 가득 찼습니다.');
  assert.equal(app.$('join').disabled, false);
  assert.equal(app.eventSources.length, 0);
});

test('client handles denied microphone access without joining', async () => {
  const app = browser({ microphoneError: Object.assign(new Error('denied'), { name: 'NotAllowedError' }) });
  await app.join();
  assert.match(app.$('status').textContent, /마이크 접근이 거부/);
  assert.equal(app.calls.length, 0);
  assert.equal(app.$('join').disabled, false);
});

test('client answers incoming peers and removes departed connections', async () => {
  const app = browser();
  await app.join();
  const events = app.eventSources[0];
  events.emit('peer-joined', { id: 'charlie', name: 'Charlie' });
  events.emit('signal', { from: 'charlie', name: 'Charlie', signal: { type: 'offer', sdp: 'remote-offer' } });
  await settle();
  assert.equal(app.calls.find(call => call.path === '/api/signal').data.signal.type, 'answer');
  const pc = app.connections[0];
  events.emit('peer-left', { id: 'charlie' }); await settle();
  assert.equal(pc.closed, true);
  app.$('leave').handlers.click();
});

test('server disconnect releases microphone and permits another join', async () => {
  const app = browser(); await app.join();
  const events = app.eventSources[0];
  events.onerror();
  assert.equal(app.track.stopped, true);
  assert.equal(events.closed, true);
  assert.match(app.$('status').textContent, /서버 연결이 끊겼습니다/);
  await app.join();
  assert.equal(app.eventSources.length, 2);
  app.$('leave').handlers.click();
});

test('does not connect to a peer who leaves before the event stream is ready', async () => {
  const app = browser(); await app.join();
  const events = app.eventSources[0];
  events.emit('peer-left', { id: 'alice' });
  events.emit('ready');
  events.emit('signal', { from: 'alice', name: 'Alice', signal: { type: 'candidate', candidate: { candidate: 'late' } } });
  await settle();
  assert.equal(app.connections.length, 0);
  assert.equal(app.$('count').textContent, '1 / 6');
  app.$('leave').handlers.click();
});
