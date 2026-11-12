# SPDX-License-Identifier: GPL-2.0-or-later
param([ValidateSet('all', 'lib', 'qemu')][string]$Only = 'all', [ValidateSet('release', 'dev')][string]$Profile = 'release')
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\environment.ps1"
$P = "$Root\sources\reims"; $Dist = "$Root\build\port-dist"
$Logs = "$Root\logs"; New-Item -ItemType Directory -Force $Logs | Out-Null

function Invoke-Msys([string]$Command, [string]$Log) {
    $env:MSYSTEM = 'UCRT64'; $env:CHERE_INVOKING = '1'; $env:MSYS2_PATH_TYPE = 'inherit'
    & "$Msys\usr\bin\bash.exe" -lc $Command *> $Log
    if ($LASTEXITCODE -ne 0) { Get-Content $Log -Tail 30; throw "failed: $Command (log: $Log)" }
}

if ($Only -in 'all', 'lib') {
    Set-Location $P
    $flag = if ($Profile -eq 'release') { '--release' } else { '' }
    $dir = if ($Profile -eq 'release') { 'release' } else { 'debug' }
    cargo build --locked $flag -p reims-vgpu-c --lib --message-format short *> "$Logs\port-lib-build.log"
    if ($LASTEXITCODE -ne 0) {
        Get-Content "$Logs\port-lib-build.log" | Select-String ': error|^error' | Select-Object -First 40 | ForEach-Object { $_.Line }
        throw "library build failed (log: $Logs\port-lib-build.log)"
    }
    $built = "$env:CARGO_TARGET_DIR\$dir"
    New-Item -ItemType Directory -Force "$Dist\bin", "$Dist\lib\pkgconfig", "$Dist\include" | Out-Null
    Copy-Item "$built\reims_vgpu_c.dll" "$Dist\bin\" -Force
    Copy-Item "$built\libreims_vgpu_c.dll.a" "$Dist\lib\" -Force
    Copy-Item "$P\crates\reims-vgpu-c\include\reims_vgpu.h" "$Dist\include\" -Force
    @'
# Written by scripts\build-port.ps1: QEMU links the library through its DLL import library.
prefix=${pcfiledir}/../..
libdir=${prefix}/lib
includedir=${prefix}/include

Name: reims-vgpu
Description: The host side of the reims-graphics device, the GPU macOS guests drive
Version: 0.1.0
Libs: -L${libdir} -lreims_vgpu_c.dll
Libs.private:
Cflags: -I${includedir}
'@ | Set-Content "$Dist\lib\pkgconfig\reims-vgpu.pc" -Encoding ascii
    "library: $((Get-Item "$Dist\bin\reims_vgpu_c.dll").Length) bytes -> $Dist"
}

if ($Only -in 'all', 'qemu') {
    $b = "$Root\build\port-qemu"; New-Item -ItemType Directory -Force $b, "$Root\build\scratch" | Out-Null
    $unixRoot = (& "$Msys/usr/bin/cygpath.exe" -u $Root).Trim()
    if ($unixRoot.Contains("'")) { throw "Checkout path must not contain apostrophes" }
    $pc = "$unixRoot/build/port-dist/lib/pkgconfig"
    $common = ('root=' + "'" + $unixRoot + "'" + '; export TEMP="$root/build/scratch" TMP="$root/build/scratch" TMPDIR="$root/build/scratch"; cd "$root/build/port-qemu"; export PKG_CONFIG_PATH="' + $pc + ':$PKG_CONFIG_PATH"; ')
    # (Re)configure when there is no build yet or it was configured against another package tree.
    $configured = (Test-Path "$b\build.ninja") -and (Select-String -Path "$b\build.ninja" -Pattern 'port-dist' -Quiet)
    if (!$configured) {
        Invoke-Msys ($common + 'WINDRES=llvm-windres CC_LD=lld CXX_LD=lld "$root/sources/qemu/configure" --target-list=x86_64-softmmu --cc=clang --cxx=clang++ --extra-ldflags=-fuse-ld=lld --enable-whpx --disable-kvm --disable-hvf --enable-reims-vgpu --enable-libusb --enable-sdl --enable-slirp --disable-gtk --disable-vnc --disable-spice --disable-gnutls --disable-curses --disable-nettle --disable-rust --disable-werror') "$Logs\port-qemu-configure.log"
    }
    Invoke-Msys ($common + 'ninja -j16 qemu-system-x86_64.exe qemu-img.exe') "$Logs\port-qemu-build.log"
    Copy-Item "$Dist\bin\reims_vgpu_c.dll" "$b\" -Force
    "qemu: $((Get-Item "$b\qemu-system-x86_64.exe").Length) bytes -> $b"
}
