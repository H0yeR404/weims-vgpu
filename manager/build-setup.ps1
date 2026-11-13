# SPDX-License-Identifier: GPL-2.0-or-later
# Run after build-display.ps1. Adds the pinned native setup helper and guest boot assets.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. "$root/scripts/environment.ps1"
$source = "$root/sources/reims-util"
$revision = '84e5c98ae7fa6b0b68e7be690525dda29697ab34'
if (!(Test-Path $source)) {
    git clone --no-checkout https://github.com/steelbrain/reims-util.git $source
    if ($LASTEXITCODE) { throw 'reims-util clone failed' }
    git -C $source config core.autocrlf false
    git -C $source checkout --detach $revision
    if ($LASTEXITCODE) { throw 'reims-util checkout failed' }
}
if ((git -C $source rev-parse HEAD).Trim() -ne $revision) { throw 'Expected pinned reims-util revision' }
git -C $source diff --quiet HEAD --
if ($LASTEXITCODE -ne 0 -or (git -C $source ls-files --others --exclude-standard)) { throw 'Expected unmodified reims-util source' }
$env:CARGO_TARGET_DIR = "$root/build/util-target"
cargo build --manifest-path "$source/Cargo.toml" --locked --release
if ($LASTEXITCODE) { throw 'reims-util build failed' }
$runtime = "$root/build/manager-display-runtime"
if (!(Test-Path "$runtime/weims-display-v1.json")) { throw 'Build display runtime first' }
Copy-Item "$env:CARGO_TARGET_DIR/release/reims-util.exe" $runtime
New-Item -ItemType Directory -Force "$runtime/boot" | Out-Null
foreach ($name in 'Graft.kext','ReimsGraphics.kext','RgfxGate.kext') {
    if (Test-Path "$runtime/boot/$name") { throw "Boot asset already exists; inspect before rebuilding: $name" }
    Copy-Item "$root/sources/reims/guest/prebuilt/$name" "$runtime/boot/$name" -Recurse
}
Copy-Item "$root/sources/reims/guest/prebuilt/set.toml" "$runtime/boot/"
Copy-Item "$root/sources/reims/firmware/prebuilt/ReimsGraphics.rom" "$runtime/boot/"
Write-Output "Setup helper and matching guest drivers: $runtime"
