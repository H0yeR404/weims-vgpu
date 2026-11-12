#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Package an already built Windows runtime, its dependency closure and source material.

Run cargo vendor --locked build/release-sources/vendor from sources/reims first.
The output is local until all source downloads and executable checks have passed.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import urllib.request
import zipfile
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--msys', required=True, type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
out = root / 'manager/artifacts/release'
out.mkdir(parents=True, exist_ok=True)
stage = Path(tempfile.mkdtemp(prefix='runtime-1.0-', dir=root / 'build'))
msys = args.msys.resolve()

def run(*cmd):
    return subprocess.check_output(cmd, text=True, encoding='utf-8', errors='replace').strip()

queue = list((root / 'build/manager-runtime').glob('*.exe')) + list((root / 'build/manager-runtime').glob('*.dll'))
files = {}
while queue:
    path = queue.pop()
    if path.name.lower() in files:
        continue
    files[path.name.lower()] = path
    for name in re.findall(r'DLL Name:\s*(\S+)', run(str(msys / 'ucrt64/bin/objdump.exe'), '-p', str(path))):
        if (path.parent / name).exists():
            queue.append(path.parent / name)
        elif (msys / 'ucrt64/bin' / name).exists():
            queue.append(msys / 'ucrt64/bin' / name)
        elif not name.lower().startswith(('api-ms-', 'ext-ms-')) and not (Path(os.environ['SystemRoot']) / 'System32' / name).exists():
            raise RuntimeError('Missing dependency ' + name)

packages = {}
for path in files.values():
    shutil.copy2(path, stage / path.name)
    if msys in path.parents:
        owner = run(str(msys / 'usr/bin/pacman.exe'), '-Qoq', '/ucrt64/bin/' + path.name)
        version = run(str(msys / 'usr/bin/pacman.exe'), '-Q', owner).split()[1]
        desc = (msys / 'var/lib/pacman/local' / (owner + '-' + version) / 'desc').read_text(encoding='utf-8')
        base = re.search(r'%BASE%\n([^\n]+)', desc).group(1)
        packages[base] = version

shutil.copy2(root / 'build/manager-runtime/weims-input-v1.json', stage)
# Retain the original DLL, so the capability marker remains valid.
bios = stage / 'pc-bios'
bios.mkdir()
for name in ['bios-256k.bin', 'kvmvapic.bin', 'efi-virtio.rom', 'pxe-virtio.rom', 'README']:
    shutil.copy2(root / 'sources/qemu/pc-bios' / name, bios)
shutil.copytree(msys / 'ucrt64/share/licenses', stage / 'licenses/MSYS2')
shutil.copytree(root / 'licenses', stage / 'licenses/upstream')
shutil.copy2(root / 'LICENSE', stage)
shutil.copy2(root / 'NOTICE.md', stage)
source_dir = root / 'build/release-sources'
source_dir.mkdir(exist_ok=True)
manifest = []

def download(item):
    name, url = item
    target = source_dir / name
    if not target.exists():
        with urllib.request.urlopen(url, timeout=90) as response, target.with_suffix('.partial').open('wb') as output:
            shutil.copyfileobj(response, output)
        target.with_suffix('.partial').replace(target)
    digest = hashlib.sha256(target.read_bytes()).hexdigest()
    print('Source:', name, flush=True)
    return dict(file=name, url=url, sha256=digest)

