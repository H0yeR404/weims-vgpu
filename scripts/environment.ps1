# SPDX-License-Identifier: GPL-2.0-or-later
$Root = Split-Path $PSScriptRoot
if (!$env:WEIMS_MSYS2) { throw 'Set WEIMS_MSYS2 to your MSYS2 installation path' }
$Msys = $env:WEIMS_MSYS2
if (!$env:RUSTUP_HOME) { $env:RUSTUP_HOME = Join-Path $env:USERPROFILE '.rustup' }
if (!$env:CARGO_HOME) { $env:CARGO_HOME = Join-Path $env:USERPROFILE '.cargo' }
$env:RUSTUP_TOOLCHAIN = 'nightly-2026-11-03-x86_64-pc-windows-gnu'
$env:CARGO_TARGET_DIR = "$Root\build\port-target"
$env:CARGO_BUILD_JOBS = '16'
$env:CMAKE_BUILD_PARALLEL_LEVEL = '16'
$env:CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = "$Msys\ucrt64\bin\clang.exe"
$env:CC = "$Msys\ucrt64\bin\clang.exe"
$env:CXX = "$Msys\ucrt64\bin\clang++.exe"
$env:CMAKE_GENERATOR = 'Ninja'
$env:RC = "$Msys\ucrt64\bin\llvm-windres.exe"
$env:CMAKE_TOOLCHAIN_FILE = "$Root\scripts\ucrt64.cmake"
$env:PYTHONUTF8 = '1'
$env:GIT_CONFIG_COUNT = '1'; $env:GIT_CONFIG_KEY_0 = 'core.autocrlf'; $env:GIT_CONFIG_VALUE_0 = 'false'
$env:PATH = (@(
    "$Msys\ucrt64\bin",
    "$env:CARGO_HOME\bin",
    "$env:RUSTUP_HOME\toolchains\$env:RUSTUP_TOOLCHAIN\bin",
    "$env:ProgramFiles\Git\cmd",
    "$Msys\usr\bin",
    $env:PATH
) -join ';')
