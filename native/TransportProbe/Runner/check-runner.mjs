import { spawn } from 'node:child_process';
import { createWriteStream } from 'node:fs';
import { readFile, writeFile } from 'node:fs/promises';
import { once } from 'node:events';
import path from 'node:path';

export async function stopProcess(child) {
  if (child.exitCode !== null || child.signalCode !== null) return;
  if (process.platform === 'win32') {
    // Only the PID returned by our own spawn: never enumerate/kill app processes.
    const code = await new Promise(resolve => {
      const killer = spawn('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
      killer.on('error', () => resolve(-1)); killer.on('close', resolve);
    });
    if (code !== 0 && child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
  } else child.kill('SIGKILL');
}

export async function runStep({ id, command, args = [], cwd, directory, timeoutMs = 120000, signal, validate }) {
  const started = Date.now();
  const output = createWriteStream(path.join(directory, id + '.log'), { flags: 'wx' });
  const closed = once(output, 'close');
  let child, timedOut = false, aborted = false, timer, failure = null, exitCode = null;
  const stop = () => { aborted = true; if (child?.pid) void stopProcess(child); };
  try {
    if (signal?.aborted) { aborted = true; throw new Error('실행 취소'); }
    child = spawn(command, args, { cwd, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.pipe(output, { end: false }); child.stderr.pipe(output, { end: false });
    signal?.addEventListener('abort', stop, { once: true });
    timer = setTimeout(() => { timedOut = true; if (child.pid) void stopProcess(child); }, timeoutMs);
    exitCode = await new Promise((resolve, reject) => { child.on('error', reject); child.on('close', resolve); });
    if (timedOut) throw new Error('제한 시간 초과');
    if (aborted) throw new Error('실행 취소');
    if (exitCode !== 0) throw new Error('프로세스 종료 코드 ' + exitCode);
  } catch (error) { failure = error.message; }
  finally {
    clearTimeout(timer); signal?.removeEventListener('abort', stop);
    if (child?.pid) await stopProcess(child);
    output.end(); await closed;
  }
  if (!failure && validate) {
    try { await validate(); } catch (error) { failure = error.message; }
  }
  return { id, status: failure ? 'fail' : 'pass', seconds: (Date.now() - started) / 1000,
    exitCode, timedOut, aborted, reason: failure, log: id + '.log' };
}

export function overall(steps) {
  if (steps.some(step => step.status === 'fail')) return 'fail';
  if (!steps.length || steps.some(step => step.status === 'skipped')) return 'incomplete';
  return 'pass';
}

export async function validateLifecycle(file) {
  const report = JSON.parse(await readFile(file, 'utf8'));
  const expected = ['after-release-failure', 'cleanup-warning-blocks-auto-recovery', 'other-peers-continue',
    'rejoin-after-failed-cleanup', 'old-stream-cleanup-skips-new-call', 'invalid-input', 'invalid-output',
    'server-503', 'actual-sse-disconnect-recovery', 'hung-leave-notification'];
  if (report.error || !expected.every(name => report.checks?.some(check => check.name === name)))
    throw new Error('장애 검사 보고서 미완료');
}

export async function writeSummary(directory, report) {
  await writeFile(path.join(directory, 'summary.json'), JSON.stringify(report, null, 2) + '\n');
  const lines = ['# 앱 자동 검사', '', `선택 범위: ${report.profile} / 결과: ${report.status}`, '',
    '| 검사 | 결과 | 초 | 이유 |', '|---|---|---:|---|',
    ...report.steps.map(step => `| ${step.id} | ${step.status} | ${step.seconds ?? '—'} | ${(step.reason ?? '').replaceAll('|', '/').replaceAll('\n', ' ')} |`),
    '', '## 판정 범위', '', report.scope, '', '## 미실시', '', ...report.notTested.map(item => '- ' + item), '',
    '통과는 선택한 기능 검사에만 해당한다. PLC·지연·음질 합격이나 앱 전체 완성을 의미하지 않는다.', ''];
  await writeFile(path.join(directory, 'summary.md'), lines.join('\n'));
}