downloads = [(base + '-' + version + '.src.tar.zst', 'https://repo.msys2.org/mingw/sources/' + base + '-' + version + '.src.tar.zst') for base, version in packages.items()]
downloads += [
    ('seabios-b52ca86e.tar.gz', 'https://api.github.com/repos/coreboot/seabios/tarball/b52ca86e094d19b58e2304417787e96b940e39c6'),
    ('ipxe-4bd064de.tar.gz', 'https://api.github.com/repos/ipxe/ipxe/tarball/4bd064de239dab2426b31c9789a1f4d78087dc63')]
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    manifest = list(pool.map(download, downloads))
(stage / 'dependency-sources.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
vendor = source_dir / 'vendor'
if not vendor.exists():
    raise RuntimeError('Run cargo vendor first')
for path in vendor.rglob('*'):
    if path.is_file() and path.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE', 'AUTHORS', 'COPYRIGHT')):
        dest = stage / 'licenses/Cargo' / path.relative_to(vendor)
        dest.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(path, dest)
with zipfile.ZipFile(out / 'weims-runtime-1.0-sources.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
    for path in sorted(source_dir.rglob('*')):
        if path.is_file() and '.partial' not in path.name:
            archive.write(path, str(Path('dependencies') / path.relative_to(source_dir)))
    for name in ['reims', 'qemu', 'metal2vulkan']:
        source = root / 'sources' / name
        paths = run('git', '-C', str(source), 'ls-files', '--cached', '--others', '--exclude-standard').splitlines()
        for relative in sorted(set(paths)):
            path = source / relative
            if path.is_file():
                archive.write(path, str(Path('sources') / name / relative))
    for name in ['dtc', 'keycodemapdb']:
        source = root / 'sources/qemu/subprojects' / name
        for path in sorted(source.rglob('*')):
            if path.is_file() and '.git' not in path.relative_to(source).parts:
                archive.write(path, str(Path('sources/qemu/subprojects') / name / path.relative_to(source)))
    for folder in ['scripts', 'patches', 'licenses']:
        for path in (root / folder).rglob('*'):
            if path.is_file(): archive.write(path, str(path.relative_to(root)))
    for relative in ['sources.json', 'NOTICE.md', 'LICENSE', 'docs/reproduce.md', 'manager/build-input.ps1', 'manager/patches/free-pointer.patch', 'manager/package-runtime.py']:
        archive.write(root / relative, relative)
    archive.writestr('dependency-sources.json', json.dumps(manifest, indent=2))
    archive.writestr('BUILD-SOURCES.txt', 'Sources under sources/ already include the downstream and input patches. Do not apply them twice. Build with scripts/build.ps1 using the toolchain and MSYS2 requirements in docs/reproduce.md. dependencies/vendor contains Cargo dependencies and licenses; configure Cargo source replacement to use this directory for offline crate access. dependencies/*.src.tar.zst contain exact MSYS2 source packages and PKGBUILDs. Firmware sources are the SeaBIOS/iPXE archives at the QEMU-pinned revisions. Manager sources and packaging scripts are in the v1.0 repository tag.\n')

# Verify the collected DLLs without access to the development MSYS2 installation.
env = os.environ.copy()
env['PATH'] = str(stage) + ';' + os.environ['SystemRoot'] + '\\System32;' + os.environ['SystemRoot']
for exe, arguments in [('qemu-system-x86_64.exe', ['--version']), ('qemu-img.exe', ['--version']), ('qemu-system-x86_64.exe', ['-device', 'reims-graphics-pci,help'])]:
    result = subprocess.run([str(stage / exe), *arguments], env=env, capture_output=True, text=True, timeout=30)
    if result.returncode: raise RuntimeError(result.stderr + result.stdout)
    print(result.stdout[:180], flush=True)
with zipfile.ZipFile(out / 'weims-runtime-1.0-win-x64.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
    for path in sorted(stage.rglob('*')):
        if path.is_file(): archive.write(path, path.relative_to(stage))
runtime_zip = out / 'weims-runtime-1.0-win-x64.zip'
catalog = dict(Runtime=dict(Version='weims-runtime-1.0', Url='https://github.com/H0yeR404/weims-vgpu/releases/download/v1.0/' + runtime_zip.name, Sha256=hashlib.sha256(runtime_zip.read_bytes()).hexdigest(), MaxBytes=runtime_zip.stat().st_size), PowerShell=dict(Version='powershell-7.7.0', Url='https://github.com/PowerShell/PowerShell/releases/download/v7.7.0/PowerShell-7.7.0-win-x64.zip', Sha256='7c74e03b20a75b59715847439d046a6214b1863ee79b187872980f139bb4a05f', MaxBytes=115330228))
(root / 'manager/runtime-catalog.json').write_text(json.dumps(catalog, indent=2), encoding='utf-8')
print('Runtime, sources and pinned catalog prepared:', out, flush=True)
