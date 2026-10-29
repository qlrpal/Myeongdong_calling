import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { compareObservations } from './pair-observation.mjs';

const [manifest, directory, option, selectedFactor] = process.argv.slice(2);
if (!manifest || !directory) throw new Error('Usage: node analyze-pair-observation.mjs MANIFEST.json OUTPUT_DIRECTORY');
if (option && (option !== '--factor' || !['shared', 'reserve'].includes(selectedFactor))) throw new Error('Use --factor shared|reserve');
await mkdir(directory, { recursive: true });
try {
  const entries = JSON.parse(await readFile(manifest, 'utf8'));
  const pairs = await Promise.all(entries.map(async row => Promise.all([row.pcReport, row.laptopReport].map(async file => JSON.parse(await readFile(file, 'utf8'))))));
  const result = compareObservations(pairs, selectedFactor ?? 'shared');
  await writeFile(path.join(directory, 'comparison.json'), JSON.stringify(result, null, 2) + '\n');
  const n = value => value === null ? '미측정' : Number(value).toFixed(3);
  await writeFile(path.join(directory, 'comparison.md'), ['# 두 PC 1:1 ' + (result.factor === 'reserve' ? '수신 여유' : '출력') + ' 대조', '', result.notes, '',
    '| 단계 | 역할 | 큐 중앙/P95 ms | PLC ms / 관측시간 % | 출력 부족 ms | 늦은 폐기 / 재버퍼 | CPU 코어 | 할당 MiB/s | GC 정지 ms | RTT 중앙 ms (보고 수) |',
    '|---|---|---|---|---|---|---|---|---|---|',
    ...result.rows.map(r => `| ${r.trial} | ${r.role} | ${n(r.queueMs.median)} / ${n(r.queueMs.p95)} | ${n(r.concealedMs)} / ${n(r.concealedMsPerObservationPercent)} | ${n(r.underfillMs)} | ${r.latePackets ?? '미측정'} / ${r.rebuffers ?? '미측정'} | ${n(r.cpuCores)} | ${n(r.allocatedMiBPerSecond)} | ${n(r.gcPauseMs)} | ${n(r.rtcpRttMs.median)} (${r.rtcpRttMs.samples}) |`), '',
    '설정·실제 장치 주기·송신 대기·부족 횟수 등은 comparison.json에 보존한다. 성공은 관측 완료이며 후보 채택이나 품질 합격이 아니다.', ''].join('\n'));
  console.log('측정 요약: ' + path.join(directory, 'comparison.md'));
} catch (error) {
  await writeFile(path.join(directory, 'comparison-error.json'), JSON.stringify({ status: 'fail', reason: error.message }, null, 2));
  console.error('분석 거부: ' + error.message); process.exitCode = 1;
}
