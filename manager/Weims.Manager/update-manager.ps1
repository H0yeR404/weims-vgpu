# SPDX-License-Identifier: GPL-2.0-or-later
# Runs outside the app folder so the installer can replace all manager files.
param([Parameter(Mandatory=$true)][string]$JobFile)
$ErrorActionPreference = 'Stop'
$log = $JobFile + '.log'
$updateLock = $null
try {
    $lockDirectory = Join-Path $env:LOCALAPPDATA 'Weims'
    [IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
    $updateLock = [IO.File]::Open((Join-Path $lockDirectory 'manager-update.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $job = Get-Content -LiteralPath $JobFile -Raw | ConvertFrom-Json
    if (!(Test-Path -LiteralPath $job.installer -PathType Leaf)) { throw 'Installer is missing.' }
    if ($job.destination.Contains('"') -or $job.data.Contains('"')) { throw 'Invalid update path.' }
    if ((Get-FileHash -LiteralPath $job.installer -Algorithm SHA256).Hash -ne $job.sha256) { throw 'Installer SHA256 mismatch.' }
    $manager = Get-Process -Id $job.pid -ErrorAction Stop
    if ($manager.StartTime.ToUniversalTime().Ticks.ToString() -ne $job.started) { throw 'Manager process identity changed.' }
    Set-Content -LiteralPath ($JobFile + '.ready') -Value 'ready'
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while (!(Test-Path -LiteralPath ($JobFile + '.commit'))) {
        if (([DateTime]::UtcNow -gt $deadline -or $manager.HasExited) -and !(Test-Path -LiteralPath ($JobFile + '.commit'))) { Set-Content -LiteralPath $log -Value 'Update not committed; no installation performed.'; exit 0 }
        Start-Sleep -Milliseconds 100
    }
    if (!$manager.WaitForExit(120000)) { throw 'Manager did not exit; update cancelled.' }
    if (Get-Process -Name 'qemu-system-x86_64' -ErrorAction SilentlyContinue) { throw 'A QEMU VM is running; update cancelled.' }
    if (Get-Process -Name 'Weims.Manager' -ErrorAction SilentlyContinue) { throw 'Another Manager is open; update cancelled.' }
    $arguments = '/SILENT /SUPPRESSMSGBOXES /NORESTART /DIR="' + $job.destination.TrimEnd('\') + '" /LOG="' + $JobFile + '.installer.log"'
    $install = Start-Process -FilePath $job.installer -ArgumentList $arguments -PassThru -Wait
    if ($install.ExitCode -ne 0) { throw ('Installer failed: ' + $install.ExitCode) }
    $exe = Join-Path $job.destination 'Weims.Manager.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Installed manager was not found.' }
    [IO.File]::WriteAllText((Join-Path $job.destination 'profile-directory.txt'), $job.data, (New-Object Text.UTF8Encoding($false)))
    Start-Process -FilePath $exe -ArgumentList ('--data-dir "' + $job.data.TrimEnd('\') + '"')
    Set-Content -LiteralPath $log -Value 'Update installed and manager restarted.'
} catch {
    $_ | Out-String | Set-Content -LiteralPath $log
    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(('Update failed. Your VM files were not changed. Details: ' + $log), 'Weims update') | Out-Null
    exit 1
}

finally { if ($updateLock) { $updateLock.Dispose() } }
