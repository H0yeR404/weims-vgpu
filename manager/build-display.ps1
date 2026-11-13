# SPDX-License-Identifier: GPL-2.0-or-later
# Build the display extension separately; keep the published input runtime usable.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'sources/reims'
foreach ($name in 'free-pointer.patch', 'adaptive-display.patch') {
    $patch = Join-Path $PSScriptRoot "patches/$name"
    git -C $source apply --reverse --check $patch 2>$null
    if ($LASTEXITCODE -ne 0) {
        git -C $source apply --check $patch
        if ($LASTEXITCODE -ne 0) { throw "Patch does not match source: $name. No patch applied for this step." }
        git -C $source apply $patch
        if ($LASTEXITCODE -ne 0) { throw "Patch failed: $name" }
    }
}
Push-Location $root
try { & "$root/scripts/build.ps1" -Only lib } finally { Pop-Location }
$runtime = Join-Path $root 'build/manager-display-runtime'
New-Item -ItemType Directory -Force $runtime | Out-Null
foreach ($name in 'qemu-system-x86_64.exe', 'qemu-img.exe') {
    Copy-Item -LiteralPath "$root/build/port-qemu/$name" -Destination $runtime
}
Copy-Item -LiteralPath "$root/build/port-dist/bin/reims_vgpu_c.dll" -Destination $runtime
$hash = (Get-FileHash "$runtime/reims_vgpu_c.dll" -Algorithm SHA256).Hash
@{ capability='free-pointer-v1'; dll_sha256=$hash } | ConvertTo-Json | Set-Content "$runtime/weims-input-v1.json" -Encoding utf8NoBOM
@{ capability='adaptive-display-v1'; dll_sha256=$hash } | ConvertTo-Json | Set-Content "$runtime/weims-display-v1.json" -Encoding utf8NoBOM
Write-Output "Display-enabled runtime: $runtime"
