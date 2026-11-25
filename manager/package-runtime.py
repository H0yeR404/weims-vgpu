#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Package the runtime scripts/build.ps1 assembled (build/weims-runtime) with its licenses, and the
source material that corresponds to it.

--runtime-only writes the runtime ZIP alone, for local tests: no source archive, no download, and
manager/runtime-catalog.json is left as it is.
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
import tempfile
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--msys', required=True, type=Path, help='the MSYS2 installation the runtime was built in')
parser.add_argument('--version', required=True)
parser.add_argument('--runtime-only', action='store_true')
parser.add_argument('--required', action='store_true', help='VMs cannot start on an older managed runtime: Manager has it replaced first')
args = parser.parse_args()
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[a-z0-9.]+)?', args.version):
    raise ValueError('Invalid release version')
version = args.version
root = Path(__file__).resolve().parent.parent
out = root / 'manager/artifacts/release'
out.mkdir(parents=True, exist_ok=True)
msys = args.msys.resolve()
runtime = root / 'build/weims-runtime'
reims = root / 'sources/reims-vgpu'
qemu = reims / 'vendor/qemu'
util = root / 'sources/reims-util'


def run(*cmd, **kwargs):
    return subprocess.check_output([str(c) for c in cmd], text=True, encoding='utf-8', errors='replace', **kwargs).strip()


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


# The runtime must be the one the sources on disk build: its record names the commit and QEMU's hash.
record = json.loads((runtime / 'weims-runtime.json').read_text(encoding='utf-8-sig'))
head = run('git', '-C', reims, 'rev-parse', 'HEAD')
if record['source'] != 'reims-vgpu ' + head:
    raise RuntimeError('build/weims-runtime was built from another commit than sources/reims-vgpu: run scripts/build.ps1')
if record['qemu_sha256'] != sha256(runtime / 'qemu-system-x86_64.exe'):
    raise RuntimeError('weims-runtime.json does not match qemu-system-x86_64.exe: run scripts/build.ps1')
for required in ['qemu-img.exe', 'reims-vgpu-compile.exe', 'reims-util.exe', 'boot/ReimsGraphics.rom', 'boot/set.toml', 'pc-bios/bios-256k.bin']:
    if not (runtime / required).is_file():
        raise RuntimeError('Missing runtime input: ' + required)
for source in json.loads((root / 'sources.json').read_text(encoding='utf-8')):
    patch = source.get('patch')
    if patch and run('git', '-C', root / 'sources' / source['name'], 'apply', '--reverse', '--check', root / patch, stderr=subprocess.STDOUT):
        raise RuntimeError('Patch is not applied to its source: ' + patch)
# reims-util is the one upstream's reims-ctl names; the build installed it at that commit.
util_rev = re.search(r'pub const REV: &str\s*=\s*"([0-9a-f]{40})"', (reims / 'crates/reims-ctl/src/reims_util.rs').read_text(encoding='utf-8')).group(1)

stage = Path(tempfile.mkdtemp(prefix='runtime-' + version + '-', dir=root / 'build'))
shutil.copytree(runtime, stage, dirs_exist_ok=True)
# Each DLL is a CLANG64 package's file, copied beside QEMU by upstream's installer.
packages = {}
for dll in sorted(runtime.glob('*.dll')):
    if not (msys / 'clang64/bin' / dll.name).is_file():
        raise RuntimeError('DLL is not from ' + str(msys) + ': ' + dll.name)
    owner = run(msys / 'usr/bin/pacman.exe', '-Qoq', '/clang64/bin/' + dll.name)
    package_version = run(msys / 'usr/bin/pacman.exe', '-Q', owner).split()[1]
    desc = (msys / 'var/lib/pacman/local' / (owner + '-' + package_version) / 'desc').read_text(encoding='utf-8')
    packages[re.search(r'%BASE%\n([^\n]+)', desc).group(1)] = package_version
