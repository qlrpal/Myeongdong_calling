import { fixture } from './synthetic-fixture.mjs';
import { spawn, execFileSync } from 'node:child_process';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { runStep, overall, stopProcess, validateLifecycle, writeSummary } from './check-runner.mjs';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const args = process.argv.slice(2);
let profile = 'core', noBuild = false, noRestore = false;
for (let i = 0; i < args.length; i++) {
  if (args[i] === '--profile') profile = args[++i];
  else if (args[i] === '--no-build') noBuild = true;
  else if (args[i] === '--no-restore') noRestore = true;
  else throw new Error('Unknown option: ' + args[i]);
}
if (!['core', 'devices', 'group'].includes(profile)) throw new Error('Invalid profile');
const parent = path.join(root, 'native/.local/app-checks');
await mkdir(parent, { recursive: true });
const directory = await mkdtemp(path.join(parent, new Date().toISOString().replaceAll(':', '-') + '-'));
const report = { schemaVersion: 1, startedUtc: new Date().toISOString(), profile, status: 'running',
  scope: '이 PC의 선택된 기능 검사. localhost 실제 WebRTC와 제어된 합성 입력을 사용한다. 서로 다른 PC의 통화가 아니다.',
  notTested: ['독립 PC·실제 다인 사용자', '외부망·TURN', '게임 부하', '물리 장치 뽑기/재삽입', '물리 입→귀 지연·주관적 음질', '장시간 연속성·PLC 품질 기준'], steps: [], hashes: {}, git: null, gitDirty: null, noBuild };
