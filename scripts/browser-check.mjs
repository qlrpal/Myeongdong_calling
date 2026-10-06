// Local-only diagnostic server. Never deployed by npm start.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createVoiceServer } from '../server.js';

const server = createVoiceServer({ iceServers: [] });
const appHandler = server.listeners('request')[0];
server.removeAllListeners('request');
server.on('request', async (req, res) => {
  try {
    if (req.method === 'GET' && req.url.split('?')[0] === '/diagnostic') {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      return res.end(await readFile(new URL('./browser-check.html', import.meta.url)));
    }
    if (req.method === 'GET' && req.url.startsWith('/diagnostic-client')) {
      const html = await readFile(new URL('../public/index.html', import.meta.url), 'utf8');
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      const setup = `<script>
        window.diagnostic = { connections: [], streams: [], constraints: [] };
        const NativeConnection = window.RTCPeerConnection;
        window.RTCPeerConnection = class extends NativeConnection {
          constructor(config) { super(config); window.diagnostic.connections.push(this); }
        };
        navigator.mediaDevices.getUserMedia = async function(constraints) {
          window.diagnostic.constraints.push(constraints);
          const context = new AudioContext();
          const oscillator = context.createOscillator();
          const destination = context.createMediaStreamDestination();
          oscillator.frequency.value = 440;
          oscillator.connect(destination); oscillator.start();
          await context.resume();
          const stream = destination.stream;
          const track = stream.getAudioTracks()[0];
          const originalStop = track.stop.bind(track);
          track.stop = () => { originalStop(); oscillator.stop(); context.close(); };
          window.diagnostic.streams.push(stream);
          return stream;
        };
        // Keep the test tone inaudible while still decoding the incoming RTP audio.
        document.addEventListener('play', event => { if (event.target.tagName === 'AUDIO') event.target.volume = 0; }, true);
      </script>`;
      return res.end(html.replace('<script type="module"', `${setup}<script type="module"`));
    }
    if (req.method === 'POST' && req.url === '/diagnostic-results') {
      const chunks = [];
      let size = 0;
      for await (const chunk of req) { size += chunk.length; if (size > 16384) throw new Error('Report too large'); chunks.push(chunk); }
      const report = JSON.parse(Buffer.concat(chunks).toString());
      await mkdir(new URL('../artifacts/', import.meta.url), { recursive: true });
      await writeFile(new URL('../artifacts/compatibility-browser.json', import.meta.url), JSON.stringify(report, null, 2));
      res.writeHead(200, { 'Content-Type': 'application/json' });
      return res.end('{"ok":true}');
    }
    return appHandler(req, res);
  } catch (error) {
    if (!res.headersSent) res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end(error.message);
  }
});
server.listen(3001, '127.0.0.1', () => console.log('Browser diagnostic: http://localhost:3001/diagnostic'));
process.on('SIGINT', () => server.shutdown());
process.on('SIGTERM', () => server.shutdown());
