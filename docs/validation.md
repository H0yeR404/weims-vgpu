# Validation scope

The publication was prepared on 2026-11-12. Each pinned upstream revision was fetched directly from GitHub in a fresh directory and its checksum-verified patch applied successfully. All 44 changed Reims files, 33 changed QEMU files and 12 changed metal2vulkan files matched the recorded working source endpoints byte for byte (the Reims submodule pointer is excluded by design).

PowerShell scripts passed parser checks. VM initialization and redacted startup dry-run passed without starting or changing the running VM. Cargo metadata with `--locked` passed. The OpenCore helper was tested on a disposable fixture. A separate read-only review found no outstanding critical packaging issue.

The portable publication build script successfully rebuilt the release DLL and QEMU from the freshly fetched and patched source directories. QEMU version and graphics-device help checks passed, including the guest-memory property. This was on the existing Windows toolchain with cached Cargo dependencies, not a clean Windows installation.

The original source implementation passed 22 C API tests, produced a release DLL and QEMU executable, and booted the owner's macOS VM. Its performance measurement is described in reproduce.md. These results are distinct from the publication-script checks. Full cross-platform `cargo xtask check`, a new macOS installation, and a second complete boot using only the published setup procedure have not been performed. Third-party tools and guest materials remain prerequisites.

QEMU patch application reports inherited whitespace warnings due to an earlier local WHPX diagnostic file's line-ending normalization. The patch is intentionally kept exact to the tested source. MSYS2 packages are recorded, but not archived or installed at historical versions; byte-for-byte binary reproducibility is not claimed.