const controller = new AbortController();
const interrupt = () => controller.abort();
process.on('SIGINT', interrupt); process.on('SIGTERM', interrupt);
const binary = path.join(root, 'native/TransportProbe/bin/Release/net10.0-windows10.0.19041.0');
const probe = path.join(binary, 'TransportProbe.exe');
let server;
await writeSummary(directory, report);
async function step(id, command, parameters, options = {}) {
  if (controller.signal.aborted) {
    report.steps.push({ id, status: 'skipped', reason: '실행 취소' }); return false;
  }
  console.log('START ' + id);
  const result = await runStep({ id, command, args: parameters, cwd: root, directory,
    signal: controller.signal, ...options });
  report.steps.push(result); console.log(result.status.toUpperCase() + ' ' + id);
  await writeSummary(directory, report);
  return result.status === 'pass';
}
function skip(id, reason) { report.steps.push({ id, status: 'skipped', reason }); }
async function startServer() {
  server = spawn(process.execPath, [path.join(root, 'native/TransportProbe/Fixtures/lifecycle-server.mjs')],
    { cwd: root, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const limit = 8192; let errors = '';
  server.stderr.on('data', chunk => { if (errors.length < limit) errors += chunk.toString(); });
  try {
    const port = await new Promise((resolve, reject) => {
      let text = '';
      const timeout = setTimeout(() => finish(new Error('Localhost 서버 시작 시간 초과')), 10000);
      function finish(error, value) { clearTimeout(timeout); server.off('error', onError); server.off('exit', onExit); server.stdout.off('data', onData); controller.signal.removeEventListener('abort', onAbort); error ? reject(error) : resolve(value); }
      const onError = error => finish(error), onExit = () => finish(new Error('Localhost 서버 조기 종료'));
      const onAbort = () => finish(new Error('실행 취소'));
      const onData = chunk => {
        text += chunk.toString();
        if (text.length > 100) return finish(new Error('Invalid server startup output'));
        if (text.includes('\n')) {
          const value = Number(text.trim());
          if (!Number.isInteger(value) || value < 1 || value > 65535) finish(new Error('Invalid localhost port'));
          else finish(null, value);
        }
      };
      server.once('error', onError); server.once('exit', onExit); server.stdout.on('data', onData);
      controller.signal.addEventListener('abort', onAbort, { once: true });
      if (controller.signal.aborted) onAbort();
    });
    return 'http://127.0.0.1:' + port + '/';
  } finally { await writeFile(path.join(directory, 'server.log'), errors); }
}
try {
  if (process.platform !== 'win32') throw new Error('Windows에서 실행하세요.');
  try {
    report.git = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8', windowsHide: true }).trim();
    report.gitDirty = !!execFileSync('git', ['status', '--porcelain'], { cwd: root, encoding: 'utf8', windowsHide: true }).trim();
  } catch { /* archive checkout */ }
  if (!noBuild) {
    if (!noRestore && !await step('restore', 'dotnet.exe', ['restore', 'native/VoiceCall.slnx', '--locked-mode'], { timeoutMs: 900000 })) throw new Error('의존성 복원 실패');
    if (!await step('build', 'dotnet.exe', ['build', 'native/VoiceCall.slnx', '-c', 'Release', '--no-restore', '-warnaserror', '-m:1'], { timeoutMs: 900000 })) throw new Error('빌드 실패');
  }
  for (const name of ['VoiceNative.dll', 'TransportProbe.dll', 'SIPSorcery.dll'])
    report.hashes[name] = createHash('sha256').update(await readFile(path.join(binary, name))).digest('hex');
  await step('runner-tests', process.execPath, ['--test', path.join(root, 'native/TransportProbe/Runner/check-runner.test.mjs'), path.join(root, 'native/TransportProbe/Runner/pair-summary.test.mjs'), path.join(root, 'native/TransportProbe/Runner/pair-observation.test.mjs')]);
  await step('package-tests', process.execPath, ['--test', path.join(root, 'native/release/package.test.mjs')]);
  const plans = [
    ['sdk', ['--sdk-build-check']], ['product', ['--product-checks', path.join(directory, 'product')]],
    ['rtp-buffer', ['--rtp-playout-test']],
    ['ui-reading', ['--ui-reading-checks', path.join(directory, 'ui-reading')]],
    ['recovery-device-periods', ['--recovery-device-period-test', path.join(directory, 'recovery-device-periods.json')]], ['audio-converter', ['--native-converter-test']],
    ['ipv4-srtp', ['--ten-ms-rtcp-integration']], ['variable-frame', ['--variable-frame-integration']],
    ['ipv6-srtp', ['--ipv6-loopback-integration']]
  ];
  for (const [id, parameters] of plans) await step(id, probe, parameters, { timeoutMs: 180000 });
  await step('server-tests', process.execPath, ['--test'], { cwd: path.join(root, 'native/server') });
  if (profile === 'core') {
    skip('actual-device-lifecycle', 'core는 실제 장치를 열지 않음. -Profile devices로 별도 실행');
    // A deliberately unselected profile is outside scope, not an incomplete core run.
    report.notTested.push('실제 장치 참가·퇴장·재참가');
    report.steps.at(-1).outsideScope = true;
  } else {
    console.log('실제 공유 입력/출력을 엽니다. 마이크 PCM은 합성 신호로 교체합니다. 시험 소리가 들릴 수 있습니다.');
    const devicesOk = await step('audio-devices', probe, ['--audio-devices'], { validate: async () => {
      const d = JSON.parse(await readFile(path.join(directory, 'audio-devices.log'), 'utf8'));
      if (!d.defaultCapture || !d.defaultRender) throw new Error('기본 입력/출력 장치 없음. 로컬 Windows 로그인과 장치를 확인하세요.');
    } });
    if (!devicesOk) {
      skip('lifecycle-legacy', '기본 입력/출력 장치 확인 실패'); skip('lifecycle-shared', '기본 입력/출력 장치 확인 실패');
      if (profile === 'group') { skip('group-legacy', '장치 준비 실패'); skip('group-shared', '장치 준비 실패'); }
    } else {
      const wave = path.join(directory, 'synthetic.wav'); await writeFile(wave, fixture());
      const url = await startServer();
      for (const [label, shared] of [['legacy', 'false'], ['shared', 'true']]) {
        const file = path.join(directory, 'lifecycle-' + label + '.json');
        await step('lifecycle-' + label, probe, ['--call-lifecycle-integration', url, file, wave, shared],
          { timeoutMs: 240000, validate: () => validateLifecycle(file) });
      }
      if (profile === 'group') for (const [label, shared] of [['legacy', 'false'], ['shared', 'true']]) {
        const file = path.join(directory, 'group-' + label + '.json');
        await step('group-' + label, probe, ['--local-group-output', url, file, wave, shared, 'baseline'], { timeoutMs: 240000,
          validate: async () => { const data = JSON.parse(await readFile(file, 'utf8')); if (data.error || !data.resources?.some(r => r.phase === 'fresh-call')) throw new Error('그룹 검사 미완료'); } });
      }
      else report.notTested.push('실제 장치 2·3·6엔진 그룹 수명');
    }
  }
} catch (error) {
  report.steps.push({ id: 'runner', status: 'fail', reason: error.message });
} finally {
  if (server?.pid) {
    try { await stopProcess(server); }
    catch (error) { report.steps.push({ id: 'server-cleanup', status: 'fail', reason: error.message }); }
  }
  report.status = overall(report.steps.filter(step => !step.outsideScope));
  report.completedUtc = new Date().toISOString();
  await writeSummary(directory, report);
  process.off('SIGINT', interrupt); process.off('SIGTERM', interrupt);
  console.log('결과: ' + report.status + '\n보고서: ' + path.join(directory, 'summary.md'));
  process.exitCode = report.status === 'pass' ? 0 : 1;
}
