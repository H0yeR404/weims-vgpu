# SPDX-License-Identifier: GPL-2.0-or-later
# This short-lived helper leaves independent QEMU stdout/stderr file handles alive.
param([Parameter(Mandatory)][string]$Plan)
$ErrorActionPreference = 'Stop'
$p = Get-Content -Raw -LiteralPath $Plan | ConvertFrom-Json
if (!$env:WEIMS_OSK) { throw 'Missing process-local SMC key' }
$arguments = @($p.arguments | ForEach-Object { if ($_ -eq 'isa-applesmc,osk=<redacted>') { 'isa-applesmc,osk=' + $env:WEIMS_OSK } else { $_ } })
Remove-Item Env:WEIMS_OSK
foreach ($name in 'REIMS_VGPU_WINDOW','REIMS_WHPX_PF_INTERCEPT','REIMS_WHPX_MMIO_BQL','REIMS_WHPX_STATS') { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
$quoted = ($arguments | ForEach-Object { '"' + ([regex]::Replace(([regex]::Replace($_, '(\\*)"', '$1$1\"')), '(\\+)$', '$1$1')) + '"' }) -join ' '
$proc = Start-Process -FilePath $p.executable -ArgumentList $quoted -WorkingDirectory (Split-Path $p.executable) -WindowStyle Hidden -RedirectStandardOutput "$($p.run_directory)/stdout.log" -RedirectStandardError "$($p.run_directory)/stderr.log" -PassThru
try {
    $temp = $p.runtime + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    @{ pid=$proc.Id; executable=$p.executable; qmp_port=$p.qmp_port; qmp_name=$p.qmp_name; started_utc=$proc.StartTime.ToUniversalTime().ToString('o'); run_directory=$p.run_directory } | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding utf8NoBOM
    [IO.File]::Move($temp, $p.runtime, $true)
} catch {
    if (!$proc.HasExited) { $proc.Kill(); $proc.WaitForExit() }
    throw
}
Start-Sleep -Seconds 2
if ($proc.HasExited) { throw "QEMU exited with code $($proc.ExitCode). See stderr.log." }
Write-Output "Started QEMU PID $($proc.Id)"
