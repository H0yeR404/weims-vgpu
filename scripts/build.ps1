# SPDX-License-Identifier: GPL-2.0-or-later
# Builds the runtime from sources/reims-vgpu (scripts/fetch.ps1) and assembles the folder Manager runs.
#
# The build is upstream's own (scripts/install-ctl.sh in MSYS2's CLANG64 shell): the library, QEMU linked
# with it, and the DLLs they load. Its install, cache and programs are kept under build/upstream, so a
# reims-ctl the user installed for themselves is untouched. reims-util is built from sources/reims-util,
# the commit upstream names with manager/patches/util.patch, on the toolchain upstream's installer uses;
# the unpatched one that installer puts beside its reims-ctl is not used.
param([switch]$AssembleOnly)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\environment.ps1"
$Logs = "$Root\logs"; New-Item -ItemType Directory -Force $Logs, $Upstream | Out-Null
if (!(Test-Path "$Source\scripts\install-ctl.sh")) { throw "Run scripts/fetch.ps1 first: $Source is missing" }
$Util = "$Root\sources\reims-util"; $UtilTarget = "$Upstream\cache\util-target"
if (!(Test-Path "$Util\Cargo.toml")) { throw "Run scripts/fetch.ps1 first: $Util is missing" }

if (!$AssembleOnly) {
    $unix = (& "$Msys\usr\bin\cygpath.exe" -u $Source).Trim()
    if ($unix -match "['\s]") { throw 'The checkout path must not contain spaces or apostrophes' }
    $saved = @{}
    $set = @{
        MSYSTEM = 'CLANG64'; CHERE_INVOKING = '1'
        XDG_DATA_HOME = "$Upstream\data"; XDG_CACHE_HOME = "$Upstream\cache"; CARGO_INSTALL_ROOT = "$Upstream\cargo"
        # The checks are of this host's ability to run VMs, which Manager makes itself.
        REIMS_INSTALL_SKIP_HOST_CHECKS = '1'
    }
    foreach ($name in $set.Keys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $set[$name]) }
    try {
        & "$Msys\usr\bin\bash.exe" -lc "cd '$unix' && scripts/install-ctl.sh --yes" *> "$Logs\upstream-build.log"
        if ($LASTEXITCODE -ne 0) { Get-Content "$Logs\upstream-build.log" -Tail 30; throw "Upstream build failed (log: $Logs\upstream-build.log)" }
        $path = { param($p) (& "$Msys\usr\bin\cygpath.exe" -u $p).Trim() }
        $channel = [regex]::Match((Get-Content "$Source\rust-toolchain.toml" -Raw), '(?m)^channel *= *"([^"]+)"').Groups[1].Value
        if (!$channel) { throw "$Source\rust-toolchain.toml names no channel" }
        $command = "cd '$(& $path $Util)' && export PATH='$(& $path "$env:USERPROFILE\.cargo\bin")':`$PATH RUSTUP_TOOLCHAIN=$channel-x86_64-pc-windows-gnullvm CC=clang CXX=clang++ AR=llvm-ar CARGO_TARGET_DIR='$(& $path $UtilTarget)' && cargo build --locked --release"
        & "$Msys\usr\bin\bash.exe" -lc $command *> "$Logs\util-build.log"
        if ($LASTEXITCODE -ne 0) { Get-Content "$Logs\util-build.log" -Tail 30; throw "reims-util build failed (log: $Logs\util-build.log)" }
    }
    finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
}

$qemu = "$Upstream\data\reims-ctl\qemu\current"
$stage = "$Runtime.new"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage, "$stage\pc-bios", "$stage\boot" | Out-Null
Copy-Item "$qemu\bin\*.dll" $stage
foreach ($name in 'qemu-system-x86_64.exe', 'qemu-img.exe', 'reims-vgpu-compile.exe') { Copy-Item -LiteralPath "$qemu\bin\$name" $stage }
foreach ($name in 'bios-256k.bin', 'kvmvapic.bin', 'efi-virtio.rom', 'pxe-virtio.rom') { Copy-Item -LiteralPath "$qemu\share\$name" "$stage\pc-bios" }
Copy-Item -LiteralPath "$UtilTarget\release\reims-util.exe" $stage
foreach ($name in 'Graft.kext', 'ReimsGraphics.kext', 'RgfxGate.kext', 'set.toml') { Copy-Item -Recurse -LiteralPath "$Source\guest\prebuilt\$name" "$stage\boot" }
Copy-Item -LiteralPath "$Source\firmware\prebuilt\ReimsGraphics.rom" "$stage\boot"

# What Manager reads to know this is its own build. The size and hash tie the record to this QEMU,
# which holds the library: a folder with another QEMU dropped in is not taken for this build.
# capabilities names what this project's patches add that Manager has a setting for: the first three are
# manager/patches/window.patch's, mac-models is util.patch's.
$exe = Get-Item "$stage\qemu-system-x86_64.exe"
[ordered]@{
    format = 1
    source = "reims-vgpu " + (git -C $Source rev-parse HEAD)
    capabilities = @('free-pointer', 'full-screen', 'super-key', 'mac-models')
    qemu_bytes = $exe.Length
    qemu_sha256 = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
} | ConvertTo-Json | Set-Content "$stage\weims-runtime.json" -Encoding utf8NoBOM

if (Test-Path $Runtime) { Remove-Item -Recurse -Force $Runtime }
Rename-Item $stage (Split-Path $Runtime -Leaf)
"runtime: $Runtime ($([math]::Round(((Get-ChildItem $Runtime -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))) MiB)"
