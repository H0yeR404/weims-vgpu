# weims-vgpu

> Latest preview: **1.1.0-pre.2** — software updates, release notes and instant English/Chinese switching. [v1.1.0-pre.2](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.2)

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

![macOS Tahoe on Windows](docs/images/macos-tahoe.png)

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

[Prerelease details](docs/prerelease-1.1.0-pre.1.md) · [Stable 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1). Stable 1.0.1 does not include automatic boot preparation or adaptive resolution.

### Option B: Build from Source (Advanced Users)

Use this method if you want to modify Weims, build the Windows graphics backend, or reproduce the project from source.

**Step 1 — Install prerequisites**

Install:

- [Git for Windows](https://git-scm.com/downloads/win)
- [PowerShell 7](https://github.com/PowerShell/PowerShell/releases)
- [Rustup](https://rustup.rs/)
- [MSYS2](https://www.msys2.org/)

Install MSYS2 to `C:\msys64` or update the paths below accordingly.

**Step 2 — Configure MSYS2**

These commands run in **Windows MSYS2 UCRT64, not WSL2**. MSYS2 uses `pacman`, but its `mingw-w64-ucrt-x86_64-*` packages are Windows build tools, not Arch Linux packages. Debian/Ubuntu `apt`, Arch `pacman` and Fedora `dnf` cannot replace this step for the native Windows build.

Open **Start → MSYS2 UCRT64**, or launch it from **Windows PowerShell**:

```powershell
& 'C:\msys64\msys2_shell.cmd' -defterm -here -no-start -ucrt64
```

In the newly opened shell, verify `echo "$MSYSTEM"` prints `UCRT64`. If it does not, stop and open the correct terminal. Run the following commands **inside that UCRT64 shell**, not directly in PowerShell, Command Prompt, Git Bash or a WSL terminal:

```sh
pacman -Syu
```

Restart the MSYS2 terminal if requested, then run the update again.

Install the required build tools:

```sh
pacman -S --needed git make diffutils bison flex mtools \
  mingw-w64-ucrt-x86_64-{clang,llvm,llvm-tools,lld,cmake,meson,ninja,python,glib2,pixman,pkgconf,libslirp,libusb,SDL2,vulkan-headers,vulkan-loader,vulkan-validation-layers,spirv-tools}
```

**Step 3 — Download and build Weims**

Return to **PowerShell 7 on Windows** for the following commands. `scripts/build.ps1` invokes MSYS2 as needed and targets Windows executables; it is not a Linux build script:

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu

$env:WEIMS_MSYS2 = "C:\msys64"

rustup toolchain install nightly-2026-11-03-x86_64-pc-windows-gnu --profile minimal

pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

The scripts will:

- Download pinned Reims, QEMU, and metal2vulkan sources
- Verify and apply the Windows compatibility patches
- Build the native Windows QEMU/Reims components

Build outputs:

```text
build/port-qemu/
build/port-dist/
```

The corresponding graphics ROM is located at:

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```


Install the .NET 8 SDK before building the Manager/CLI. Then run:

```powershell
pwsh -File manager/build-display.ps1
pwsh -File manager/build-setup.ps1
pwsh -File manager/build.ps1 -SelfContained
```

### Optional: WSL2 tools for inspecting or preparing EFI files

Skip this section for the Windows workflow above. WSL2 can provide Linux tools for inspecting files or preparing a copied raw FAT OpenCore image; the packages below are **not** a replacement build environment for Windows QEMU/Reims. They do not enable WHPX or supply the Windows Vulkan driver.

If you already use WSL2, run the following in **Windows PowerShell** to list installed distributions and enter one. Replace `Ubuntu` with its exact installed name (for example `Debian`, `Arch`, or the Fedora name shown by the list):

```powershell
wsl --list --verbose
wsl -d Ubuntu
```

Confirm the selected distribution uses WSL version `2` in the list. Inside the Linux shell, inspect `/etc/os-release` with `cat /etc/os-release`, then use **only the matching package commands** below. These commands assume a user with `sudo` access; a root shell can omit `sudo`.

**Debian / Ubuntu — inside WSL2:**

```sh
sudo apt update
sudo apt install git python3 mtools
```

**Arch Linux — inside WSL2:**

```sh
sudo pacman -Syu --needed git python mtools
```

**Fedora — inside WSL2:**

```sh
sudo dnf install git python3 mtools
```

The EFI helper requires **Python 3.11 or newer** for `tomllib`. Check `python3 --version` and `python3 -c 'import tomllib'`; if unavailable, use a distribution release providing Python 3.11+ or the MSYS2 preparation path above.

Windows paths differ between shells: `C:\VM-inputs\OpenCore.img` is `/c/VM-inputs/OpenCore.img` in MSYS2 and `/mnt/c/VM-inputs/OpenCore.img` in WSL2. In WSL2, change to the actual repository path, such as `cd /mnt/d/Coding/weims-vgpu`, and run the EFI helper as `python3 scripts/prepare-opencore.py vm/boot-work/EFI` instead of `/ucrt64/bin/python.exe ...`. Use this only as an alternative for the copied-image EFI preparation step, with sources already fetched; do not run both preparation paths on the same copy. Run Windows `ocvalidate`, builds and VM startup back in Windows PowerShell. Type `exit` to leave WSL2.

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

The implementation passed 22 C API tests. One 10-second drag test measured 54.17 unique FPS, with a maximum 211 ms frame gap. These are limited measurements, not universal performance or stability guarantees. Shared guest memory still involves host device caches and is not full zero-copy.

## Upstream and licensing

Built on [Reims vGPU](https://github.com/steelbrain/reims-vgpu), [metal2vulkan](https://github.com/steelbrain/metal2vulkan), [QEMU Reims](https://github.com/steelbrain/qemu-reims-vgpu) and Windows work from [tqmane](https://github.com/tqmane/reims-vgpu). This project is not an official upstream release.

Reims and metal2vulkan retain `GPL-2.0-or-later OR LGPL-3.0-or-later`; QEMU retains its original per-file licenses. Integration scripts and documentation use GPL-2.0-or-later. Read [LICENSE](LICENSE), [attribution and provenance](NOTICE.md), and the [license texts](licenses/). Apple software and third-party content shown in the screenshot are not licensed by this repository.
