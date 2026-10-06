import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const source = await readFile(new URL('../public/app.js', import.meta.url), 'utf8');
const settle = async () => { for (let i = 0; i < 5; i++) await new Promise(resolve => setImmediate(resolve)); };

function browser({ microphoneError, joinError } = {}) {
  const elements = new Map();
  const calls = [], connections = [], eventSources = [];
  const track = { kind: 'audio', stopped: false, stop() { this.stopped = true; } };
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
    addTrack(track) {
      this.track = track;
      this.sender = { track, getParameters: () => ({ encodings: [{}] }), setParameters: async parameters => { this.parameters = parameters; } };
    }
    getSenders() { return [this.sender]; }
    async createOffer() { return { type: 'offer', sdp: 'v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111 0\r\na=rtpmap:111 opus/48000/2\r\na=fmtp:111 minptime=10;useinbandfec=1\r\na=rtpmap:0 PCMU/8000\r\n' }; }
    async createAnswer() { return { type: 'answer', sdp: (await this.createOffer()).sdp }; }
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
  return { $, calls, track, connections, eventSources, context, get constraints() { return constraints; }, join: () => $('join-form').handlers.submit({ preventDefault() {} }) };
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
  assert.match(pc.localDescription.sdp, /a=fmtp:111 minptime=10;useinbandfec=1;maxaveragebitrate=510000/);
  events.emit('signal', { from: 'alice', name: 'Alice', signal: { type: 'candidate', candidate: { candidate: 'early' } } });
  await settle(); assert.equal(pc.candidates.length, 0);
  events.emit('signal', { from: 'alice', name: 'Alice', signal: { type: 'answer', sdp: 'remote-answer' } });
  await settle(); assert.equal(pc.candidates[0].candidate, 'early');
  assert.equal(pc.parameters.encodings[0].maxBitrate, 510000);
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
  assert.equal(pc.parameters.encodings[0].maxBitrate, 510000);
  assert.match(pc.localDescription.sdp, /maxaveragebitrate=510000/);
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

test('Opus bitrate negotiation replaces old values and preserves other media', () => {
  const app = browser();
  app.context.description = { type: 'offer', sdp: 'v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 109\r\na=rtpmap:109 opus/48000/2\r\na=fmtp:109 useinbandfec=1;maxaveragebitrate=16000\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 VP8/90000\r\na=fmtp:96 maxaveragebitrate=12345\r\n' };
  const result = vm.runInContext('withOpusBitrate(description)', app.context);
  assert.match(result.sdp, /a=fmtp:109 useinbandfec=1;maxaveragebitrate=510000\r\n/);
  assert.match(result.sdp, /a=fmtp:96 maxaveragebitrate=12345\r\n/);
  app.context.description = result;
  assert.equal(vm.runInContext('withOpusBitrate(description).sdp', app.context), result.sdp);
});

test('Opus bitrate negotiation handles a missing fmtp without changing other codecs', () => {
  const app = browser();
  app.context.description = { type: 'answer', sdp: 'v=0\nm=audio 9 UDP/TLS/RTP/SAVPF 112 0\na=rtpmap:112 opus/48000/2\na=rtpmap:0 PCMU/8000\n' };
  const result = vm.runInContext('withOpusBitrate(description)', app.context);
  assert.match(result.sdp, /a=rtpmap:112 opus\/48000\/2\na=fmtp:112 maxaveragebitrate=510000\n/);
  assert.match(result.sdp, /a=rtpmap:0 PCMU\/8000\n/);
});

test('unsupported bitrate parameters do not prevent answering the call', async () => {
  const app = browser(); await app.join();
  const events = app.eventSources[0];
  events.emit('peer-joined', { id: 'charlie', name: 'Charlie' });
  app.connections[0].sender.setParameters = async () => { throw new Error('unsupported'); };
  events.emit('signal', { from: 'charlie', name: 'Charlie', signal: { type: 'offer', sdp: 'remote-offer' } });
  await settle();
  assert.equal(app.calls.find(call => call.path === '/api/signal').data.signal.type, 'answer');
  assert.match(app.$('status').textContent, /기본 설정/);
  app.$('leave').handlers.click();
});

test('sets the audio receiver buffer hint in milliseconds', async () => {
  const app = browser(); await app.join();
  app.eventSources[0].emit('peer-joined', { id: 'alice', name: 'Alice' });
  const receiver = { track: { kind: 'audio' }, jitterBufferTarget: null };
  const media = { getTracks: () => [] };
  app.connections[0].ontrack({ streams: [media], receiver });
  assert.equal(receiver.jitterBufferTarget, 20);
  assert.equal(app.$('audio-container').children[0].srcObject, media);
  app.$('leave').handlers.click();
});

test('does not invent a buffer API on unsupported receivers or change video', () => {
  const app = browser();
  const receiver = { track: { kind: 'audio' } };
  app.context.receiver = receiver;
  assert.equal(vm.runInContext('configureAudioReceiver(receiver)', app.context), false);
  assert.equal('jitterBufferTarget' in receiver, false);
  app.context.receiver = { track: { kind: 'video' }, jitterBufferTarget: null };
  assert.equal(vm.runInContext('configureAudioReceiver(receiver)', app.context), false);
  assert.equal(app.context.receiver.jitterBufferTarget, null);
});

test('buffer setter rejection keeps incoming audio playable', async () => {
  const app = browser(); await app.join();
  app.eventSources[0].emit('peer-joined', { id: 'alice', name: 'Alice' });
  const receiver = { track: { kind: 'audio' } };
  Object.defineProperty(receiver, 'jitterBufferTarget', { get: () => null, set() { throw new Error('unsupported'); } });
  const media = { getTracks: () => [] };
  assert.doesNotThrow(() => app.connections[0].ontrack({ streams: [media], receiver }));
  assert.equal(app.$('audio-container').children[0].srcObject, media);
  app.$('leave').handlers.click();
});

test('live buffer controls apply zero, persist for new peers, and reset after leave', async () => {
  const app = browser(); await app.join();
  app.eventSources[0].emit('peer-joined', { id: 'alice', name: 'Alice' });
  const receiver = { track: { kind: 'audio' }, jitterBufferTarget: null };
  app.connections[0].ontrack({ streams: [{}], receiver });
  for (const value of [10, 5, 0, 20, 0]) {
    const result = vm.runInContext(`setAudioBufferTarget(${value})`, app.context);
    assert.equal(result.applied, 1);
    assert.equal(receiver.jitterBufferTarget, value);
  }
  app.eventSources[0].emit('peer-joined', { id: 'bob', name: 'Bob' });
  const newReceiver = { track: { kind: 'audio' }, jitterBufferTarget: null };
  app.connections[1].ontrack({ streams: [{}], receiver: newReceiver });
  assert.equal(newReceiver.jitterBufferTarget, 0);
  assert.ok(vm.runInContext('setAudioBufferTarget(-1)', app.context).error);
  assert.equal(receiver.jitterBufferTarget, 0);
  app.$('leave').handlers.click();
  assert.ok(vm.runInContext('setAudioBufferTarget(5)', app.context).error);
  assert.equal(vm.runInContext('audioBufferTargetMs', app.context), 20);
});

test('live buffer control reports unsupported receivers without interrupting playback', async () => {
  const app = browser(); await app.join();
  app.eventSources[0].emit('peer-joined', { id: 'alice', name: 'Alice' });
  const receiver = { track: { kind: 'audio' } }, media = {};
  app.connections[0].ontrack({ streams: [media], receiver });
  const result = vm.runInContext('setAudioBufferTarget(0)', app.context);
  assert.equal(result.total, 1);
  assert.equal(result.applied, 0);
  assert.equal('jitterBufferTarget' in receiver, false);
  assert.equal(app.$('audio-container').children[0].srcObject, media);
  assert.equal(app.connections[0].closed, undefined);
  app.$('leave').handlers.click();
});

test('packet duration SDP only changes Opus audio, replaces old values, and is idempotent', () => {
  const app = browser();
  app.context.description = { type: 'offer', sdp: 'v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=rtpmap:111 opus/48000/2\r\na=ptime:20\r\na=maxptime:60\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=ptime:30\r\n' };
  const result = vm.runInContext('withAudioPacketTime(description)', app.context);
  assert.match(result.sdp, /a=ptime:10\r\na=maxptime:10/);
  assert.match(result.sdp, /m=video[^]*a=ptime:30/);
  assert.doesNotMatch(result.sdp, /a=maxptime:60/);
  app.context.description = result;
  assert.equal(vm.runInContext('withAudioPacketTime(description).sdp', app.context), result.sdp);
});

test('packet duration rejection falls back to a playable negotiated call', async () => {
  const app = browser(); await app.join();
  app.eventSources[0].emit('peer-joined', { id: 'alice', name: 'Alice' });
  const pc = app.connections[0];
  const original = pc.setLocalDescription.bind(pc);
  pc.setLocalDescription = async description => {
    if (description.sdp.includes('a=ptime:')) throw new Error('unsupported');
    await original(description);
  };
  await vm.runInContext("offer('alice', 'Alice')", app.context);
  assert.match(pc.localDescription.sdp, /maxaveragebitrate=510000/);
  assert.doesNotMatch(pc.localDescription.sdp, /a=ptime:/);
  assert.equal(pc.closed, undefined);
  app.$('leave').handlers.click();
});
