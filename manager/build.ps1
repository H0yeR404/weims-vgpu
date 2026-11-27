# SPDX-License-Identifier: GPL-2.0-or-later
param([string]$DotNet='dotnet', [switch]$SelfContained, [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$out=if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'artifacts/Weims-Manager' }
$mode=if ($SelfContained) { 'true' } else { 'false' }
& $DotNet publish "$PSScriptRoot/Weims.Manager/Weims.Manager.csproj" -c Release -r win-x64 --self-contained $mode -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed' }
& $DotNet publish "$PSScriptRoot/Weims.Cli/Weims.Cli.csproj" -c Release -r win-x64 --self-contained $mode -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }
# The Guest Tools broker lives next to Manager and the CLI; both start it from their own folder.
& $DotNet publish "$PSScriptRoot/Weims.Broker/Weims.Broker.csproj" -c Release -r win-x64 --self-contained $mode -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Broker publish failed' }
$root=Split-Path $PSScriptRoot
# The Guest Tools install disk and the helper archive for self-update are built on macOS
# (guest-tools/make-image.sh) and kept in the repository with their hashes.
$prebuilt=Join-Path $root 'guest-tools/prebuilt'
if (Test-Path -LiteralPath "$prebuilt/SHA256SUMS") {
    New-Item -ItemType Directory -Force "$out/guest-tools" | Out-Null
    Get-ChildItem -LiteralPath "$out/guest-tools" -File | Remove-Item -Force
    foreach ($line in Get-Content -LiteralPath "$prebuilt/SHA256SUMS") {
        $recorded,$name=$line -split '\s+',2
        if (!$name) { continue }
        if ($name -notmatch '^[A-Za-z0-9._-]+$') { throw "Unexpected name in guest-tools/prebuilt/SHA256SUMS: $name" }
        if ((Get-FileHash -LiteralPath "$prebuilt/$name" -Algorithm SHA256).Hash -ne $recorded) { throw "guest-tools/prebuilt/$name does not match its recorded hash" }
        Copy-Item -LiteralPath "$prebuilt/$name" -Destination "$out/guest-tools/$name"
    }
} else { Write-Warning 'Nothing in guest-tools/prebuilt: Manager will not offer to install Guest Tools.' }
# UsbDk is the official 1.0.22 x64 installer. The same hash is UsbHost.InstallerSha256, checked again before it runs.
$usbdk = Join-Path $PSScriptRoot 'third_party/UsbDk/UsbDk_1.0.22_x64.msi'
if ((Get-FileHash -LiteralPath $usbdk -Algorithm SHA256).Hash -ne '91F6F695E1E13C656024E6D3B55620BF08D8835EF05EE0496935BA6BB62466A5') { throw 'UsbDk installer does not match its recorded hash' }
New-Item -ItemType Directory -Force "$out/licenses/UsbDk" | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/third_party/UsbDk/LICENSE.txt" -Destination "$out/licenses/UsbDk/LICENSE.txt"
Copy-Item -LiteralPath "$PSScriptRoot/third_party/UsbDk/README.md" -Destination "$out/licenses/UsbDk/README.md"
# SMBLibrary (LGPL-3.0-or-later) is two assemblies beside the broker. Its license, the GPL that license refers to,
# and the note on where its source is and how to replace it go with them.
$smb=Join-Path $out 'licenses/SMBLibrary'
New-Item -ItemType Directory -Force $smb | Out-Null
foreach ($assembly in 'SMBLibrary.dll','SMBLibrary.Win32.dll') { if (!(Test-Path -LiteralPath "$out/$assembly")) { throw "Missing from the publish output: $assembly" } }
Copy-Item -LiteralPath "$PSScriptRoot/third_party/SMBLibrary/License.txt" -Destination "$smb/LGPL-3.0.txt"
Copy-Item -LiteralPath "$root/licenses/GPL-3.0-or-later.txt" -Destination "$smb/GPL-3.0.txt"
Copy-Item -LiteralPath "$PSScriptRoot/third_party/SMBLibrary/README.md" -Destination "$smb/README.md"
Copy-Item -LiteralPath "$root/LICENSE" -Destination "$out/LICENSE"
Copy-Item -LiteralPath "$root/NOTICE.md" -Destination "$out/NOTICE.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.zh-CN.md" -Destination "$out/使用说明.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination "$out/README.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.ja.md" -Destination "$out/README.ja.md"
Get-ChildItem "$root/docs/release-1.2.0*.md" | Copy-Item -Destination $out
if ($SelfContained) {
    $cacheLine = (& $DotNet nuget locals global-packages --list --force-english-output | Select-Object -First 1)
    $cache = $cacheLine -replace '^global-packages:\s*', ''
    $deps = Get-Content -Raw "$out/Weims.Manager.deps.json" | ConvertFrom-Json
    foreach ($entry in $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.*' }) {
        $parts = ($entry -replace '^runtimepack\.', '').Split('/')
        $package = Join-Path $cache ($parts[0].ToLowerInvariant() + '/' + $parts[1])
        $noticeDir = Join-Path $out ('licenses/' + $parts[0])
        New-Item -ItemType Directory -Force $noticeDir | Out-Null
        $notices = Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' }
        if (!$notices) { throw "No runtime license found: $package" }
        $notices | Copy-Item -Destination $noticeDir
    }
}
Write-Output "Run $out/Weims.Manager.exe --repo <weims-vgpu checkout>"
