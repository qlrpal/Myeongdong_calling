// Fault-injection server for localhost lifecycle checks; production start does
// not load this file or its control routes. No request URLs/tokens are logged.
import { createVoiceServer } from '../../server/server.js';
const server = createVoiceServer({ iceServers: [] });
const original = server.listeners('request')[0];
const streams = new Set();
let configFail = false, hangLeave = false;
server.removeAllListeners('request');
server.on('request', async (req, res) => {
  const path = new URL(req.url, 'http://localhost').pathname;
  if (req.method === 'POST' && path === '/__test/flags') {
    let text = ''; for await (const chunk of req) { text += chunk; if (text.length > 1024) { res.writeHead(413).end(); return; } }
    try { const data = JSON.parse(text); configFail = data.configFail === true; hangLeave = data.hangLeave === true; }
    catch { res.writeHead(400).end(); return; }
    res.writeHead(200, { 'Content-Type': 'application/json' }).end('{}'); return;
  }
  if (req.method === 'POST' && path === '/__test/drop') {
    for (const stream of streams) stream.destroy();
    res.writeHead(200, { 'Content-Type': 'application/json' }).end('{}'); return;
  }
  if (configFail && path === '/api/config') { res.writeHead(503).end(); return; }
  if (hangLeave && path === '/api/leave') { req.resume(); return; }
  if (path === '/api/events') { streams.add(res); res.on('close', () => streams.delete(res)); }
  void original(req, res);
});
server.listen(0, '127.0.0.1', () => console.log(server.address().port));
process.on('SIGTERM', () => { for (const stream of streams) stream.destroy(); server.shutdown(); });
