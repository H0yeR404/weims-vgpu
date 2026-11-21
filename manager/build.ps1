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
# The Guest Tools install disk is built on macOS (guest-tools/make-image.sh) and kept in the repository with its hash.
$image=Join-Path $root 'guest-tools/prebuilt/weims-guest-tools.img'
if (Test-Path -LiteralPath $image) {
    $recorded=(Get-Content -LiteralPath (Join-Path $root 'guest-tools/prebuilt/SHA256SUMS') | Where-Object { $_ -match 'weims-guest-tools\.img$' } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne $recorded) { throw 'The Guest Tools install image does not match its recorded hash' }
    New-Item -ItemType Directory -Force "$out/guest-tools" | Out-Null
    Copy-Item -LiteralPath $image -Destination "$out/guest-tools/weims-guest-tools.img"
} else { Write-Warning 'No Guest Tools install image in guest-tools/prebuilt: Manager will not offer to install Guest Tools.' }
Copy-Item -LiteralPath "$root/LICENSE" -Destination "$out/LICENSE"
Copy-Item -LiteralPath "$root/NOTICE.md" -Destination "$out/NOTICE.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.zh-CN.md" -Destination "$out/使用说明.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination "$out/README.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.ja.md" -Destination "$out/README.ja.md"
Get-ChildItem "$root/docs/release-1.1.1*.md" | Copy-Item -Destination $out
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
