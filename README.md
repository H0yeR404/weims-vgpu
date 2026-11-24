# weims-vgpu

> Latest release: **1.1.1** — closes a way for software in the guest to reach QEMU's control channel, and fixes shutdown: the Manager's Shut down works and QEMU exits when macOS powers off. [Download](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.1)
>
> Preview: **1.2.0-pre.1** — Guest Tools shares the text clipboard between Windows and macOS, and a new runtime lets QEMU exit at once when a VM closes. [Download](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.2.0-pre.1)

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

![macOS Tahoe on Windows](docs/images/macos-tahoe.png)

## Quick Start — Windows · 1.1.1

> **Known issues: the VM may freeze, including during resolution changes. This is a known issue and will be addressed in a future update. Back up important data.** The manager is still under development. Automatic preparation has reached the Tahoe recovery menu on the test host; a complete fresh installation to the desktop has not been validated.

Requirements: Windows 10 2004+/11 x64, BIOS/UEFI virtualization, **Windows Hypervisor Platform (WHPX)** enabled (restart Windows after enabling it), and a Vulkan GPU/driver supporting `VK_EXT_external_memory_host`. Enable WHPX in **Turn Windows features on or off**. The app does not enable firmware virtualization or Windows features for you. WSL2, MSYS2, Rust and the .NET SDK are **not needed for the packaged app**. Internet access to GitHub, Apple and upstream download servers is required.

### Install and create a VM

1. Download `Weims-Manager-1.1.1-Setup-x64.exe` from the [release](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.1), or extract the portable ZIP. The installer and app use the Weims icon. The installer uses the stable Weims Manager application folder. English and light appearance are the defaults; language and appearance can be changed immediately in the sidebar.
2. Open **Weims Manager → Create VM**. Choose a new VM location, name and disk capacity (default **200 GiB**). Keep automatic Apple Tahoe recovery download enabled, or select your own compatible `BaseSystem.dmg` / `.img`. Full ISO and ARM macOS are not supported.
3. Click **Create**. The app prepares PowerShell and QEMU/Reims, downloads and verifies Apple recovery, generates OpenCore with a new machine identity, prepares OVMF and matching graphics drivers/ROM, converts recovery media, and creates the empty disk. No manual WSL, EFI editing, firmware copying or SMC-key entry is needed. Existing destinations are rejected. Sparse capacity does not reserve physical disk space.
4. Start the VM. In OpenCore select recovery (press **Space** if hidden). In **Disk Utility → View → Show All Devices**, erase **only the new empty system disk**, identified by its capacity, as **GUID Partition Map / APFS**. Never erase the recovery or OpenCore disks.
5. Close Disk Utility, choose **Reinstall macOS Tahoe**, and select the new volume. Allow downloads and restarts; choose the installer/system entry as installation progresses. After reaching the desktop, shut down macOS (the VM window closes by itself), detach recovery in the Manager, save and boot again.

New guided VMs enable **automatic guest resolution** and free pointer. Drag the separate Reims window after login. For imported VMs, shut down first, select **Display & Input → Auto fit guest resolution**, disable borderless mode, and save. Startup bounds: **800–4096 × 600–2160**; **2560×1440 is validated on this host only**. Fixed mode scales the complete image; Compatibility preserves the old behavior. If resizing stops responding, preserve logs and restart normally when possible. Clipboard sharing comes with Guest Tools in the 1.2.0-pre.1 preview; file sharing is not included.

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

First-time users open the runtime page automatically. **One-click setup** downloads, verifies and saves runtime paths; a valid existing runtime is reused. If the runtime that Manager installed is older than the one this version comes with, the Runtime page says so and **One-click setup** switches to the newer one; a runtime you chose yourself is left alone. Manual paths remain available under **Advanced**. Starting an imported VM prepares missing components automatically. Firmware virtualization, Windows features and GPU drivers may still require manual action.

## Guest Tools: clipboard sharing — preview 1.2.0-pre.1

Guest Tools shares the text clipboard between Windows and macOS while the VM's window is in front.

1. Start the VM and log in to macOS.
2. In Manager, turn on **Enable Guest Tools integration for this VM** and click **Install Guest Tools…**. An install disk appears in macOS.
3. In macOS, open the disk and double-click **Install Weims Guest Tools**. It installs for the current user, pairs with this VM and ejects the disk. Nothing is typed.

