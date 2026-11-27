# Attribution and provenance

This is an independent Windows integration of [steelbrain/reims-vgpu](https://github.com/steelbrain/reims-vgpu), [steelbrain/metal2vulkan](https://github.com/steelbrain/metal2vulkan) and [steelbrain/qemu-reims-vgpu](https://github.com/steelbrain/qemu-reims-vgpu). It is not an official upstream release.

## The source tree after 1.2.0-pre.1

The runtime is built from upstream reims-vgpu at the commit `sources.json` names, which supports Windows hosts itself. QEMU (its `vendor/qemu` submodule) and metal2vulkan (a Cargo dependency) are used unmodified at the revisions that commit names. This project applies one patch, `manager/patches/window.patch`, to reims-vgpu's window crate: the free pointer, and the window's size after a resize. [steelbrain/reims-util](https://github.com/steelbrain/reims-util) is built unmodified at the commit upstream names (`7867384c0ab703b4c6aae36cbbb1a35fcffc8321`). All original authors retain their copyrights, and upstream source notices remain intact when the patch is applied. See sources.json for base revisions and the patch checksum.

## Releases up to 1.2.0-pre.1

Those releases carried a Windows port of an earlier Reims as patches to Reims, QEMU and metal2vulkan. Their Windows platform implementations and toolchain conventions also derive from [tqmane/reims-vgpu](https://github.com/tqmane/reims-vgpu) and [tqmane/qemu-reims-vgpu](https://github.com/tqmane/qemu-reims-vgpu). The patches included the existing local Windows port, clock correction, WHPX diagnostic controls, and subsequent shared-section guest-memory integration. Local source endpoints were Reims 9d57adfb5, QEMU bd00ca633c and metal2vulkan 3bfe1972. The Reims submodule pointer was deliberately excluded: QEMU was fetched independently and patched separately. The patches and their base revisions are in the tags of those releases.

Prerelease 1.1.0-pre.1 added adaptive display and the Windows build of reims-util at `84e5c98ae7fa6b0b68e7be690525dda29697ab34`.

## Licenses

Reims and metal2vulkan permit GPL-2.0-or-later OR LGPL-3.0-or-later; QEMU retains its per-file licensing. The licenses directory contains original notices and full texts. Cargo/MSYS2 dependencies and OpenCore/EDK II retain their own licenses. A release distributes a Windows runtime and a self-contained .NET manager. The runtime source archive supplies the Reims tree as patched, with QEMU, reims-util, the Cargo dependencies (metal2vulkan among them), exact MSYS2 source packages with build recipes, and matching SeaBIOS/iPXE sources. Runtime packages retain dependency license notices. Manager source and build/packaging instructions are in this repository at the release tag. PowerShell is downloaded separately from its official Microsoft release and retains its notices.

reims-util is distributed under GPL-2.0-or-later (its dual-license alternative remains available). Its macserial-derived code retains OpenCorePkg's BSD-3-Clause notices. The matching Reims guest prebuilts, Graft and graphics ROM retain their upstream licenses; Graft's notices are included separately. OpenCore, OVMF and Apple recovery media are downloaded by the user-side helper, not copied from the developer's VM or distributed in the release. Downloaded OpenCore/OVMF remain subject to their original licenses; no developer machine identity or VM disk is included.

Manager's Guest Tools broker (`Weims.Broker.exe`) serves shared folders with [SMBLibrary](https://github.com/TalAloni/SMBLibrary) 1.5.8.1 by Tal Aloni, licensed under LGPL-3.0-or-later. Its source is in `manager/third_party/SMBLibrary` with one file changed; the README there names the upstream commit, the change and how to replace the library, and a release ships it as two separate assemblies with that README and both license texts under `licenses/SMBLibrary`. The broker combined with the library is distributed under the terms of the GPL, version 3 or later, as this project's GPL-2.0-or-later code allows.

USB passthrough installs [UsbDk](https://github.com/daynix/UsbDk) 1.0.22, the official 64-bit installer from release v1.00-22, licensed under Apache-2.0. The unchanged installer and its license are in `manager/third_party/UsbDk`. Manager checks the installer's SHA-256 before running it.

Modifications were prepared with AI assistance. This repository is a downstream distribution, not a submission to QEMU upstream. The screenshot was supplied and approved for publication by the repository owner.
