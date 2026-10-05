import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { randomUUID, timingSafeEqual } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const files = new Map([
  ['/', ['index.html', 'text/html; charset=utf-8']],
  ['/app.js', ['app.js', 'text/javascript; charset=utf-8']],
  ['/style.css', ['style.css', 'text/css; charset=utf-8']],
]);

export function createVoiceServer({ iceServers = [{ urls: 'stun:stun.l.google.com:19302' }] } = {}) {
  const members = new Map();
  function send(member, event, data) {
    const message = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
    if (member.stream) member.stream.write(message);
    else member.queue.push(message);
  }
  function others(member) {
    return [...members.values()].filter(other => other.room === member.room && other.id !== member.id);
  }
  function remove(member) {
    if (!members.delete(member.id)) return;
    clearTimeout(member.expiry);
    clearInterval(member.heartbeat);
    member.stream?.end();
    for (const other of others(member)) send(other, 'peer-left', { id: member.id });
  }
  function json(res, status, data) {
    res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify(data));
  }
  async function body(req) {
    let text = '';
    for await (const chunk of req) {
      text += chunk;
      if (Buffer.byteLength(text) > 65536) throw Object.assign(new Error('요청이 너무 큽니다.'), { status: 413 });
    }
    try { return JSON.parse(text); }
    catch { throw Object.assign(new Error('잘못된 요청입니다.'), { status: 400 }); }
  }
  function authorize(id, token) {
    const member = members.get(id);
    if (!member || typeof token !== 'string') return null;
    const a = Buffer.from(token), b = Buffer.from(member.token);
    return a.length === b.length && timingSafeEqual(a, b) ? member : null;
  }
  const server = http.createServer(async (req, res) => {
    try {
      const url = new URL(req.url, 'http://localhost');
      // Reject cross-origin mutations; microphone access is handled by the browser.
      if (req.method === 'POST' && req.headers.origin && new URL(req.headers.origin).host !== req.headers.host) {
        return json(res, 403, { error: '다른 사이트에서 요청할 수 없습니다.' });
      }
      if (req.method === 'GET' && files.has(url.pathname)) {
        const [name, type] = files.get(url.pathname);
        const content = await readFile(new URL(`./public/${name}`, import.meta.url));
        res.writeHead(200, { 'Content-Type': type, 'Cache-Control': 'no-cache', 'X-Content-Type-Options': 'nosniff', 'Referrer-Policy': 'no-referrer' });
        return res.end(content);
      }
      if (req.method === 'GET' && url.pathname === '/api/config') return json(res, 200, { iceServers });
      if (req.method === 'POST' && url.pathname === '/api/join') {
        const data = await body(req);
        const room = typeof data.room === 'string' ? data.room.trim() : '';
        const name = typeof data.name === 'string' ? data.name.trim() : '';
        if (!/^[\p{L}\p{N}_-]{1,40}$/u.test(room) || !name || name.length > 24) {
          return json(res, 400, { error: '방 이름은 문자·숫자·밑줄·하이픈 1~40자, 닉네임은 1~24자로 입력하세요.' });
        }
        const peers = [...members.values()].filter(member => member.room === room);
        if (peers.length >= 6) return json(res, 409, { error: '방이 가득 찼습니다. 최대 6명까지 입장할 수 있습니다.' });
        const member = { id: randomUUID(), token: randomUUID(), room, name, queue: [], stream: null };
        member.expiry = setTimeout(() => remove(member), 15000);
        members.set(member.id, member);
        for (const peer of peers) send(peer, 'peer-joined', { id: member.id, name });
        return json(res, 200, { id: member.id, token: member.token, peers: peers.map(({ id, name }) => ({ id, name })) });
      }
      if (req.method === 'GET' && url.pathname === '/api/events') {
        const member = authorize(url.searchParams.get('id'), url.searchParams.get('token'));
        if (!member) return json(res, 401, { error: '통화 세션이 만료되었습니다.' });
        if (member.stream) return json(res, 409, { error: '이미 연결된 세션입니다.' });
        clearTimeout(member.expiry);
        res.writeHead(200, { 'Content-Type': 'text/event-stream', 'Cache-Control': 'no-store', Connection: 'keep-alive', 'X-Accel-Buffering': 'no' });
        res.write(': connected\n\n');
        member.stream = res;
        for (const message of member.queue) res.write(message);
        member.queue = [];
        send(member, 'ready', {});
        member.heartbeat = setInterval(() => res.write(': heartbeat\n\n'), 10000);
        res.on('close', () => remove(member));
        return;
      }
      if (req.method === 'POST' && ['/api/signal', '/api/leave'].includes(url.pathname)) {
        const data = await body(req);
        const member = authorize(data.id, data.token);
        if (!member) return json(res, 401, { error: '통화 세션이 만료되었습니다.' });
        if (url.pathname === '/api/leave') {
          remove(member);
          return json(res, 200, { ok: true });
        }
        const target = members.get(data.to);
        if (!target || target.room !== member.room || target.id === member.id) return json(res, 404, { error: '참여자가 통화를 나갔습니다.' });
        if (!data.signal || typeof data.signal !== 'object' || !['offer', 'answer', 'candidate'].includes(data.signal.type)) {
          return json(res, 400, { error: '잘못된 통화 신호입니다.' });
        }
        send(target, 'signal', { from: member.id, name: member.name, signal: data.signal });
        return json(res, 200, { ok: true });
      }
      json(res, 404, { error: '찾을 수 없습니다.' });
    } catch (error) {
      if (!res.headersSent) json(res, error.status || 500, { error: error.status ? error.message : '서버 오류가 발생했습니다.' });
      else res.end();
    }
  });
  server.on('close', () => { for (const member of [...members.values()]) remove(member); });
  server.shutdown = () => { for (const member of [...members.values()]) remove(member); server.close(); };
  return server;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  let iceServers;
  try { iceServers = process.env.ICE_SERVERS ? JSON.parse(process.env.ICE_SERVERS) : undefined; }
  catch { console.error('ICE_SERVERS 환경 변수는 JSON 배열이어야 합니다.'); process.exit(1); }
  const server = createVoiceServer({ iceServers });
  const port = Number(process.env.PORT || 3000);
  server.listen(port, process.env.HOST || '127.0.0.1', () => console.log(`Voice Room: http://localhost:${port}`));
  process.on('SIGINT', () => server.shutdown());
  process.on('SIGTERM', () => server.shutdown());
}
