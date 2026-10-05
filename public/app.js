const $ = id => document.getElementById(id);
const peers = new Map();
let session = null;
let stream = null;
let events = null;
let iceServers = [];
let joining = false;
let generation = 0;
let signalChain = Promise.resolve();
const initialRoom = new URL(location.href).searchParams.get('room');
if (initialRoom) $('room').value = initialRoom;

function status(message, error = false) {
  $('status').textContent = message;
  $('status').classList.toggle('error', error);
}
async function post(path, data) {
  const response = await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
  const result = await response.json();
  if (!response.ok) throw new Error(result.error || '요청에 실패했습니다.');
  return result;
}
function render() {
  $('participants').replaceChildren();
  const members = session ? [{ name: session.name, state: '나', self: true }, ...[...peers.values()].map(peer => ({ name: peer.name, state: peer.pc.connectionState }))] : [];
  const labels = { new: '연결 중', connecting: '연결 중', connected: '연결됨', disconnected: '연결 끊김', failed: '연결 실패', closed: '종료됨' };
  for (const member of members) {
    const li = document.createElement('li');
    const avatar = document.createElement('span');
    avatar.className = 'avatar'; avatar.textContent = Array.from(member.name)[0].toUpperCase();
    const name = document.createElement('span');
    name.className = 'member-name'; name.textContent = member.name;
    const state = document.createElement('span');
    state.className = `member-state${member.self || member.state === 'connected' ? ' connected' : ''}`;
    state.textContent = labels[member.state] || member.state;
    li.append(avatar, name, state); $('participants').append(li);
  }
  $('count').textContent = `${members.length} / 6`;
  $('waiting').hidden = peers.size > 0;
}
function send(to, signal) {
  if (!session) return Promise.resolve();
  return post('/api/signal', { id: session.id, token: session.token, to, signal });
}
function makePeer(id, name) {
  if (peers.has(id)) return peers.get(id);
  const pc = new RTCPeerConnection({ iceServers });
  const audio = document.createElement('audio');
  audio.autoplay = true;
  $('audio-container').append(audio);
  const peer = { pc, audio, name, candidates: [] };
  peers.set(id, peer);
  for (const track of stream.getAudioTracks()) pc.addTrack(track, stream);
  pc.onicecandidate = ({ candidate }) => {
    if (candidate) send(id, { type: 'candidate', candidate: candidate.toJSON() }).catch(error => {
      if (peers.get(id) === peer && session) status(error.message, true);
    });
  };
  pc.ontrack = ({ streams }) => {
    audio.srcObject = streams[0];
    audio.play().catch(() => { if (session && peers.get(id) === peer) $('resume').hidden = false; });
  };
  pc.onconnectionstatechange = () => {
    if (peers.get(id) !== peer) return;
    render();
    if (pc.connectionState === 'failed') status('상대방과 연결하지 못했습니다. 다시 입장해 주세요. 다른 네트워크에서는 중계 서버가 필요할 수 있습니다.', true);
    else if (pc.connectionState === 'connected') status('통화가 연결되었습니다.');
  };
  render();
  return peer;
}
async function offer(id, name) {
  const peer = makePeer(id, name);
  await peer.pc.setLocalDescription(await peer.pc.createOffer());
  await send(id, { type: 'offer', sdp: peer.pc.localDescription.sdp });
}
async function receive({ from, name, signal }) {
  const peer = makePeer(from, name);
  if (signal.type === 'candidate') {
    if (peer.pc.remoteDescription) await peer.pc.addIceCandidate(signal.candidate);
    else peer.candidates.push(signal.candidate);
    return;
  }
  await peer.pc.setRemoteDescription({ type: signal.type, sdp: signal.sdp });
  for (const candidate of peer.candidates.splice(0)) await peer.pc.addIceCandidate(candidate);
  if (signal.type === 'offer') {
    await peer.pc.setLocalDescription(await peer.pc.createAnswer());
    await send(from, { type: 'answer', sdp: peer.pc.localDescription.sdp });
  }
}
function removePeer(id) {
  const peer = peers.get(id);
  if (!peer) return;
  peers.delete(id);
  peer.pc.close(); peer.audio.srcObject = null; peer.audio.remove();
  render();
}
function leave(message = '통화를 종료했습니다. 마이크가 꺼졌습니다.') {
  generation++;
  const old = session;
  session = null;
  events?.close(); events = null;
  for (const id of [...peers.keys()]) removePeer(id);
  stream?.getTracks().forEach(track => track.stop()); stream = null;
  signalChain = Promise.resolve();
  $('call').hidden = true; $('join-form').hidden = false; $('resume').hidden = true;
  $('badge').textContent = '입장 전'; $('badge').classList.remove('active');
  if (old) {
    const data = JSON.stringify({ id: old.id, token: old.token });
    fetch('/api/leave', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: data, keepalive: true }).catch(() => {});
  }
  status(message);
}
function enqueue(task, version) {
  signalChain = signalChain.then(() => { if (session && generation === version) return task(); }).catch(error => {
    if (session && generation === version) status(`통화 연결 오류: ${error.message}`, true);
  });
}
$('join-form').addEventListener('submit', async event => {
  event.preventDefault();
  if (joining || session) return;
  joining = true; $('join').disabled = true;
  const version = ++generation;
  try {
    if (!navigator.mediaDevices?.getUserMedia) throw new Error('마이크를 사용하려면 localhost 또는 HTTPS 주소에서 접속하세요.');
    const name = $('name').value.trim(), room = $('room').value.trim();
    if (!name || !/^[\p{L}\p{N}_-]{1,40}$/u.test(room)) throw new Error('닉네임과 방 이름을 확인하세요. 방 이름에는 문자·숫자·밑줄·하이픈을 사용할 수 있습니다.');
    status('마이크 접근을 허용해 주세요.');
    const acquired = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false }, video: false });
    if (version !== generation) { acquired.getTracks().forEach(track => track.stop()); return; }
    stream = acquired;
    for (const track of stream.getAudioTracks()) track.onended = () => leave('마이크 연결이 끊겼습니다. 장치를 확인하고 다시 입장하세요.');
    status('통화방에 입장하고 있습니다.');
    const configResponse = await fetch('/api/config');
    if (!configResponse.ok) throw new Error('통화 설정을 불러오지 못했습니다.');
    iceServers = (await configResponse.json()).iceServers;
    if (version !== generation) return;
    const result = await post('/api/join', { name, room });
    if (version !== generation) {
      await post('/api/leave', { id: result.id, token: result.token }); return;
    }
    session = { ...result, name, room };
    const departed = new Set();
    $('join-form').hidden = true; $('call').hidden = false;
    $('room-title').textContent = room; $('badge').textContent = '통화 중'; $('badge').classList.add('active');
    const url = new URL(location.href); url.searchParams.set('room', room); history.replaceState(null, '', url);
    render();
    events = new EventSource(`/api/events?id=${encodeURIComponent(result.id)}&token=${encodeURIComponent(result.token)}`);
    events.addEventListener('ready', () => {
      status(result.peers.length ? '참여자와 연결하고 있습니다.' : '입장했습니다. 다른 참여자를 기다리고 있습니다.');
      for (const peer of result.peers) enqueue(() => { if (!departed.has(peer.id)) return offer(peer.id, peer.name); }, version);
    });
    events.addEventListener('peer-joined', event => {
      if (generation !== version || !session) return;
      const peer = JSON.parse(event.data); makePeer(peer.id, peer.name);
    });
    events.addEventListener('peer-left', event => {
      const { id } = JSON.parse(event.data);
      departed.add(id);
      enqueue(() => { removePeer(id); if (!peers.size) status('다른 참여자를 기다리고 있습니다.'); }, version);
    });
    events.addEventListener('signal', event => {
      const data = JSON.parse(event.data);
      enqueue(() => { if (!departed.has(data.from)) return receive(data); }, version);
    });
    events.onerror = () => { if (generation === version) leave('서버 연결이 끊겼습니다. 다시 입장해 주세요.'); };
  } catch (error) {
    if (version === generation) {
      leave();
      const messages = { NotAllowedError: '마이크 접근이 거부되었습니다. 브라우저의 마이크 권한을 허용해 주세요.', NotFoundError: '마이크를 찾을 수 없습니다. 마이크를 연결해 주세요.', NotReadableError: '마이크를 사용할 수 없습니다. 다른 앱이나 장치 설정을 확인하세요.' };
      status(messages[error.name] || error.message, true);
    }
  } finally { joining = false; $('join').disabled = false; }
});
$('leave').addEventListener('click', () => leave());
$('resume').addEventListener('click', async () => {
  const results = await Promise.allSettled([...peers.values()].filter(peer => peer.audio.srcObject).map(peer => peer.audio.play()));
  $('resume').hidden = results.every(result => result.status === 'fulfilled');
});
window.addEventListener('pagehide', () => leave());
