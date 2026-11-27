# SPDX-License-Identifier: GPL-2.0-or-later
# Replaces libusb-1.0.dll in a runtime directory with scripts/libusb-usbdk.c, which selects the UsbDk
# backend after libusb_init. The original DLL is kept beside it as libusb_orig.dll. QEMU 11 never asks
# for UsbDk, and without this the ports it sees are not the ones Manager records.
param([Parameter(Mandatory = $true)][string]$Directory)
$ErrorActionPreference = 'Stop'
$dll = Join-Path $Directory 'libusb-1.0.dll'
$orig = Join-Path $Directory 'libusb_orig.dll'
if (!(Test-Path -LiteralPath $dll)) { throw "No libusb-1.0.dll in $Directory" }
$marker = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dll))
if ($marker.Contains('libusb_orig.dll')) { 'libusb already selects UsbDk'; return }

function RvaOffset([byte[]]$b, [int]$rva, [int]$section, [int]$count) {
    for ($i = 0; $i -lt $count; $i++) {
        $s = $section + $i * 40
        $va = [BitConverter]::ToInt32($b, $s + 12); $vs = [BitConverter]::ToInt32($b, $s + 8); $raw = [BitConverter]::ToInt32($b, $s + 20)
        if ($rva -ge $va -and $rva -lt ($va + $vs)) { return $raw + ($rva - $va) }
    }
    throw "Export address $rva is outside the sections"
}
$bytes = [IO.File]::ReadAllBytes($dll)
$pe = [BitConverter]::ToInt32($bytes, 0x3C)
$opt = $pe + 24
$magic = [BitConverter]::ToUInt16($bytes, $opt)
$data = if ($magic -eq 0x20b) { $opt + 112 } else { $opt + 96 }
$expRva = [BitConverter]::ToInt32($bytes, $data)
$nsec = [BitConverter]::ToUInt16($bytes, $pe + 6)
$sec = $pe + 24 + [BitConverter]::ToUInt16($bytes, $pe + 20)
$exp = RvaOffset $bytes $expRva $sec $nsec
$nNames = [BitConverter]::ToInt32($bytes, $exp + 24)
$names = RvaOffset $bytes ([BitConverter]::ToInt32($bytes, $exp + 32)) $sec $nsec
$exports = for ($i = 0; $i -lt $nNames; $i++) {
    $start = RvaOffset $bytes ([BitConverter]::ToInt32($bytes, $names + $i * 4)) $sec $nsec
    $end = $start; while ($bytes[$end] -ne 0) { $end++ }
    [Text.Encoding]::ASCII.GetString($bytes, $start, $end - $start)
}
if ($exports -notcontains 'libusb_init') { throw 'libusb_init is not exported' }

$work = Join-Path ([IO.Path]::GetTempPath()) ("libusb-usbdk-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $plain = Join-Path $work 'libusb_orig.def'
    [IO.File]::WriteAllLines($plain, @('EXPORTS') + $exports)
    $forward = Join-Path $work 'libusb-usbdk.def'
    $lines = @('EXPORTS', 'libusb_init')
    foreach ($name in $exports) { if ($name -ne 'libusb_init') { $lines += "$name=libusb_orig.$name" } }
    [IO.File]::WriteAllLines($forward, $lines)
    $clang = Get-Command clang -ErrorAction SilentlyContinue
    if (!$clang -and $env:WEIMS_MSYS2) { $candidate = Join-Path $env:WEIMS_MSYS2 'clang64\bin\clang.exe'; if (Test-Path $candidate) { $clang = $candidate } }
    if (!$clang) { throw 'clang is required to build the UsbDk libusb wrapper' }
    $clang = if ($clang.Source) { $clang.Source } else { $clang }
    $lld = Join-Path (Split-Path $clang) 'lld-link.exe'
    $implib = Join-Path $work 'libusb_orig.lib'
    & $lld /lib "/def:$plain" "/out:$implib" /machine:x64
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the import library for libusb_orig.dll' }
    $out = Join-Path $work 'libusb-1.0.dll'
    & $clang -shared -nostdlib -O2 -o $out "$PSScriptRoot\libusb-usbdk.c" -lkernel32 $implib "-Wl,/DEF:$forward" "-Wl,/ENTRY:DllMain"
    if ($LASTEXITCODE -ne 0) { throw 'UsbDk libusb wrapper failed to build' }
    $staged = "$dll.usbdk"
    Copy-Item -LiteralPath $out -Destination $staged
    Move-Item -LiteralPath $dll -Destination $orig
    try { Move-Item -LiteralPath $staged -Destination $dll }
    catch { if (!(Test-Path -LiteralPath $dll) -and (Test-Path -LiteralPath $orig)) { Move-Item -LiteralPath $orig -Destination $dll }; throw }
} finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
"UsbDk selected by libusb in $Directory"
