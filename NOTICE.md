# Attribution and provenance

This is an independent Windows integration of [steelbrain/reims-vgpu](https://github.com/steelbrain/reims-vgpu), [steelbrain/metal2vulkan](https://github.com/steelbrain/metal2vulkan) and [steelbrain/qemu-reims-vgpu](https://github.com/steelbrain/qemu-reims-vgpu). It is not an official upstream release.

Windows platform implementations and toolchain conventions also derive from [tqmane/reims-vgpu](https://github.com/tqmane/reims-vgpu) and [tqmane/qemu-reims-vgpu](https://github.com/tqmane/qemu-reims-vgpu). All original authors retain their copyrights. Upstream source notices remain intact when patches are applied. See sources.json for base revisions and patch checksums.

The patches include the existing local Windows port, clock correction, WHPX diagnostic controls, and subsequent shared-section guest-memory integration. Local source endpoints were Reims 9d57adfb5, QEMU bd00ca633c and metal2vulkan 3bfe1972. The Reims submodule pointer is deliberately excluded: QEMU is fetched independently and patched separately.

Reims and metal2vulkan permit GPL-2.0-or-later OR LGPL-3.0-or-later; QEMU retains its per-file licensing. The licenses directory contains original notices and full texts. Cargo/MSYS2 dependencies and OpenCore/EDK II retain their own licenses. No prebuilt host binaries are distributed here. Future binary distributions must meet the applicable corresponding-source, build-instruction and notice requirements.

Modifications were prepared with AI assistance. This repository is a downstream distribution, not a submission to QEMU upstream. The screenshot was supplied and approved for publication by the repository owner.
