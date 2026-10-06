const $ = id => document.getElementById(id);
const tabs = new Map();
const channel = 'BroadcastChannel' in window ? new BroadcastChannel('voice-room-diagnostics-v1') : null;
let latest = null;
let pendingBuffer = null;
const bufferButtons = [...document.querySelectorAll('[data-buffer]')];
const format = (value, unit = '', digits = 1) => Number.isFinite(value) ? `${value.toFixed(digits)}${unit}` : '—';
function metric(list, title, value) {
  const box = document.createElement('div'); box.className = 'metric';
  const dt = document.createElement('dt'); dt.textContent = title;
  const dd = document.createElement('dd'); dd.textContent = value; box.append(dt, dd); list.append(box);
}
function render() {
  const selected = $('call-tab').value;
  for (const [id, snapshot] of tabs) if (Date.now() - snapshot.timestamp > 5000) tabs.delete(id);
  $('call-tab').replaceChildren();
  for (const [id, snapshot] of tabs) {
    const option = document.createElement('option'); option.value = id;
    option.textContent = `${snapshot.room || '입장 전'} · 탭 ${id.slice(0, 6)}`; $('call-tab').append(option);
  }
  if (tabs.has(selected)) $('call-tab').value = selected;
  latest = tabs.get($('call-tab').value) || null;
  $('download').disabled = !latest;
  for (const button of bufferButtons) {
    button.disabled = !latest?.room || !latest.bufferControl || !!pendingBuffer;
    button.setAttribute('aria-pressed', String(!!latest?.room && Number(button.dataset.buffer) === latest.requestedBufferMs));
  }
  $('connections').replaceChildren();
  $('debug-status').textContent = latest ? `마지막 갱신 ${new Date(latest.timestamp).toLocaleTimeString()} · 상대 ${latest.peerCount}명 · 최근 약 1초 구간` : '통화 탭을 기다리고 있습니다. 통화 페이지를 새로고침하고 열어 두세요.';
  if (!latest?.rows.length) {
    const empty = document.createElement('div'); empty.className = 'panel empty';
    empty.textContent = latest?.room ? '상대방의 음성 수신을 기다리고 있습니다.' : '통화 탭에서 입장하면 상대별 통계가 표시됩니다.'; $('connections').append(empty); return;
  }
  for (const row of latest.rows) {
    const card = document.createElement('article'); card.className = 'panel';
    const heading = document.createElement('div'); heading.className = 'peer-heading';
    const name = document.createElement('h2'); name.textContent = row.name;
    const state = document.createElement('span'); state.className = 'badge'; state.textContent = row.state;
    heading.append(name, state); card.append(heading);
    if (row.error) { const error = document.createElement('p'); error.textContent = row.error; card.append(error); }
    else {
      const list = document.createElement('dl'); list.className = 'metrics';
      metric(list, '수신 음성 비트레이트', format(row.bitrateKbps, ' kbps'));
      metric(list, '수신 패킷 빈도', format(row.packetsPerSecond, '개/s'));
      metric(list, '평균 수신 간격 추정', format(row.packetCadenceMs, ' ms'));
      metric(list, '패킷 간격 요청', format(latest.requestedPacketTimeMs, ' ms', 0));
      metric(list, '실제 버퍼 대기', format(row.bufferMs, ' ms'));
      metric(list, '브라우저 목표 버퍼', format(row.targetMs, ' ms'));
      metric(list, '수신기 설정값', row.receiverSupported ? format(row.receiverTarget, ' ms') : '미지원');
      metric(list, '앱 요청 버퍼', format(latest.requestedBufferMs, ' ms', 0));
      metric(list, '네트워크 RTT', format(row.rttMs, ' ms'));
      metric(list, '지터', format(row.jitterMs, ' ms'));
      metric(list, '구간 패킷 손실', format(row.lossPercent, '%', 2));
      metric(list, '구간 추가 끊김', format(row.interruptions, '회', 0));
      metric(list, '구간 끊김 시간', format(row.interruptionMs, ' ms'));
      metric(list, '통화 누적 끊김', format(row.totals.interruptionCount, '회', 0));
      metric(list, '통화 누적 끊김 시간', format(row.totals.interruptionMs, ' ms'));
      metric(list, '마이크 보고 지연', format(latest.capture?.latencyMs, ' ms'));
      card.append(list);
      const detail = document.createElement('p'); detail.className = 'peer-detail';
      detail.textContent = `코덱 ${row.codec || '—'} · 송신 상한 ${format(row.senderCapKbps, ' kbps', 0)} · 경로 ${row.route || '—'}\n구간 손실 / 수신 ${format(row.lost, '', 0)} / ${format(row.received, '', 0)} · 패킷 간격 SDP ${row.packetTimeAccepted === true ? '수락' : row.packetTimeAccepted === false ? '실패·기본값 사용' : '—'} · 마이크 ${format(latest.capture?.sampleRate, ' Hz', 0)} / ${format(latest.capture?.channelCount, '채널', 0)} · ${row.codecParameters || ''}`;
      card.append(detail);
    }
    $('connections').append(card);
  }
}
channel?.addEventListener('message', ({ data }) => {
  if (data?.type === 'buffer-result' && pendingBuffer?.id === data.commandId && pendingBuffer.tabId === data.tabId) {
    clearTimeout(pendingBuffer.timeout);
    pendingBuffer = null;
    $('buffer-status').textContent = data.error || `${data.requestedBufferMs}ms 요청 · 수신기 ${data.applied}/${data.total}개 적용${data.total === 0 ? ' · 상대 연결 시 적용됩니다.' : data.applied < data.total ? ' · 일부 수신기는 미지원 또는 적용 실패입니다.' : ''}`;
    channel?.postMessage({ type: 'request' });
    render();
    return;
  }
  if (data?.type === 'snapshot') { tabs.set(data.tabId, data); render(); }
  else if (data?.type === 'closed') { tabs.delete(data.tabId); render(); }
});
$('call-tab').addEventListener('change', () => {
  $('buffer-status').textContent = '선택한 통화 탭에 적용합니다. 원하는 값을 선택하세요.';
  render();
});
for (const button of bufferButtons) button.addEventListener('click', () => {
  if (!latest?.room || !latest.bufferControl || pendingBuffer) return;
  const id = crypto.randomUUID(), tabId = latest.tabId;
  pendingBuffer = { id, tabId, timeout: setTimeout(() => {
    if (pendingBuffer?.id !== id) return;
    pendingBuffer = null;
    $('buffer-status').textContent = '적용 응답을 받지 못했습니다. 통화 탭을 확인하고 수신기 설정값을 확인하세요.';
    render();
  }, 4000) };
  $('buffer-status').textContent = `${button.dataset.buffer}ms 적용 중…`;
  channel.postMessage({ type: 'set-buffer', tabId, commandId: id, value: Number(button.dataset.buffer) });
  render();
});
$('download').addEventListener('click', () => {
  if (!latest) return;
  const url = URL.createObjectURL(new Blob([JSON.stringify(latest, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = url; link.download = `voice-debug-${Date.now()}.json`; link.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
});
function refresh() { channel?.postMessage({ type: 'request' }); render(); }
if (channel) { refresh(); setInterval(refresh, 1000); }
else $('debug-status').textContent = '이 브라우저는 통화 탭과의 진단 통신을 지원하지 않습니다.';
window.addEventListener('pagehide', () => channel?.close());
