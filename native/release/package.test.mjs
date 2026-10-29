import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { checkPackagePaths, verifyPackage } from './package.mjs';

test('beta rejects development/private files and unsafe paths', () => {
  for (const name of ['TransportProbe.exe', 'settings.json', 'voice-diagnostics-123.json', 'app.log', '.env', 'x.key',
    'VoiceNative.pdb', 'docs/reports/R.md', 'server/install.sh', '../escape', 'C:\\private.txt', '/root/file'])
    assert.throws(() => checkPackagePaths([name]), name);
  assert.doesNotThrow(() => checkPackagePaths(['VoiceNative.dll', 'licenses/NAudio.txt', 'ko/System.Windows.Forms.resources.dll']));
});
test('beta inventory detects corruption, extra files and missing required assets', async () => {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'calling-package-test-'));
  try {
    const names = ['VoiceNative.exe', 'VoiceNative.dll', 'VoiceNative.deps.json', 'VoiceNative.runtimeconfig.json',
      'SIPSorcery.dll', 'SIPSorcery.LICENSE.md', 'Opus.LICENSE.txt', 'opus.dll', 'README.md', '시작하기.md', '베타안내.md', 'THIRD-PARTY-NOTICES.md'].sort();
    const files = [];
    for (const name of names) {
      const bytes = Buffer.from('fixture: ' + name); await writeFile(path.join(directory, name), bytes);
      files.push({ path: name, bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
    }
    const manifest = { schemaVersion: 1, channel: 'beta', version: '0.6.1-beta.1', rid: 'win-x64', selfContained: false, files };
    const manifestFile = path.join(directory, 'release-manifest.json');
    await writeFile(manifestFile, JSON.stringify(manifest)); await verifyPackage(directory);
    const saved = await readFile(path.join(directory, 'VoiceNative.dll'));
    await writeFile(path.join(directory, 'VoiceNative.dll'), 'changed'); await assert.rejects(verifyPackage(directory), /hash mismatch/);
    await writeFile(path.join(directory, 'VoiceNative.dll'), saved);
    await writeFile(path.join(directory, 'extra.txt'), 'extra'); await assert.rejects(verifyPackage(directory), /inventory mismatch/);
    await rm(path.join(directory, 'extra.txt'));
    await writeFile(manifestFile, JSON.stringify({ ...manifest, selfContained: true })); await assert.rejects(verifyPackage(directory), /Missing beta asset/);
    await writeFile(manifestFile, JSON.stringify({ ...manifest, channel: 'stable' })); await assert.rejects(verifyPackage(directory), /Invalid beta manifest/);
  } finally {
    // Only this mkdtemp-created, absolute path under the OS temporary directory.
    if (path.dirname(directory) !== path.resolve(os.tmpdir()) || !path.basename(directory).startsWith('calling-package-test-')) throw new Error('Unsafe fixture cleanup');
    await rm(directory, { recursive: true, force: true });
  }
});