Copy on one side, paste on the other. Text up to 4 MiB is carried, and what a password manager marks as sensitive stays out of the clipboard history on the other side. A newer Manager updates Guest Tools in macOS by itself. To remove it, run **Uninstall Weims Guest Tools.command** from the install disk. The CLI has the same functions: `tools-enable`, `tools-install`, `tools-status`.

## Build from source

The source tree builds its runtime from upstream [Reims vGPU](https://github.com/steelbrain/reims-vgpu), which supports Windows hosts itself, with one patch for the window. The releases above still use the earlier runtime; the one built here is not in a release yet.

What it changes for a VM:

- macOS's resolution follows the window: resize the window and the image fills it. The fixed and compatibility display modes are gone.
- The guest has sound output.
- Captured input is released with Ctrl+Alt+Win.
- A VM made with an earlier version gets its OpenCore image made again at its first start, with the new runtime's drivers and the same Mac identity. The previous image stays beside it as `OpenCore.img.previous`.
- The GPU needs Vulkan 1.3.

Install [Git for Windows](https://git-scm.com/downloads/win), [PowerShell 7](https://github.com/PowerShell/PowerShell/releases), [MSYS2](https://www.msys2.org/) in a path without spaces, and the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then, in PowerShell 7:

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu
$env:WEIMS_MSYS2 = "C:\msys64"
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
pwsh -File manager/build.ps1 -SelfContained
./manager/artifacts/Weims-Manager/Weims.Manager.exe
```

`fetch.ps1` fetches the pinned upstream sources and applies the patch. `build.ps1` runs upstream's own installer in MSYS2's CLANG64 environment, which installs the MSYS2 packages and the Rust toolchain it needs, and assembles `build/weims-runtime`. Manager started from the checkout uses that folder. A first build took about ten minutes on the tested host. [Building from source](docs/reproduce.md) has the details, the tests and release packaging.

## Tested environment

> **Compatibility note:** This project has only been tested and confirmed working on the machine listed below. Other devices are theoretically supported, but compatibility and successful operation require actual testing.

| Component | Configuration |
| --- | --- |
| Laptop | Lenovo Legion Y9000P IRX8H (82WQ) |
| Host OS | Windows 11 Pro for Workstations, x86_64 |
| Windows build | 10.0.26300.9550 (reported as 26H2 by Fastfetch) |
| CPU | Intel Core i9-13900HX, 32 logical processors |
| GPU | NVIDIA GeForce RTX 4090 Laptop GPU, 15.67 GiB reported VRAM |
| Host RAM | 95.81 GiB reported usable memory |
| Host display | CU34G4 external monitor, 3440 × 1440 at 180 Hz |
| Host shell | PowerShell 7.6.9 |
| Virtualization / graphics | WHPX / Vulkan, shared guest-memory graphics path |
| Tested guest | macOS Tahoe, 8 vCPUs, 32 GiB RAM, 200 GiB virtual system disk |
| Guest resolution | 1920 × 1080 |

This is the tested machine, not a minimum or recommended specification. Host display refresh rate is separate from guest rendering FPS. The host's existing PowerShell version is also separate from the pinned PowerShell 7.7.0 package used by automatic setup when needed.

The runtime of the releases above passed 22 C API tests. One 10-second drag test measured 54.17 unique FPS, with a maximum 211 ms frame gap. These are limited measurements, not universal performance or stability guarantees. Shared guest memory still involves host device caches and is not full zero-copy.

The runtime built from source was run on this machine on 2026-11-24 with a macOS 26.7.2 guest: boot, graphics, free pointer, window resizing, Guest Tools clipboard, sound and shutdown worked. Its frame rate was not measured.

## Upstream and licensing

This project is built on upstream [Reims vGPU](https://github.com/steelbrain/reims-vgpu). Its goal is to let you try and use a macOS virtual machine on Windows with one click.

Upstream projects used:

- **[Reims vGPU](https://github.com/steelbrain/reims-vgpu)**: the virtual graphics device, its macOS guest drivers and the host window.
- **[metal2vulkan](https://github.com/steelbrain/metal2vulkan)**: translates Apple's Metal API and shaders to Vulkan.
- **[QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu)**: the QEMU build that carries the device.

Thanks to [steelbrain](https://github.com/steelbrain), the author of all three. This project is not an official upstream release.

Reims and metal2vulkan retain `GPL-2.0-or-later OR LGPL-3.0-or-later`; QEMU retains its original per-file licenses. Integration scripts and documentation use GPL-2.0-or-later. Read [LICENSE](LICENSE), [attribution and provenance](NOTICE.md), and the [license texts](licenses/). Apple software and third-party content shown in the screenshot are not licensed by this repository.
