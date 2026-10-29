import { summarizePair } from './pair-summary.mjs';

const get = (object, key) => Object.entries(object ?? {}).find(([name]) => name.toLowerCase() === key.toLowerCase())?.[1] ?? null;
const finite = value => typeof value === 'number' && Number.isFinite(value);
function distribution(values) {
  const sorted = values.filter(finite).sort((a, b) => a - b);
  const q = p => sorted.length ? sorted[Math.max(0, Math.ceil(sorted.length * p) - 1)] : null;
  return { samples: sorted.length, median: q(.5), p95: q(.95), p99: q(.99), max: q(1) };
}

export function summarizeObservation(report) {
  if (report?.error || !report?.completed) throw new Error('실패/미완료 원본은 관측 기준선으로 사용할 수 없습니다.');
  const window = report.windows?.find(w => w.phase === 'baseline');
  const samples = window?.samples;
  if (!samples || samples.length < 2) throw new Error('관측 표본 부족');
  const points = samples.map(s => {
    const peers = get(s, 'Peers');
    if (!Array.isArray(peers) || peers.length !== 1 || get(peers[0], 'State') !== 'connected'
      || get(get(peers[0], 'Audio'), 'DeviceError')) throw new Error('관측 중 연결/출력 실패');
    return { sample: s, peer: peers[0], audio: get(peers[0], 'Audio'), mic: get(s, 'Microphone'),
      rtp: get(get(peers[0], 'Audio'), 'RtpPlayout'), sender: get(s, 'Sender'), at: get(s, 'MonotonicSeconds') };
  });
  const peer = get(points[0].peer, 'Id');
  const expectedRoute = report.route === 'direct' ? '직접 연결' : 'TURN 중계';
  if (points.some(p => report.shared
    ? get(get(p.sample, 'SharedOutput'), 'OutputStreams') !== 1 || get(get(p.sample, 'SharedOutput'), 'Sources') !== 1
    : get(p.sample, 'SharedOutput') !== null)) throw new Error('관측 출력 구조와 요청 불일치');
  if (points.some((p, i) => !finite(p.at) || i > 0 && p.at <= points[i - 1].at
    || get(p.peer, 'Id') !== peer || get(p.peer, 'Route') !== expectedRoute)) throw new Error('시각·세션·경로 불일치');
  const seconds = points.at(-1).at - points[0].at, expected = report.observeSeconds ?? 5;
  if (seconds < expected * .9 || !finite(window.elapsedSeconds) || window.elapsedSeconds < expected * .98)
    throw new Error('관측 구간 미완료');
  function delta(read) {
    const values = points.map(read);
    if (values.some(v => v === null || v === undefined)) return null;
    if (values.some((v, i) => !finite(v) || v < 0 || i > 0 && v < values[i - 1])) throw new Error('관측 중 누적 카운터 역행/오류');
    return values.at(-1) - values[0];
  }
  const concealMs = delta(p => get(p.rtp, 'ConcealedMs'));
  let previousAge = null; const rtts = [];
  for (const p of points) {
    const precision = get(p.peer, 'Precision'), age = get(precision, 'RttAgeSeconds'), rtt = get(precision, 'RtcpRttMs');
    if (finite(age) && age >= 0 && age < 10 && finite(rtt) && p.at - age >= points[0].at
      && (previousAge === null || age < previousAge - .1)) rtts.push(rtt);
    if (finite(age)) previousAge = age;
  }
  const unique = read => [...new Set(points.map(read))];
  for (const key of ['cpuCores', 'allocatedBytes', 'gcPauseMs'])
    if (!finite(window[key]) || window[key] < 0) throw new Error('자원 관측 누락/오류');
  const result = { role: report.role, shared: report.shared, observedSeconds: seconds, snapshotCount: points.length,
    queueMs: distribution(points.map(p => get(p.audio, 'QueueMs'))), rtcpRttMs: distribution(rtts),
    concealedMs: concealMs, concealedMsPerObservationPercent: concealMs === null ? null : concealMs / (seconds * 10),
    underfillMs: delta(p => get(p.audio, 'UnderfillMs')), underruns: delta(p => get(p.audio, 'Underruns')),
    latePackets: delta(p => get(p.rtp, 'LatePackets')), rebuffers: delta(p => get(p.rtp, 'Rebuffers')),
    invalidPackets: delta(p => get(p.rtp, 'InvalidPackets')), duplicatePackets: delta(p => get(p.rtp, 'DuplicatePackets')),
    connectionMaxConsecutivePlcMs: distribution(points.map(p => get(p.rtp, 'MaxConsecutivePlcMs'))).max,
    sourceDrops: delta(p => get(p.sender, 'DroppedFrames')), sourceExpired: delta(p => get(p.sender, 'ExpiredFrames')),
    captureDiscontinuities: delta(p => get(p.mic, 'CaptureDiscontinuities')),
    renderEmptyEvents: delta(p => get(get(p.audio, 'Render'), 'EmptyEvents')),
    renderTargetPaddingMs: distribution(points.map(p => get(get(p.audio, 'Render'), 'TargetPaddingMs'))),
    outputApiReferenceMs: distribution(points.map(p => get(p.audio, 'DeviceLatencyMs'))),
    sendWaitSnapshotMs: distribution(points.map(p => get(p.sender, 'LastWaitMs'))),
    cpuCores: window.cpuCores, allocatedMiBPerSecond: window.allocatedBytes / 1048576 / window.elapsedSeconds, gcPauseMs: window.gcPauseMs,
    configuration: { frameMs: unique(p => get(p.sample, 'FrameMs')), policy: unique(p => get(p.sample, 'PlaybackPolicy')),
      reserveMs: unique(p => get(p.rtp, 'TargetReserveMs')), capturePeriodMs: unique(p => get(p.mic, 'EnginePeriodMs')),
      requestedOutputMs: unique(p => get(p.audio, 'RequestedOutputMs')), outputPeriodMs: unique(p => get(p.audio, 'EnginePeriodMs')),
      captureDevice: unique(p => get(p.mic, 'Device')), outputDevice: unique(p => get(p.audio, 'Device')),
      captureFormat: unique(p => get(p.mic, 'DeviceFormat')), outputFormat: unique(p => get(p.audio, 'DeviceFormat')),
      encoder: unique(p => JSON.stringify(get(p.sample, 'Encoder'))),
      mediaAddress: report.mediaAddress ?? null } };
  return result;
}

