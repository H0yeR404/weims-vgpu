# Validation scope

## The upstream-based runtime, 2026-11-24

The source tree builds its runtime from upstream reims-vgpu `c8419476` with `manager/patches/window.patch` (see [Building from source](reproduce.md)). What was checked on the host in the main README:

- **Sources.** `scripts/fetch.ps1` was run into an empty folder against local copies of the two repositories: the patch's checksum matched, it applied to the pinned commit, and reims-util was checked out at the commit upstream names. The fetch from GitHub itself was not repeated in that run.
- **Build.** `scripts/build.ps1` built the library, QEMU 11.1.90 and reims-util in MSYS2 CLANG64 and assembled `build/weims-runtime`. Its programs ran with only Windows' system directory on `PATH`. `manager/package-runtime.py --runtime-only` produced a 94.8 MB runtime ZIP.
- **Automated tests.** 115 core, 66 WPF, 22 update, 198 broker and 27 integration checks, the last two groups against the built QEMU without a guest; and the 117 tests of upstream's window crate with the patch applied, three of them the patch's own.
- **A real guest.** macOS 26.7.2 on a copy-on-write overlay of an existing VM, RTX 4090 Laptop GPU. The VM's boot image was made again at its first start with the same Mac identity, and macOS reached the desktop. Graphics, the free pointer, window resizing, Guest Tools with the clipboard both ways, the install disk hot-plugged into the running guest, sound output, and shutdown from the CLI were exercised; QEMU and the broker had exited 14 and 11 seconds after the two shutdown requests. After a resize the window's client area and the guest's mode were both 1712×1148.

Not checked: Intel and AMD GPUs, creating a VM and installing macOS from recovery on this runtime, relative mouse motion for games, more than one display, sessions longer than a few minutes, the source archive and catalog that `package-runtime.py` writes without `--runtime-only`, the installer, and an update from a released version.

## The Windows port of releases up to 1.2.0-pre.1, 2026-11-12

The publication was prepared on 2026-11-12. Each pinned upstream revision was fetched directly from GitHub in a fresh directory and its checksum-verified patch applied successfully. All 44 changed Reims files, 33 changed QEMU files and 12 changed metal2vulkan files matched the recorded working source endpoints byte for byte (the Reims submodule pointer is excluded by design).

PowerShell scripts passed parser checks. VM initialization and redacted startup dry-run passed without starting or changing the running VM. Cargo metadata with `--locked` passed. The OpenCore helper was tested on a disposable fixture. A separate read-only review found no outstanding critical packaging issue.

The portable publication build script successfully rebuilt the release DLL and QEMU from the freshly fetched and patched source directories. QEMU version and graphics-device help checks passed, including the guest-memory property. This was on the existing Windows toolchain with cached Cargo dependencies, not a clean Windows installation.

The original source implementation passed 22 C API tests, produced a release DLL and QEMU executable, and booted the owner's macOS VM. Its performance measurement is in the main README. These results are distinct from the publication-script checks. Full cross-platform `cargo xtask check`, a new macOS installation, and a second complete boot using only the published setup procedure have not been performed. Third-party tools and guest materials remain prerequisites.

QEMU patch application reports inherited whitespace warnings due to an earlier local WHPX diagnostic file's line-ending normalization. The patch is intentionally kept exact to the tested source. MSYS2 packages are recorded, but not archived or installed at historical versions; byte-for-byte binary reproducibility is not claimed.

The patches, scripts and sources of that port are in the tags of those releases.
