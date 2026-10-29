// Deterministic voiced waveform, not recorded speech or a physical latency probe.
export function fixture() {
  const rate = 48000, count = rate * 2, wave = Buffer.alloc(44 + count * 2);
  wave.write('RIFF'); wave.writeUInt32LE(wave.length - 8, 4); wave.write('WAVEfmt ', 8);
  wave.writeUInt32LE(16, 16); wave.writeUInt16LE(1, 20); wave.writeUInt16LE(1, 22);
  wave.writeUInt32LE(rate, 24); wave.writeUInt32LE(rate * 2, 28); wave.writeUInt16LE(2, 32); wave.writeUInt16LE(16, 34);
  wave.write('data', 36); wave.writeUInt32LE(count * 2, 40);
  for (let i = 0; i < count; i++) {
    const t = i / rate, envelope = .3 + .7 * Math.sin(Math.PI * 3 * t) ** 2;
    wave.writeInt16LE(Math.round(envelope * (5200 * Math.sin(2 * Math.PI * 173 * t)
      + 2100 * Math.sin(2 * Math.PI * 346 * t) + 900 * Math.sin(2 * Math.PI * 692 * t))), 44 + i * 2);
  }
  return wave;
}