export function compareObservations(pairs, factor = 'shared') {
  if (!['shared', 'reserve'].includes(factor)) throw new Error('Unknown comparison factor');
  if (pairs.length !== 3) throw new Error('기존→단일→기존의 세 쌍이 필요합니다.');
  const rows = [];
  for (let i = 0; i < pairs.length; i++) {
    const [a, b] = pairs[i], verdict = summarizePair(a, b);
    if (verdict.status !== 'pass' || a.shared !== (factor === 'shared' && i === 1)) throw new Error('양쪽 기능/순서/호스트 판정 실패');
    if (factor === 'reserve') {
      const base = pairs[0][0].reserveMs, reserve = a.reserveMs;
      if (!Number.isInteger(base) || !Number.isInteger(reserve) || reserve < 10 || reserve > 40
        || (i === 1 ? reserve >= base : reserve !== base)) throw new Error('수신 여유 대조 순서/범위 오류');
    }
    for (const [role, report] of [['a', a], ['b', b]]) {
      const first = pairs[0][role === 'a' ? 0 : 1];
      if (['buildHash', 'probeHash', 'fixtureHash', 'route', 'observeSeconds', 'hostFingerprint', 'mediaAddress'].some(k => report[k] !== first[k]))
        throw new Error('대조 중 빌드·파형·경로·시간·호스트·인터페이스 변경');
      const row = { trial: ['A1', 'B', 'A2'][i], ...summarizeObservation(report) };
      if (factor === 'reserve' && (row.configuration.reserveMs.length !== 1 || row.configuration.reserveMs[0] !== report.reserveMs))
        throw new Error('요청 수신 여유가 실제 버퍼에 적용되지 않음');
      const previous = rows.find(r => r.role === role);
      // Actual output period is an observed outcome; compare requested settings.
      const controlConfig = config => Object.fromEntries(Object.entries(config).filter(([key]) => !(factor === 'shared' && key === 'outputPeriodMs') && !(factor === 'reserve' && key === 'reserveMs')));
      if (previous && JSON.stringify(controlConfig(previous.configuration)) !== JSON.stringify(controlConfig(row.configuration)))
        throw new Error('대조 중 프레임·정책·재생 여유·장치 설정 변경');
      rows.push(row);
    }
  }
  const assessment = factor === 'reserve' ? ['a', 'b'].map(role => {
    const [a1, b, a2] = rows.filter(row => row.role === role);
    const latencyImproved = ['median', 'p95'].every(key => b.queueMs[key] !== null
      && a1.queueMs[key] !== null && a2.queueMs[key] !== null && b.queueMs[key] < Math.min(a1.queueMs[key], a2.queueMs[key]));
    const metrics = ['concealedMs', 'underfillMs', 'rebuffers', 'latePackets', 'sourceDrops', 'invalidPackets', 'renderEmptyEvents'];
    const continuityNotWorseThanControlRange = metrics.every(key => [a1[key], b[key], a2[key]].every(finite) && b[key] <= Math.max(a1[key], a2[key]));
    return { role, latencyImproved, continuityNotWorseThanControlRange, followUpEligible: latencyImproved && continuityNotWorseThanControlRange,
      cpuAboveControlRange: b.cpuCores > Math.max(a1.cpuCores, a2.cpuCores) };
  }) : null;
  return { schemaVersion: 1, status: 'measured', factor, assessment, qualityQualified: false, physicalLatencyMeasured: false, rows,
    notes: '큐·송신 대기는 250ms 진단 스냅샷 분포, RTT는 신선한 서로 다른 로컬 RTCP 보고다. PLC 비율은 관측 시간 대비 마스킹 오디오 시간이며 패킷 손실률이 아니다. CPU/GC/할당은 시험 C# 프로세스만 포함한다. 1:1 A/B/A 한 번이며 다인/게임/장시간/물리 입→귀 지연 합격이 아니다.' };
}
