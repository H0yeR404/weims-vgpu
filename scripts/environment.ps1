# SPDX-License-Identifier: GPL-2.0-or-later
$Root = Split-Path $PSScriptRoot
# An MSYS2 installation whose CLANG64 environment upstream's installer may add packages to.
if (!$env:WEIMS_MSYS2) { throw 'Set WEIMS_MSYS2 to your MSYS2 installation path' }
$Msys = $env:WEIMS_MSYS2
$Source = "$Root\sources\reims-vgpu"
# Everything upstream's installer writes outside the source tree goes here instead of the user's profile.
$Upstream = "$Root\build\upstream"
$Runtime = "$Root\build\weims-runtime"
