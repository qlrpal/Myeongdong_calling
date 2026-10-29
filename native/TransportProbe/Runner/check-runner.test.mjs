import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { runStep, overall, validateLifecycle, writeSummary } from './check-runner.mjs';

async function directory(t) {
  const dir = await mkdtemp(path.join(tmpdir(), 'calling-runner-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  return dir;
}
test('keeps UTF-8 logs and validates after output is closed', async t => {
  const dir = await directory(t);
  const result = await runStep({ id: 'good', command: process.execPath,
    args: ['-e', 'console.log(JSON.stringify({message:"한글 확인"}))'], directory: dir,
    validate: async () => assert.equal(JSON.parse(await readFile(path.join(dir, 'good.log'), 'utf8')).message, '한글 확인') });
  assert.equal(result.status, 'pass');
});
test('nonzero, spawn failure, invalid report and timeout remain failures', async t => {
  const dir = await directory(t);
  const base = { command: process.execPath, directory: dir };
  assert.equal((await runStep({ ...base, id: 'exit', args: ['-e', 'process.exit(3)'] })).exitCode, 3);
  assert.equal((await runStep({ ...base, id: 'missing', command: path.join(dir, 'missing.exe') })).status, 'fail');
  assert.equal((await runStep({ ...base, id: 'report', args: ['-e', ''], validate: () => { throw new Error('report missing'); } })).status, 'fail');
  const hung = await runStep({ ...base, id: 'timeout', args: ['-e', 'setInterval(()=>{},1000)'], timeoutMs: 250 });
  assert.equal(hung.timedOut, true); assert.equal(hung.status, 'fail');
});
test('abort terminates owned process and is not a pass', async t => {
  const dir = await directory(t), controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 250);
  const result = await runStep({ id: 'abort', command: process.execPath, args: ['-e', 'setInterval(()=>{},1000)'],
    directory: dir, signal: controller.signal });
  clearTimeout(timer); assert.equal(result.aborted, true); assert.equal(result.status, 'fail');
});
test('incomplete reports and skipped tests cannot become overall pass', async t => {
  assert.equal(overall([]), 'incomplete');
  assert.equal(overall([{ status: 'pass' }, { status: 'skipped' }]), 'incomplete');
  assert.equal(overall([{ status: 'fail' }, { status: 'skipped' }]), 'fail');
  const dir = await directory(t), file = path.join(dir, 'incomplete.json');
  await writeFile(file, JSON.stringify({ error: null, checks: [] }));
  await assert.rejects(validateLifecycle(file));
  await writeSummary(dir, { profile: 'core', status: 'incomplete', steps: [{ id: 'missing', status: 'skipped', reason: '미실시' }], scope: '범위', notTested: ['지연'] });
  assert.equal(JSON.parse(await readFile(path.join(dir, 'summary.json'), 'utf8')).status, 'incomplete');
  assert.match(await readFile(path.join(dir, 'summary.md'), 'utf8'), /미실시/);
});
