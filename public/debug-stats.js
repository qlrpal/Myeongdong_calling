export function intervalStats(current, previous) {
  const delta = key => Number.isFinite(current[key]) && Number.isFinite(previous?.[key]) && current[key] >= previous[key]
    ? current[key] - previous[key] : null;
  const samples = delta('jitterBufferEmittedCount');
  const average = key => samples > 0 && delta(key) !== null ? delta(key) / samples * 1000 : null;
  const elapsed = delta('timestamp');
  const received = delta('packetsReceived'), lost = delta('packetsLost');
  return {
    bitrateKbps: elapsed > 0 && delta('bytesReceived') !== null ? delta('bytesReceived') * 8 / elapsed : null,
    bufferMs: average('jitterBufferDelay'), targetMs: average('jitterBufferTargetDelay'), minimumMs: average('jitterBufferMinimumDelay'),
    lost, received, lossPercent: received !== null && lost !== null && received + lost > 0 ? lost / (received + lost) * 100 : null,
    packetsPerSecond: elapsed > 0 && received !== null ? received * 1000 / elapsed : null,
    packetCadenceMs: elapsed > 0 && received > 0 ? elapsed / received : null,
    interruptions: delta('interruptionCount'), interruptionMs: delta('totalInterruptionDuration') === null ? null : delta('totalInterruptionDuration') * 1000,
  };
}
