"""Download checksum-verified portable Node runtimes and test the version matrix.

Python 3.12+; standard library only. Does not change the system Node installation.
"""
import argparse
import concurrent.futures
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import platform
import socket
import shutil
import subprocess
import tarfile
import time
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent
VERSIONS = ['22.0.0', '22.7.0', '22.8.0', '22.23.0', '23.6.0', '24.0.0', '24.15.0']


def fetch(url):
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def prepare(version, target, arch, cache_root=None):
    suffix = '.zip' if target == 'win' else '.tar.gz'
    name = f'node-v{version}-{target}-{arch}'
    shared_cache = ROOT / '.compat' / f'{target}-{arch}'
    cache = Path(cache_root) if cache_root else shared_cache
    cache.mkdir(parents=True, exist_ok=True)
    executable = cache / name / ('node.exe' if target == 'win' else 'bin/node')
    marker = cache / name / '.verified.sha256'
    if executable.exists() and marker.exists():
        return executable
    archive_name = name + suffix
    base = f'https://nodejs.org/dist/v{version}/'
    sums = fetch(base + 'SHASUMS256.txt').decode()
    expected = next(line.split()[0] for line in sums.splitlines() if line.split()[-1] == archive_name)
    destination = cache / archive_name
    existing_archive = shared_cache / archive_name
    if not destination.exists():
        if existing_archive.exists():
            shutil.copyfile(existing_archive, destination)
        else:
            partial = cache / (archive_name + '.partial')
            with urllib.request.urlopen(base + archive_name, timeout=60) as response, partial.open('wb') as output:
                shutil.copyfileobj(response, output, length=1024 * 1024)
            partial.replace(destination)
    with destination.open('rb') as archive:
        if hashlib.file_digest(archive, 'sha256').hexdigest() != expected:
            raise RuntimeError(f'{archive_name}: SHA256 mismatch')
    if target == 'win':
        with zipfile.ZipFile(destination) as bundle:
            for member in bundle.infolist():
                if not (cache / member.filename).resolve().is_relative_to(cache.resolve()):
                    raise RuntimeError('Unsafe archive path')
            bundle.extractall(cache)
    else:
        with tarfile.open(destination) as bundle:
            bundle.extractall(cache, filter='data')
    marker.write_text(expected, encoding='ascii')
    return executable


def run(command, env):
    result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True,
                            encoding='utf-8', errors='replace', timeout=90)
    return {'exitCode': result.returncode, 'output': result.stdout + result.stderr}


def smoke(node, env):
    with socket.socket() as reserve:
        reserve.bind(('127.0.0.1', 0))
        port = reserve.getsockname()[1]
    configured = [{'urls': 'stun:example.invalid:19302'}]
    process = subprocess.Popen([str(node), 'server.js'], cwd=ROOT,
                               env={**env, 'HOST': '127.0.0.1', 'PORT': str(port),
                                    'ICE_SERVERS': json.dumps(configured)},
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    try:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(process.communicate()[1].decode(errors='replace'))
            try:
                # Local traffic must bypass any outbound proxy configuration.
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
                with opener.open(f'http://127.0.0.1:{port}/api/config', timeout=1) as response:
                    config = json.load(response)
                if config != {'iceServers': configured}:
                    raise RuntimeError(f'Unexpected config: {config}')
                with opener.open(f'http://127.0.0.1:{port}/', timeout=1) as response:
                    assert '통화방 입장' in response.read().decode()
                return {'passed': True, 'checks': ['server.js entry point', 'HOST', 'PORT', 'ICE_SERVERS', 'HTTP page', 'HTTP config']}
            except (urllib.error.URLError, TimeoutError):
                time.sleep(0.1)
        raise RuntimeError('Server startup timed out')
    except Exception as error:
        return {'passed': False, 'error': str(error)}
    finally:
        process.terminate()
        try:
            process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()


def verify(node, version):
    directory = node.parent
    env = {**os.environ, 'PATH': str(directory) + os.pathsep + os.environ.get('PATH', '')}
    npm_cli = (directory / 'node_modules/npm/bin/npm-cli.js' if os.name == 'nt'
               else directory.parent / 'lib/node_modules/npm/bin/npm-cli.js')
    record = {'nodeRequested': version,
              'node': run([str(node), '--version'], env),
              'npm': run([str(node), str(npm_cli), '--version'], env)}
    record['npmTest'] = run([str(node), str(npm_cli), 'test'], env)
    flags = ['--test']
    record['versionSpecificCommand'] = 'node ' + ' '.join(flags)
    record['versionSpecificTest'] = run([str(node), *flags], env)
    record['startup'] = smoke(node, env)
    builtin = "await Promise.all(['node:http','node:fs/promises','node:crypto','node:url','node:test','node:assert/strict','node:vm'].map(name=>import(name)));console.log('builtins OK')"
    record['builtinImports'] = run([str(node), '--input-type=module', '-e', builtin], env)
    for filename in ['server.js', 'public/app.js']:
        record[f'syntax:{filename}'] = run([str(node), '--check', filename], env)
    print(f"{platform.system()}/{platform.machine()} Node {version}: npm test={record['npmTest']['exitCode']}, version-specific={record['versionSpecificTest']['exitCode']}, startup={record['startup']['passed']}", flush=True)
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--versions', nargs='+', default=VERSIONS)
    parser.add_argument('--output', default='artifacts/compatibility-results.json')
    parser.add_argument('--cache-dir', help='Optional portable-runtime cache; Linux /tmp avoids slow WSL shared-folder extraction')
    parser.add_argument('--download-workers', type=int, default=1, choices=[1, 2, 3], help='Use 1 on small OCI instances to limit extraction memory/CPU')
    args = parser.parse_args()
    target = 'win' if os.name == 'nt' else 'linux'
    arch = {'AMD64': 'x64', 'x86_64': 'x64', 'aarch64': 'arm64', 'ARM64': 'arm64'}[platform.machine()]
    if platform.system() not in ('Windows', 'Linux'):
        raise RuntimeError('Only Windows and Linux are supported by this verification script')
    prepared = {}
    records = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.download_workers) as pool:
        futures = {pool.submit(prepare, version, target, arch, args.cache_dir): version for version in args.versions}
        for future in concurrent.futures.as_completed(futures):
            version = futures[future]
            try:
                prepared[version] = future.result()
                print(f'Prepared checksum-verified Node {version} ({target}-{arch})', flush=True)
            except Exception as error:
                records.append({'nodeRequested': version, 'preparationError': str(error)})
    for version in args.versions:
        if version in prepared:
            try:
                records.append(verify(prepared[version], version))
            except Exception as error:
                records.append({'nodeRequested': version, 'verificationError': str(error)})
    output = ROOT / args.output
    output.parent.mkdir(parents=True, exist_ok=True)
    package = json.loads((ROOT / 'package.json').read_text(encoding='utf-8'))
    output.write_text(json.dumps({'date': datetime.now(timezone(timedelta(hours=9))).date().isoformat(), 'platform': platform.platform(),
                                 'architecture': arch, 'package': package,
                                 'results': records}, indent=2, ensure_ascii=False), encoding='utf-8')
    failures = any('preparationError' in record or 'verificationError' in record
                   or record['npmTest']['exitCode'] or record['versionSpecificTest']['exitCode']
                   or not record['startup']['passed'] or record['builtinImports']['exitCode']
                   or record['syntax:server.js']['exitCode'] or record['syntax:public/app.js']['exitCode']
                   for record in records)
    print(f'Report: {output}', flush=True)
    raise SystemExit(1 if failures else 0)


if __name__ == '__main__':
    main()
