import { intervalStats } from '/debug-stats.js';

if ('BroadcastChannel' in window) {
  const channel = new BroadcastChannel('voice-room-diagnostics-v1');
  const tabId = crypto.randomUUID();
  const previous = new Map();
  let busy = false;
  channel.onmessage = async ({ data }) => {
    if (data?.type === 'set-buffer' && data.tabId === tabId) {
      let result = { error: '통화 페이지를 새로고침하세요.' };
      if ([0, 5, 10, 20].includes(data.value)) {
        window.dispatchEvent(new CustomEvent('voice-debug-buffer', { detail: { value: data.value, reply: value => { result = value; } } }));
        previous.clear();
      } else result = { error: '허용되지 않는 버퍼 값입니다.' };
      channel.postMessage({ type: 'buffer-result', tabId, commandId: data.commandId, ...result });
      return;
    }
    if (data?.type !== 'request' || busy) return;
    busy = true;
    try {
      let state;
      window.dispatchEvent(new CustomEvent('voice-debug-request', { detail: value => { state = value; } }));
      if (!state) return;
      const rows = await Promise.all(state.peers.map(async peer => {
        try {
          const stats = await peer.pc.getStats();
          const incoming = [...stats.values()].filter(stat => stat.type === 'inbound-rtp' && (stat.kind || stat.mediaType) === 'audio');
          const transport = [...stats.values()].find(stat => stat.type === 'transport' && stat.selectedCandidatePairId);
          const pair = transport ? stats.get(transport.selectedCandidatePairId) : [...stats.values()].find(stat => stat.type === 'candidate-pair' && stat.nominated && stat.state === 'succeeded');
          const receiver = peer.receiver;
          let receiverTarget = null;
          try { if (receiver && 'jitterBufferTarget' in receiver) receiverTarget = receiver.jitterBufferTarget; } catch {}
          return incoming.map(stat => {
            const key = `${peer.id}:${stat.id}`;
            const measurement = intervalStats(stat, previous.get(key));
            previous.set(key, stat);
            const codec = stats.get(stat.codecId);
            const local = stats.get(pair?.localCandidateId), remote = stats.get(pair?.remoteCandidateId);
            return { peerId: peer.id, name: peer.name, state: peer.pc.connectionState,
              receiverTarget, receiverSupported: !!receiver && 'jitterBufferTarget' in receiver,
              packetTimeAccepted: peer.packetTimeAccepted ?? null,
              jitterMs: Number.isFinite(stat.jitter) ? stat.jitter * 1000 : null,
              rttMs: Number.isFinite(pair?.currentRoundTripTime) ? pair.currentRoundTripTime * 1000 : null,
              route: pair ? `${local?.candidateType || '?'} → ${remote?.candidateType || '?'} / ${local?.protocol || '?'}` : null,
              codec: codec?.mimeType || null, codecParameters: codec?.sdpFmtpLine || null,
              senderCapKbps: peer.pc.getSenders().find(sender => sender.track?.kind === 'audio')?.getParameters().encodings?.[0]?.maxBitrate / 1000 || null,
              totals: { packetsLost: stat.packetsLost ?? null, packetsReceived: stat.packetsReceived ?? null,
                interruptionCount: stat.interruptionCount ?? null, interruptionMs: Number.isFinite(stat.totalInterruptionDuration) ? stat.totalInterruptionDuration * 1000 : null },
              ...measurement };
          });
        } catch { return [{ peerId: peer.id, name: peer.name, state: peer.pc.connectionState, error: '통계를 읽지 못했습니다.' }]; }
      }));
      const active = new Set(state.peers.map(peer => peer.id));
      for (const key of previous.keys()) if (!active.has(key.split(':')[0])) previous.delete(key);
      channel.postMessage({ type: 'snapshot', tabId, room: state.room, bufferControl: true, requestedBitrateKbps: state.bitrate / 1000,
        requestedBufferMs: state.bufferTarget, requestedPacketTimeMs: state.packetTimeMs, capture: state.capture,
        timestamp: Date.now(), peerCount: state.peers.length, rows: rows.flat() });
    } finally { busy = false; }
  };
  window.addEventListener('pagehide', () => { channel.postMessage({ type: 'closed', tabId }); channel.close(); });
}
