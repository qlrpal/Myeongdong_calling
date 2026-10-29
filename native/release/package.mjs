import { readFile, writeFile, readdir, mkdir, copyFile } from 'node:fs/promises';
import { createHash, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export function checkPackagePaths(files) {
  for (const file of files) {
    const p = file.replaceAll('\\', '/').toLowerCase();
    if (path.posix.isAbsolute(p) || /^[a-z]:/.test(p) || p.split('/').includes('..')
      || /(^|\/)(transportprobe[^/]*|settings\.json|\.env[^/]*|app\.log[^/]*|voice-diagnostics-[^/]*)$/.test(p)
      || /(^|\/)(server|deploy|reports|\.local|bin|obj|node_modules)(\/|$)/.test(p)
      || /\.(pdb|etl|pcapng?|dmp|key|pem|pfx|wav|zip|cs|tsx|ps1|sh)$/.test(p))
      throw new Error('Development/private file in beta: ' + file);
  }
}
const requiredAssets = ['VoiceNative.exe', 'VoiceNative.dll', 'VoiceNative.deps.json', 'VoiceNative.runtimeconfig.json',
  'SIPSorcery.dll', 'SIPSorcery.LICENSE.md', 'Opus.LICENSE.txt', 'opus.dll', 'README.md', '시작하기.md', '베타안내.md', 'THIRD-PARTY-NOTICES.md'];
function checkAssets(names, contained) {
  for (const required of [...requiredAssets, ...(contained ? ['coreclr.dll', 'System.Private.CoreLib.dll', 'System.Windows.Forms.dll'] : [])])
    if (!names.includes(required)) throw new Error('Missing beta asset: ' + required);
  for (const unused of ['NAudio.dll', 'NAudio.Asio.dll', 'NAudio.Dmo.dll', 'NAudio.WinForms.dll', 'Microsoft.Windows.SDK.NET.dll', 'WinRT.Runtime.dll'])
    if (names.includes(unused)) throw new Error('Unused module returned to beta: ' + unused);
}
async function filesIn(directory, prefix = '') {
  const result = [];
  for (const item of await readdir(path.join(directory, prefix), { withFileTypes: true })) {
    if (item.isSymbolicLink()) throw new Error('Package must not contain links');
    const name = prefix ? prefix + '/' + item.name : item.name;
    if (item.isDirectory()) result.push(...await filesIn(directory, name)); else result.push(name);
  }
  return result.sort();
}
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
export async function verifyPackage(directory) {
  const manifest = JSON.parse(await readFile(path.join(directory, 'release-manifest.json'), 'utf8'));
  if (manifest.schemaVersion !== 1 || manifest.channel !== 'beta' || manifest.rid !== 'win-x64'
    || typeof manifest.selfContained !== 'boolean' || !Array.isArray(manifest.files) || !manifest.version?.includes('-beta.'))
    throw new Error('Invalid beta manifest');
  const actual = (await filesIn(directory)).filter(f => f !== 'release-manifest.json');
  checkPackagePaths(actual);
  checkAssets(actual, manifest.selfContained);
  if (JSON.stringify(actual) !== JSON.stringify(manifest.files.map(f => f.path))) throw new Error('Package inventory mismatch');
  for (const item of manifest.files) {
    const bytes = await readFile(path.join(directory, item.path));
    if (bytes.length !== item.bytes || hash(bytes) !== item.sha256) throw new Error('Package hash mismatch: ' + item.path);
  }
  return manifest;
}
function catalogName(name) {
  if (name.startsWith('NAudio.')) return 'NAudio';
  if (name.startsWith('Common.Logging')) return 'Common.Logging';
  if (name.startsWith('Microsoft.') || name.startsWith('System.')) return 'Microsoft';
  return name;
}
export async function prepareBeta(directory, nativeRoot, selfContained) {
  directory = path.resolve(directory); nativeRoot = path.resolve(nativeRoot);
  if (path.dirname(directory) !== path.join(nativeRoot, '.local') || !path.basename(directory).startsWith('publish-'))
    throw new Error('Only fresh workspace publish staging may be packaged');
  const deps = JSON.parse(await readFile(path.join(directory, 'VoiceNative.deps.json'), 'utf8'));
  const config = JSON.parse(await readFile(path.join(directory, 'VoiceNative.runtimeconfig.json'), 'utf8'));
  if (!!config.runtimeOptions.includedFrameworks !== selfContained) throw new Error('Runtime inclusion does not match package kind');
  const version = Object.keys(deps.libraries).find(n => n.startsWith('VoiceNative/'))?.split('/')[1];
  if (!version?.includes('-beta.')) throw new Error('Not a versioned beta assembly');
  const lock = JSON.parse(await readFile(path.join(nativeRoot, 'VoiceNative/packages.lock.json'), 'utf8'));
  const pinned = Object.values(lock.dependencies)[0];
  const licenseRoot = path.join(directory, 'licenses'); await mkdir(licenseRoot);
  const rows = ['# Third-party notices', '', 'Package versions are from the published dependency graph. Original license/notice texts are in `licenses/`.', '', '| Package | Version | Copyright from package metadata | Texts |', '|---|---|---|---|'];
  for (const [identity, entry] of Object.entries(deps.libraries).sort()) {
    if (entry.type !== 'package') continue;
    const slash = identity.lastIndexOf('/'), name = identity.slice(0, slash), version = identity.slice(slash+1);
    if (pinned[name] && pinned[name].resolved !== version) throw new Error('Publish changed pinned dependency: ' + identity);
    const source = path.join(nativeRoot, '.nuget', name.toLowerCase(), version);
    const entries = await readdir(source);
    const nuspec = entries.find(f => f.endsWith('.nuspec'));
    const metadata = nuspec ? await readFile(path.join(source, nuspec), 'utf8') : '';
    const copyright = (metadata.match(/<copyright>([\s\S]*?)<\/copyright>/)?.[1] ?? 'See license text')
      .replaceAll('|', '\\|').replaceAll(/\s+/g, ' ').replaceAll('&amp;', '&').replaceAll('&lt;', '<').replaceAll('&gt;', '>');
    const documents = entries.filter(f => /^(licen[cs]e|copying|notice|third-party-notices)(\.|$)/i.test(f));
    const copied = [];
    for (const document of documents) {
      const destination = name + '-' + document;
      await copyFile(path.join(source, document), path.join(licenseRoot, destination)); copied.push(destination);
    }
    if (!documents.some(f => /^licen[cs]e(\.|$)/i.test(f))) {
      const catalog = catalogName(name) + '.txt';
      await copyFile(path.join(nativeRoot, 'release/licenses', catalog), path.join(licenseRoot, catalog)); copied.push(catalog);
    }
    rows.push(`| ${name} | ${version} | ${copyright} | ${copied.map(f => `[${f}](licenses/${f})`).join(', ')} |`);
  }
  await copyFile(path.join(nativeRoot, 'release/licenses/sources.json'), path.join(licenseRoot, 'sources.json'));
  rows.push('', 'The patched SIPSorcery source license is also provided as `SIPSorcery.LICENSE.md`; native Opus terms are in `Opus.LICENSE.txt`.', 'Microsoft runtime license/third-party notices, when bundled, are included with the runtime.');
  await writeFile(path.join(directory, 'THIRD-PARTY-NOTICES.md'), rows.join('\n')+'\n');
  for (const name of ['README.md', '시작하기.md', '베타안내.md']) await copyFile(path.join(nativeRoot, 'release', name), path.join(directory, name));
  const probePath = path.join(nativeRoot, '.local', 'beta-identity-' + randomUUID() + '.json');
  const exe = path.join(directory, 'VoiceNative.exe');
  execFileSync(exe, ['--sdk-build-info', probePath], { windowsHide: true, timeout: 15000 });
  const sdk = JSON.parse(await readFile(probePath, 'utf8'));
  if (!sdk.ProductionPatched) throw new Error('Published package did not load the patched SDK');
  execFileSync(exe, ['--codec-build-info', probePath], { windowsHide: true, timeout: 15000, env: { ...process.env, VOICE_OPUS_BACKEND: 'native' } });
  const codec = JSON.parse(await readFile(probePath, 'utf8'));
  if (!codec.NativeActive) throw new Error('Published native Opus unavailable');
  let head = null, dirty = null;
  try {
    head = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: nativeRoot, encoding: 'utf8', windowsHide: true }).trim();
    dirty = !!execFileSync('git', ['status', '--porcelain'], { cwd: nativeRoot, encoding: 'utf8', windowsHide: true }).trim();
  } catch { /* ZIP checkout */ }
  const names = await filesIn(directory); checkPackagePaths(names);
  checkAssets(names, selfContained);
  const files = [];
  for (const name of names) { const bytes = await readFile(path.join(directory, name)); files.push({ path: name, bytes: bytes.length, sha256: hash(bytes) }); }
  const manifest = { schemaVersion: 1, channel: 'beta', version, rid: 'win-x64', selfContained,
    createdUtc: new Date().toISOString(), sourceHead: head, sourceDirty: dirty, sdk, codec, files };
  await writeFile(path.join(directory, 'release-manifest.json'), JSON.stringify(manifest, null, 2)+'\n');
  await verifyPackage(directory); return manifest;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [mode, directory, root, selfContained] = process.argv.slice(2);
  if (mode === 'prepare') { const m = await prepareBeta(directory, root, selfContained === 'true'); console.log(`PASS beta package: ${m.files.length} files; patched SDK/native Opus loaded`); }
  else if (mode === 'verify') { const m = await verifyPackage(directory); console.log(`PASS beta inventory/hashes: ${m.files.length}`); }
  else throw new Error('Use prepare DIRECTORY NATIVE_ROOT true|false or verify DIRECTORY');
}
