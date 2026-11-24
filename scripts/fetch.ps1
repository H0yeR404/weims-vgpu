# SPDX-License-Identifier: GPL-2.0-or-later
# Fetches the upstream sources sources.json pins and applies this project's patches to them.
# QEMU is reims-vgpu's vendor/qemu submodule and metal2vulkan a Cargo dependency of it: the build fetches
# both at the commits that reims-vgpu commit names. reims-util is fetched for the source archive; the build
# installs it itself, at the commit reims-vgpu names, which sources.json must repeat.
param([string]$Destination = (Join-Path $PSScriptRoot '../sources'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
New-Item -ItemType Directory -Force $Destination | Out-Null
foreach ($s in (Get-Content "$root/sources.json" -Raw | ConvertFrom-Json)) {
    $path = Join-Path $Destination $s.name
    if (Test-Path $path) { throw "Refusing existing source directory: $path" }
    $patch = if ($s.patch) { Join-Path $root $s.patch } else { $null }
    if ($patch -and (Get-FileHash $patch -Algorithm SHA256).Hash.ToLower() -ne $s.sha256) { throw "Patch checksum mismatch: $patch" }
    git init $path
    if ($LASTEXITCODE) { throw 'git init failed' }
    git -C $path config core.autocrlf false
    git -C $path remote add origin $s.url
    git -C $path fetch --depth 1 origin $s.base
    if ($LASTEXITCODE) { throw "Fetch failed: $($s.name)" }
    git -C $path checkout --detach FETCH_HEAD
    if ($LASTEXITCODE) { throw 'Checkout failed' }
    if (!$patch) { continue }
    git -C $path apply --check $patch
    if ($LASTEXITCODE) { throw 'Patch check failed' }
    git -C $path apply $patch
    if ($LASTEXITCODE) { throw 'Patch failed' }
}
