# SPDX-License-Identifier: GPL-2.0-or-later
# Run the exact policy module without upstream's Unix-only test harness or any GPU calls.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. "$root/scripts/environment.ps1"
$test = Join-Path $root 'build/display-policy-tests'
New-Item -ItemType Directory -Force $test | Out-Null
$module = (Join-Path $root 'sources/reims/crates/reims-vgpu/src/graphics/display_mode.rs').Replace('\','/')
"#[path = `"$module`"] mod display_mode;" | Set-Content "$test/main.rs" -Encoding utf8NoBOM
rustc --edition 2024 --test "$test/main.rs" -o "$test/display-policy-tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Display policy test build failed' }
& "$test/display-policy-tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Display policy tests failed' }
