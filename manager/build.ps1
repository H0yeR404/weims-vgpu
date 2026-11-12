# SPDX-License-Identifier: GPL-2.0-or-later
param([string]$DotNet='dotnet', [switch]$SelfContained, [string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$out=if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $PSScriptRoot 'artifacts/Weims-Manager' }
$mode=if ($SelfContained) { 'true' } else { 'false' }
& $DotNet publish "$PSScriptRoot/Weims.Manager/Weims.Manager.csproj" -c Release -r win-x64 --self-contained $mode -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw 'Manager publish failed' }
$root=Split-Path $PSScriptRoot
Copy-Item -LiteralPath "$root/LICENSE" -Destination "$out/LICENSE"
Copy-Item -LiteralPath "$root/NOTICE.md" -Destination "$out/NOTICE.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.zh-CN.md" -Destination "$out/使用说明.md"
Copy-Item -LiteralPath "$PSScriptRoot/README.md" -Destination "$out/README.md"
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
