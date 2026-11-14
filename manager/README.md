# Weims Manager Preview 1.1.0-pre.1

## Quick Start — Windows · 1.1.0-pre.1

> **PRE-RELEASE: the VM may freeze, including during resolution changes. This is a known issue and will be addressed in a future update. Back up important data.** The manager is still under development. Automatic preparation has reached the Tahoe recovery menu on the test host; a complete fresh installation to the desktop has not been validated.

Requirements: Windows 10 2004+/11 x64, BIOS/UEFI virtualization, **Windows Hypervisor Platform (WHPX)** enabled (restart Windows after enabling it), and a Vulkan GPU/driver supporting `VK_EXT_external_memory_host`. Enable WHPX in **Turn Windows features on or off**. The app does not enable firmware virtualization or Windows features for you. WSL2, MSYS2, Rust and the .NET SDK are **not needed for the packaged app**. Internet access to GitHub, Apple and upstream download servers is required.

### Install and create a VM

1. Download `Weims-Manager-1.1.0-pre.1-Setup-x64.exe` from the [prerelease](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1), or extract the portable ZIP. The installer and app use the Weims icon. Preview installs beside stable 1.0.1; English is the default UI, with Chinese available in Settings.
2. Open **Weims Manager Preview → Create VM**. Choose a new VM location, name and disk capacity (default **200 GiB**). Keep automatic Apple Tahoe recovery download enabled, or select your own compatible `BaseSystem.dmg` / `.img`. Full ISO and ARM macOS are not supported.
3. Click **Create**. The app prepares PowerShell and QEMU/Reims, downloads and verifies Apple recovery, generates OpenCore with a new machine identity, prepares OVMF and matching graphics drivers/ROM, converts recovery media, and creates the empty disk. No manual WSL, EFI editing, firmware copying or SMC-key entry is needed. Existing destinations are rejected. Sparse capacity does not reserve physical disk space.
4. Start the VM. In OpenCore select recovery (press **Space** if hidden). In **Disk Utility → View → Show All Devices**, erase **only the new empty system disk**, identified by its capacity, as **GUID Partition Map / APFS**. Never erase the recovery or OpenCore disks.
5. Close Disk Utility, choose **Reinstall macOS Tahoe**, and select the new volume. Allow downloads and restarts; choose the installer/system entry as installation progresses. After reaching the desktop, shut down macOS, stop the remaining QEMU process, detach recovery in the Manager, save and boot again.

New guided VMs enable **automatic guest resolution** and free pointer. Drag the separate Reims window after login. For imported VMs, shut down first, select **Display & Input → Auto fit guest resolution**, disable borderless mode, and save. Startup bounds: **800–4096 × 600–2160**; **2560×1440 is validated on this host only**. Fixed mode scales the complete image; Compatibility preserves the old behavior. If resizing stops responding, preserve logs and restart normally when possible. Clipboard and file sharing are not included.

### Command line (included with installer and portable ZIP)

Run in Windows PowerShell from the application's installation/extraction directory. `create` prepares the same components automatically; omit `--recovery` to download from Apple.

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json --display-mode auto
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
# After macOS has shut down, release the remaining QEMU process:
.\Weims.Cli.exe stop --config D:\VMs\macOS-Tahoe\state\config.json
```

For an existing 200 GiB VM, use its actual `state/config.json` with `start`; do not recreate its disk. Run `setup` first if this Preview profile has no runtime. GUI and CLI share `%LOCALAPPDATA%\Weims\Manager-Preview`; do not launch the same disk twice. `stop` refuses a guest that QMP does not report as shut down. If the shutdown request is ignored, use the macOS Apple menu.

[Prerelease details](https://github.com/H0yeR404/weims-vgpu/blob/v1.1.0-pre.1/docs/prerelease-1.1.0-pre.1.md) · [Stable 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1). Stable 1.0.1 does not include automatic boot preparation or adaptive resolution.

## Local update preview (1.1.0-pre.2, not published)

Open **Software updates → Check for updates** to read the new version and GitHub release notes. **Update now** downloads the official installer, verifies its SHA256 digest, and installs/restarts Manager after all QEMU VMs and other Manager windows are closed. Stable builds accept stable releases; preview builds also accept newer previews. Older versions are never offered. Downloading can be cancelled. Errors do not change VM disks or runtime paths.

Updating a portable copy installs Manager in the per-user application folder. The selected settings directory is retained for restart and later shortcut launches. The update does not replace the QEMU runtime. Installation failure details are saved under the settings directory's `updates` folder.

English remains the default. The sidebar language picker now changes the current window immediately while preserving the selected page and unsaved configuration. The update page also describes this local build when no newer release is available. VM freezes remain a known issue under investigation.
