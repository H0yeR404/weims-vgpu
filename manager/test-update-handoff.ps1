# SPDX-License-Identifier: GPL-2.0-or-later
# Exercises the real helper with mocked process boundaries; never invokes an installer.
param([string]$OutputDirectory = (Join-Path $env:TEMP ('weims-handoff-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$global:weimsTestDestination = Join-Path $OutputDirectory 'mock installed'
$global:weimsTestLaunches = [Collections.Generic.List[object]]::new()
$installer = Join-Path $OutputDirectory 'mock-installer.exe'
Set-Content -LiteralPath $installer -Value 'not executable: mock fixture'
$jobFile = Join-Path $OutputDirectory 'job.json'
$now = [DateTime]::Now
@{pid=$PID; started=$now.ToUniversalTime().Ticks.ToString(); installer=$installer; sha256=(Get-FileHash $installer).Hash; destination=$global:weimsTestDestination; data=$OutputDirectory} | ConvertTo-Json | Set-Content $jobFile
Set-Content ($jobFile+'.commit') 'install'
$global:weimsTestManager=[pscustomobject]@{StartTime=$now;HasExited=$false}
$global:weimsTestManager | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {param($timeout) return $true}
function Get-Process { param($Id,$Name,$ErrorAction) if ($Id) {return $global:weimsTestManager} }
function Start-Process {
 param($FilePath,$ArgumentList,[switch]$PassThru,[switch]$Wait)
 $global:weimsTestLaunches.Add(@{file=$FilePath;arguments=$ArgumentList})
 if ($PassThru) {
  New-Item -ItemType Directory -Force $global:weimsTestDestination | Out-Null
  Set-Content -LiteralPath (Join-Path $global:weimsTestDestination 'Weims.Manager.exe') -Value 'mock manager'
  return [pscustomobject]@{ExitCode=0}
 }
}
& "$PSScriptRoot/Weims.Manager/update-manager.ps1" -JobFile $jobFile
if ($global:weimsTestLaunches.Count -ne 2) {throw 'Installer and relaunch expected'}
if ($global:weimsTestLaunches[0].arguments -notlike '*/SILENT*/DIR="*mock installed"*') {throw 'Silent install arguments with spaced paths lost'}
if ($global:weimsTestLaunches[1].arguments -ne ('--data-dir "'+$OutputDirectory+'"')) {throw 'Profile not preserved during restart'}
if ([IO.File]::ReadAllText((Join-Path $global:weimsTestDestination 'profile-directory.txt')) -ne $OutputDirectory) {throw 'Profile not persisted for later shortcut launches'}
if (!(Test-Path ($jobFile+'.ready'))) {throw 'Readiness signal absent'}
$global:weimsTestLaunches.Clear()
$global:weimsTestManager.HasExited = $true
$cancelJob = Join-Path $OutputDirectory ('cancel-' + [guid]::NewGuid().ToString('N') + '.json')
Copy-Item -LiteralPath $jobFile -Destination $cancelJob
& "$PSScriptRoot/Weims.Manager/update-manager.ps1" -JobFile $cancelJob
if ($global:weimsTestLaunches.Count -ne 0) {throw 'Uncommitted handoff must not install after manager exits'}
Write-Output '6 handoff checks passed. Installer and process-launch boundaries were mocked; no software installed.'
