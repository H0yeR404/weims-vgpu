# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory)][string]$Disk,
    [Parameter(Mandatory)][string]$OpenCore,
    [Parameter(Mandatory)][string]$FirmwareCode,
    [Parameter(Mandatory)][string]$FirmwareVars,
    [string]$Vm = 'default', [int]$QmpPort = 4444, [int]$SshPort = 2222
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
if ($Vm -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid VM name' }
foreach ($p in @($Disk,$OpenCore,$FirmwareCode,$FirmwareVars)) {
    if (!(Test-Path -LiteralPath $p -PathType Leaf)) { throw "Missing file: $p" }
}
$dir = Join-Path $root "vm/$Vm"
if (Test-Path $dir) { throw "Refusing existing VM directory: $dir" }
New-Item -ItemType Directory -Path "$dir/state","$dir/firmware" | Out-Null
Copy-Item -LiteralPath $FirmwareVars -Destination "$dir/firmware/OVMF_VARS.fd"
Copy-Item -LiteralPath $FirmwareCode -Destination "$dir/firmware/OVMF_CODE.fd"
Copy-Item -LiteralPath $OpenCore -Destination "$dir/firmware/OpenCore.img"
$macBytes = New-Object byte[] 3
[Security.Cryptography.RandomNumberGenerator]::Fill($macBytes)
$mac = '52:54:00:' + (($macBytes | ForEach-Object { $_.ToString('x2') }) -join ':')
@{
    cpus = 8; memory_mib = 32768; resolution = '1920x1080'
    cpu = 'Haswell-noTSX,stepping=3,vendor=GenuineIntel,+ssse3,+sse4.2,+popcnt,+avx,+avx2,+aes,+xsave,+xsaveopt,-arat,enforce'
    mac = $mac; qmp_port = $QmpPort; ssh_port = $SshPort
    active_disk = (Resolve-Path -LiteralPath $Disk).Path
    opencore = "$dir/firmware/OpenCore.img"
    firmware_code = "$dir/firmware/OVMF_CODE.fd"
    firmware_vars = "$dir/firmware/OVMF_VARS.fd"
    rom = "$root/sources/reims/firmware/prebuilt/ReimsGraphics.rom"
} | ConvertTo-Json | Set-Content "$dir/state/config.json" -Encoding utf8NoBOM
Write-Output "Configured $Vm. The selected disk is used directly for writes."
