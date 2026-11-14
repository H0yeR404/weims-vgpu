# Weims Manager 1.1.0-pre.2

- Check for updates from GitHub, read release notes, and download/install with SHA256 verification.
- Stable builds accept stable releases; previews also accept newer previews. Downgrades are blocked.
- Installation waits until all QEMU VMs and other Manager windows are closed. Downloads can be cancelled.
- Switch English/Chinese immediately without restarting, preserving the current page and unsaved configuration. English remains the default.
- Show the actual application version and preserve the settings directory across updates.

**PRE-RELEASE: VM freezes remain possible, including during resolution changes. This known issue is under investigation and will be addressed.** The manager is still under development. Full fresh macOS installation and long-term stability remain unvalidated.

## Install / upgrade

Download `Weims-Manager-1.1.0-pre.2-Setup-x64.exe`, close VMs and Manager, then run it; or extract `Weims-Manager-1.1.0-pre.2-win-x64.zip` and run `Weims.Manager.exe`. Versions 1.0.1 and 1.1.0-pre.1 do not have the new updater: install this release manually once. Future compatible releases can be installed from Software updates.

Updating a portable copy installs Manager into the per-user application folder. The updater preserves the selected settings directory. QEMU/Reims runtime and VM disks are not replaced by a Manager update.

The runtime is unchanged from [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1). Automatic preparation still uses that pinned runtime; its runtime ZIP and corresponding source archive remain available there. Both GUI and CLI are included.

## Validation

63 Core checks, 25 WPF checks, 21 updater checks (including live GitHub discovery), and 6 installer-handoff checks passed. Handoff tests mock installation/process launches; a real in-place installation upgrade has not been performed. SHA256SUMS is attached. Stable 1.0.1 remains Latest.
