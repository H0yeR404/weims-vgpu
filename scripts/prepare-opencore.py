#!/usr/bin/env python3
"""Inject the pinned Reims guest set into an extracted, working OpenCore EFI tree."""
import argparse
import pathlib
import plistlib
import shutil
import tomllib

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('efi', type=pathlib.Path, help='EFI directory containing OC/config.plist')
a = p.parse_args()
prebuilt = pathlib.Path(__file__).resolve().parents[1] / 'sources/reims/guest/prebuilt'
config = a.efi / 'OC/config.plist'
c = plistlib.loads(config.read_bytes())
entries = tomllib.loads((prebuilt / 'set.toml').read_text(encoding='utf-8'))['kext']
backup = config.with_suffix('.plist.before-weims')
if backup.exists():
    raise SystemExit('Backup already exists; use a fresh copy of your EFI tree.')
old = c['Kernel']['Add']
names = {e['BundlePath'] for e in entries}
if any(e.get('Enabled') and e['BundlePath'] not in names for e in old):
    raise SystemExit('Unexpected enabled kexts: inspect dependencies before replacing Kernel/Add.')
shutil.copy2(config, backup)
add = []
for e in entries:
    shutil.copytree(prebuilt / e['BundlePath'], a.efi / 'OC/Kexts' / e['BundlePath'], dirs_exist_ok=True)
    add.append(dict(Arch=e['Arch'], BundlePath=e['BundlePath'], Comment=e['Comment'],
                    Enabled=True, ExecutablePath=e['ExecutablePath'],
                    MaxKernel=e['MaxKernel'], MinKernel=e['MinKernel'], PlistPath=e['PlistPath']))
c['Kernel']['Add'] = add
c['Misc']['Security']['SecureBootModel'] = 'Disabled'
config.write_bytes(plistlib.dumps(c, sort_keys=False))
print('Injected:', ', '.join(e['BundlePath'] for e in entries))
