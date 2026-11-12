# SPDX-License-Identifier: GPL-2.0-or-later
# Apply the optional input patch after scripts/fetch.ps1 and build a separate runtime.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$source = Join-Path $root 'sources/reims'
$patch = Join-Path $PSScriptRoot 'patches/free-pointer.patch'
git -C $source apply --reverse --check $patch 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $source apply --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'Input patch does not match source; no changes applied.' }
    git -C $source apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Input patch failed.' }
}
Push-Location $root
try { & "$root/scripts/build.ps1" -Only lib } finally { Pop-Location }
$runtime = Join-Path $root 'build/manager-runtime'
New-Item -ItemType Directory -Force $runtime | Out-Null
foreach ($name in 'qemu-system-x86_64.exe','qemu-img.exe') { Copy-Item -LiteralPath "$root/build/port-qemu/$name" -Destination $runtime }
Copy-Item -LiteralPath "$root/build/port-dist/bin/reims_vgpu_c.dll" -Destination $runtime
@{ capability='free-pointer-v1'; dll_sha256=(Get-FileHash "$runtime/reims_vgpu_c.dll" -Algorithm SHA256).Hash } | ConvertTo-Json | Set-Content "$runtime/weims-input-v1.json" -Encoding utf8NoBOM
Write-Output "Input-enabled runtime: $runtime"