shutil.copytree(msys / 'clang64/share/licenses', stage / 'licenses/MSYS2')
shutil.copytree(root / 'licenses', stage / 'licenses/upstream')
shutil.copytree(reims / 'LICENSES', stage / 'licenses/reims-vgpu/LICENSES')
shutil.copy2(reims / 'LICENSE', stage / 'licenses/reims-vgpu/LICENSE')
shutil.copytree(reims / 'guest/graft/LICENSES', stage / 'licenses/Graft/LICENSES')
shutil.copy2(reims / 'guest/graft/LICENSE', stage / 'licenses/Graft/LICENSE')
shutil.copy2(root / 'LICENSE', stage)
shutil.copy2(root / 'NOTICE.md', stage)

manifest = []
if not args.runtime_only:
    if run('git', '-C', util, 'rev-parse', 'HEAD') != util_rev:
        raise RuntimeError('sources/reims-util is not at the commit reims-ctl names (' + util_rev + '): update sources.json and fetch it')
    shutil.copytree(util / 'LICENSES', stage / 'licenses/reims-util/LICENSES')
    shutil.copy2(util / 'LICENSE', stage / 'licenses/reims-util/LICENSE')
    source_dir = root / 'build/release-sources' / head[:12]
    source_dir.mkdir(parents=True, exist_ok=True)
    # The crates the library, the helper and reims-util are built from, metal2vulkan and SDL3 among them.
    vendor = source_dir / 'vendor'
    if not vendor.exists():
        channel = re.search(r'^channel\s*=\s*"([^"]+)"', (reims / 'rust-toolchain.toml').read_text(encoding='utf-8'), re.M).group(1)
        environment = dict(os.environ, RUSTUP_TOOLCHAIN=channel + '-x86_64-pc-windows-gnullvm')
        subprocess.check_call(['cargo', 'vendor', '--locked', '--versioned-dirs', '--sync', str(util / 'Cargo.toml'), str(vendor) + '.partial'], cwd=reims, env=environment, stdout=subprocess.DEVNULL)
        Path(str(vendor) + '.partial').rename(vendor)

    def download(item):
        name, url = item
        target = source_dir / name
        if not target.exists():
            with urllib.request.urlopen(url, timeout=90) as response, target.with_suffix('.partial').open('wb') as output:
                shutil.copyfileobj(response, output)
            target.with_suffix('.partial').replace(target)
        print('Source:', name, flush=True)
        return dict(file=name, url=url, sha256=sha256(target))

    downloads = [(base + '-' + v + '.src.tar.zst', 'https://repo.msys2.org/mingw/sources/' + base + '-' + v + '.src.tar.zst') for base, v in packages.items()]
    # The firmware files under pc-bios are built from these submodules of QEMU, at the commits QEMU names.
    for name, repository in [('seabios', 'coreboot/seabios'), ('ipxe', 'ipxe/ipxe')]:
        commit = run('git', '-C', qemu, 'ls-tree', 'HEAD', 'roms/' + name).split()[2]
        downloads.append((f'{name}-{commit[:8]}.tar.gz', f'https://api.github.com/repos/{repository}/tarball/{commit}'))
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        manifest = list(pool.map(download, downloads))
    for path in vendor.rglob('*'):
        if path.is_file() and path.name.upper().startswith(('LICENSE', 'COPYING', 'NOTICE', 'AUTHORS', 'COPYRIGHT')):
            dest = stage / 'licenses/Cargo' / path.relative_to(vendor)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, dest)
    (stage / 'dependency-sources.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    with zipfile.ZipFile(out / f'weims-runtime-{version}-sources.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
        for path in sorted(source_dir.rglob('*')):
            if path.is_file() and '.partial' not in path.name:
                archive.write(path, str(Path('dependencies') / path.relative_to(source_dir)))
        for name, source in [('reims-vgpu', reims), ('reims-vgpu/vendor/qemu', qemu), ('reims-util', util)]:
            for relative in sorted(set(run('git', '-C', source, 'ls-files', '--cached', '--others', '--exclude-standard').splitlines())):
                if (source / relative).is_file():
                    archive.write(source / relative, str(Path('sources') / name / relative))
        # What QEMU's build fetched into its own tree.
        for subproject in sorted((qemu / 'subprojects').iterdir()):
            if subproject.is_dir() and (subproject / '.git').exists():
                for path in sorted(subproject.rglob('*')):
                    if path.is_file() and '.git' not in path.relative_to(subproject).parts:
                        archive.write(path, str(Path('sources/reims-vgpu/vendor/qemu/subprojects') / subproject.name / path.relative_to(subproject)))
        for folder in ['scripts', 'licenses', 'manager/patches']:
            for path in sorted((root / folder).rglob('*')):
                if path.is_file():
                    archive.write(path, str(path.relative_to(root)))
        for relative in ['sources.json', 'NOTICE.md', 'LICENSE', 'docs/reproduce.md', 'manager/package-runtime.py']:
            archive.write(root / relative, relative)
        archive.writestr('dependency-sources.json', json.dumps(manifest, indent=2))
        archive.writestr('BUILD-SOURCES.txt', f'sources/reims-vgpu is upstream reims-vgpu at {head} with the patches sources.json lists already applied; do not apply them twice. vendor/qemu inside it is the QEMU it builds. Build with scripts/build.ps1, which runs upstream\'s scripts/install-ctl.sh in an MSYS2 CLANG64 shell and assembles the runtime folder. sources/reims-util is the helper at {util_rev}, which that build installs. dependencies/vendor holds the Cargo dependencies of both, metal2vulkan and SDL3 among them; configure source replacement for offline builds. dependencies/*.src.tar.zst are the MSYS2 source packages of the DLLs shipped. The SeaBIOS and iPXE archives are the firmware sources at the commits QEMU names. Manager and CLI sources are in repository tag v{version}. No macOS image is distributed.\n')

# The folder must run with nothing but Windows around it.
env = dict(os.environ, PATH=str(stage) + ';' + os.environ['SystemRoot'] + '\\System32;' + os.environ['SystemRoot'])
for exe, arguments in [('qemu-system-x86_64.exe', ['--version']), ('qemu-img.exe', ['--version']), ('reims-util.exe', ['--version']), ('qemu-system-x86_64.exe', ['-L', str(stage / 'pc-bios'), '-device', 'reims-graphics-pci,help'])]:
    result = subprocess.run([str(stage / exe), *arguments], env=env, capture_output=True, text=True, timeout=60)
    if result.returncode:
        raise RuntimeError(result.stderr + result.stdout)
    print(result.stdout[:180], flush=True)
runtime_zip = out / f'weims-runtime-{version}-win-x64.zip'
with zipfile.ZipFile(runtime_zip, 'w', zipfile.ZIP_DEFLATED, compresslevel=6, strict_timestamps=False) as archive:
    for path in sorted(stage.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(stage))
shutil.rmtree(stage)
if args.runtime_only:
    print('Runtime ZIP for local tests (catalog unchanged):', runtime_zip, runtime_zip.stat().st_size, flush=True)
else:
    catalog = json.loads((root / 'manager/runtime-catalog.json').read_text(encoding='utf-8'))
    catalog['Runtime'] = dict(Version=f'weims-runtime-{version}', Url=f'https://github.com/H0yeR404/weims-vgpu/releases/download/v{version}/' + runtime_zip.name, Sha256=sha256(runtime_zip), MaxBytes=runtime_zip.stat().st_size, Required=args.required)
    (root / 'manager/runtime-catalog.json').write_text(json.dumps(catalog, indent=2), encoding='utf-8')
    print('Runtime, sources and pinned catalog prepared:', out, flush=True)
