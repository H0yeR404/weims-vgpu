# First installation on Windows

## Quick Start — Windows · 1.1.1

> The manager is still under development. Automatic preparation has reached the Tahoe recovery menu on the test host; a complete fresh installation to the desktop has not been validated.

Requirements: Windows 10 2004+/11 x64, BIOS/UEFI virtualization, **Windows Hypervisor Platform (WHPX)** enabled (restart Windows after enabling it), and a Vulkan GPU/driver supporting `VK_EXT_external_memory_host`. Enable WHPX in **Turn Windows features on or off**. The app does not enable firmware virtualization or Windows features for you. WSL2, MSYS2, Rust and the .NET SDK are **not needed for the packaged app**. Internet access to GitHub, Apple and upstream download servers is required.

### Install and create a VM

1. Download `Weims-Manager-1.1.1-Setup-x64.exe` from the [release](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.1), or extract the portable ZIP. The installer and app use the Weims icon. The installer uses the stable Weims Manager application folder. English and light appearance are the defaults; language and appearance can be changed immediately in the sidebar.
2. Open **Weims Manager → Create VM**. Choose a new VM location, name and disk capacity (default **200 GiB**). Keep automatic Apple Tahoe recovery download enabled, or select your own compatible `BaseSystem.dmg` / `.img`. Full ISO and ARM macOS are not supported.
3. Click **Create**. The app prepares PowerShell and QEMU/Reims, downloads and verifies Apple recovery, generates OpenCore with a new machine identity, prepares OVMF and matching graphics drivers/ROM, converts recovery media, and creates the empty disk. No manual WSL, EFI editing, firmware copying or SMC-key entry is needed. Existing destinations are rejected. Sparse capacity does not reserve physical disk space.
4. Start the VM. In OpenCore select recovery (press **Space** if hidden). In **Disk Utility → View → Show All Devices**, erase **only the new empty system disk**, identified by its capacity, as **GUID Partition Map / APFS**. Never erase the recovery or OpenCore disks.
5. Close Disk Utility, choose **Reinstall macOS Tahoe**, and select the new volume. Allow downloads and restarts; choose the installer/system entry as installation progresses. After reaching the desktop, shut down macOS (the VM window closes by itself), detach recovery in the Manager, save and boot again.

New guided VMs enable **automatic guest resolution** and free pointer. Drag the separate Reims window after login. For imported VMs, shut down first, select **Display & Input → Auto fit guest resolution**, disable borderless mode, and save. Startup bounds: **800–4096 × 600–2160**; **2560×1440 is validated on this host only**. Fixed mode scales the complete image; Compatibility preserves the old behavior. If resizing stops responding, preserve logs and restart normally when possible. Clipboard and file sharing are not included.

### Command line (included with installer and portable ZIP)

Run in Windows PowerShell from the application's installation/extraction directory. `create` prepares the same components automatically; omit `--recovery` to download from Apple.

```powershell
.\Weims.Cli.exe create --destination D:\VMs\macOS-Tahoe --disk-gib 200
.\Weims.Cli.exe start --config D:\VMs\macOS-Tahoe\state\config.json --display-mode auto
.\Weims.Cli.exe status --config D:\VMs\macOS-Tahoe\state\config.json
.\Weims.Cli.exe shutdown --config D:\VMs\macOS-Tahoe\state\config.json
```

For an existing 200 GiB VM, use its actual `state/config.json` with `start`; do not recreate its disk. Run `setup` first if this profile has no runtime. GUI and CLI use the same profile: an updater-retained custom profile takes precedence; otherwise an existing `%LOCALAPPDATA%\Weims\Manager-Preview` is reused, or `%LOCALAPPDATA%\Weims\Manager` is used for fresh stable installs. Use `--data-dir` to choose another profile. Please do not launch the same disk twice. `shutdown` asks macOS to quit all apps and shut down; macOS asks about unsaved documents first, and QEMU exits when the guest has powered off. `stop` is only needed for a VM started by 1.1.0 or older whose QEMU stayed behind after shutdown.

[1.1.1 release notes](https://github.com/H0yeR404/weims-vgpu/blob/v1.1.1/docs/release-1.1.1.md). The graphics runtime remains pinned to v1.1.0-pre.1; this Manager release does not fix the known VM freezes.

## Appearance, language and updates

Choose **Appearance → Light / Dark** in the sidebar. The current window and open setup windows change immediately; the choice is saved without restarting or discarding unsaved configuration. Windows high contrast takes precedence. English remains the default language, and English/Chinese also switch immediately.

In **Software updates → Check for updates**, read the release notes and select **Update now**. The official installer is downloaded and SHA256-verified. Close all QEMU VMs and other Manager windows before installation; only the software update restarts Manager. Stable builds accept newer stable releases: 1.1.0 and pre.2 users can update to 1.1.1 through their existing updater. Users of 1.0.1 and pre.1 need to download the installer manually once.

The updater preserves the chosen settings directory for subsequent GUI and CLI launches. Updating a portable copy installs into the per-user application folder. The runtime and VM disks are not replaced. The retained Preview application is not automatically uninstalled; after upgrading, use the stable Weims Manager shortcut.

## One-click runtime setup

First-time users open the runtime page automatically. **One-click setup** downloads, verifies and saves runtime paths; a valid existing runtime is reused. Manual paths remain available under **Advanced**. Starting an imported VM prepares missing components automatically. Firmware virtualization, Windows features and GPU drivers may still require manual action.
