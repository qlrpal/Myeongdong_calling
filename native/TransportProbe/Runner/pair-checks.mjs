import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { isIP } from 'node:net';
import { runStep, overall, writeSummary } from './check-runner.mjs';
import { fixture } from './synthetic-fixture.mjs';
import { summarizePair } from './pair-summary.mjs';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const args = process.argv.slice(2), command = args.shift();
const parent = path.join(root, 'native/.local/pair-checks'); await mkdir(parent, { recursive: true });
const directory = await mkdtemp(path.join(parent, new Date().toISOString().replaceAll(':', '-') + '-'));
if (command === 'merge') {
  try {
    if (args.length !== 2) throw new Error('a/b 원본 보고서 두 개가 필요합니다.');
    const result = summarizePair(...await Promise.all(args.map(async file => JSON.parse(await readFile(file, 'utf8')))));
    await writeFile(path.join(directory, 'pair-summary.json'), JSON.stringify(result, null, 2) + '\n');
    await writeFile(path.join(directory, 'pair-summary.md'), ['# 두 PC 기능 검사', '', `결과: ${result.status}`,
      `양쪽 기능 완료: ${result.functionalPassed}`, '', ...result.reasons.map(reason => '- ' + reason), '', result.notes, ''].join('\n'));
    console.log('결과: ' + result.status + '\n보고서: ' + path.join(directory, 'pair-summary.md'));
    process.exitCode = result.status === 'pass' ? 0 : 1;
  } catch (error) {
    await writeFile(path.join(directory, 'pair-summary.json'), JSON.stringify({ status: 'fail', reason: error.message }));
    console.error('병합 실패: ' + error.message); process.exitCode = 1;
  }
} else {
  const report = { profile: 'pair', status: 'running', startedUtc: new Date().toISOString(), steps: [],
    scope: '이 PC의 두 참가자 시험 기여분. 반대 역할 보고서와 병합하기 전에는 두 PC 합격이 아니다.',
    notTested: ['물리 입→귀 지연', '게임 부하', '장시간 연속성·PLC 품질 기준', '실제 장치 분리'], settings: {} };
  const control = new AbortController(), stop = () => control.abort();
  process.on('SIGINT', stop); process.on('SIGTERM', stop);
  await writeSummary(directory, report);
  async function step(id, executable, parameters, options = {}) {
    const result = await runStep({ id, command: executable, args: parameters, cwd: root, directory, signal: control.signal, ...options });
    report.steps.push(result); await writeSummary(directory, report); console.log(result.status.toUpperCase() + ' ' + id);
    if (result.status !== 'pass') throw new Error('검사 실패: ' + id);
  }
  try {
    if (command !== 'run' || process.platform !== 'win32') throw new Error('Windows에서 run/merge 명령으로 실행하세요.');
    const values = {}; let noRestore = false, noBuild = false, traceBaseline = false;
    for (let i = 0; i < args.length; i++) {
      if (args[i] === '--trace-baseline') traceBaseline = true;
      else if (args[i] === '--no-restore') noRestore = true;
      else if (args[i] === '--no-build') noBuild = true;
      else if (['--server', '--run', '--role', '--route', '--shared', '--media-address', '--observe-seconds', '--reserve-ms'].includes(args[i]) && args[i + 1]) values[args[i].slice(2)] = args[++i];
      else throw new Error('잘못된 실행 옵션');
    }
    const uri = new URL(values.server);
    if (!['http:', 'https:'].includes(uri.protocol) || uri.username || uri.password || uri.search || uri.hash
      || !/^[0-9a-f]{32}$/i.test(values.run ?? '') || !['a', 'b'].includes(values.role)
      || !['direct', 'relay'].includes(values.route) || !['true', 'false'].includes(values.shared)) throw new Error('서버/실행 ID/역할/설정을 확인하세요.');
    if (values['media-address'] && !isIP(values['media-address'])) throw new Error('유효한 로컬 IP 주소를 지정하세요.');
    const observeSeconds = Number(values['observe-seconds'] ?? 5);
    if (!Number.isInteger(observeSeconds) || observeSeconds < 5 || observeSeconds > 300) throw new Error('관측 시간은 5–300초여야 합니다.');
    if (traceBaseline && observeSeconds > 45) throw new Error('상세 기준선 기록은 최대45초입니다.');
    const reserveMs = Number(values['reserve-ms'] ?? 20);
    if (!Number.isInteger(reserveMs) || reserveMs < 10 || reserveMs > 40) throw new Error('수신 여유는 10–40ms여야 합니다.');
    report.settings = { run: values.run, role: values.role, route: values.route, shared: values.shared === 'true', observeSeconds, reserveMs, traceBaseline, mediaAddress: values['media-address'] ?? null, noBuild };
    if (!noBuild) {
      if (!noRestore) await step('restore', 'dotnet.exe', ['restore', 'native/VoiceCall.slnx', '--locked-mode'], { timeoutMs: 900000 });
      await step('build', 'dotnet.exe', ['build', 'native/VoiceCall.slnx', '-c', 'Release', '--no-restore', '-warnaserror', '-m:1'], { timeoutMs: 900000 });
    }
    const wave = path.join(directory, 'synthetic.wav'), file = path.join(directory, 'participant.json');
    await writeFile(wave, fixture());
    console.log('반대 PC에서 같은 RunId/Route/SharedOutput과 반대 Role로 시작하세요. 실제 공유 장치를 열며 합성 음성이 재생될 수 있습니다.');
    await step('participant', path.join(root, 'native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0/TransportProbe.exe'),
      ['--pair-functional', values.server, values.run, values.role, values.route, file, wave, values.shared, values['media-address'] ?? 'auto', String(observeSeconds), String(reserveMs), String(traceBaseline)],
      { timeoutMs: Math.max(360000, (observeSeconds + 150) * 1000), validate: async () => {
        const data = JSON.parse(await readFile(file, 'utf8'));
        if (data.error || !data.completed || data.checks?.length !== 6 || data.checks.some(c => !c.ok)) throw new Error('참가자 보고서 오류/미완료');
      } });
  } catch (error) { report.steps.push({ id: 'pair-runner', status: 'fail', reason: error.message }); }
  finally {
    report.status = overall(report.steps); report.completedUtc = new Date().toISOString();
    await writeSummary(directory, report); process.off('SIGINT', stop); process.off('SIGTERM', stop);
    console.log('로컬 결과: ' + report.status + '\n보고서: ' + path.join(directory, 'summary.md'));
    process.exitCode = report.status === 'pass' ? 0 : 1;
  }
}
