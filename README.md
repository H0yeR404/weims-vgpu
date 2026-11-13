# weims-vgpu

[Download Weims Manager 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1) — Windows installer, English by default, optional Chinese, and automatic runtime setup.

The Windows [Weims Manager 1.0.1](manager/README.md) adds a graphical configuration utility with recovery-image preparation and independent VM windows.

[English](README.md) · [简体中文](README.zh-CN.md) · [日本語](README.ja.md)

Run the Reims graphics lane on Windows with WHPX and Vulkan. This independent integration preserves the new guest graphics protocol while sharing Windows guest-RAM sections with the host renderer, addressing the severe copy-path slowdown observed in the earlier Windows port.

![macOS Tahoe running with weims-vgpu on Windows](docs/images/macos-tahoe.png)

The owner-supplied screenshot shows macOS Tahoe, 8 vCPUs, 32 GiB RAM and a roughly 200 GiB APFS volume. Its reported 180 Hz display mode does not represent measured rendering performance.

## Quick Start — Windows

Weims vGPU runs natively on Windows using WHPX and Vulkan. WSL2 is **not required** to run the VM.

> **Development status:** Weims Manager is still under active development and is not yet a complete installation or management solution. Some preparation remains manual. A full recovery-to-desktop installation has not yet been validated end to end with the Manager.

### Option A: Install Weims Manager (Recommended)

For most users, downloading the prebuilt application is the easiest option.

1. Download **Weims Manager 1.0.1** from the [Releases page](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1).
2. Run `Weims-Manager-1.0.1-Setup-x64.exe`.
3. Open **Runtime → Check environment**.
4. Click **Prepare / Retry** to automatically download and configure the Windows QEMU/Reims runtime.

**Requirements:**

- Windows 10 version 2004 or newer / Windows 11, x64
- CPU virtualization enabled in BIOS/UEFI
- Windows Hypervisor Platform (WHPX) enabled
- A Vulkan-compatible GPU and driver supporting `VK_EXT_external_memory_host`

**Important:** The manager automatically prepares the Windows runtime, but macOS recovery media, compatible OpenCore, OVMF firmware, and the Reims graphics ROM must currently be supplied separately. English is the default UI language; Chinese is also available.

Continue with **Prepare the boot files and install macOS** below. The main preparation and installation steps are included on this page.

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

Open **MSYS2 UCRT64** and run:

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

Open **PowerShell 7** on Windows:

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

**Step 4 — Prepare and boot a VM**

Building the runtime does not automatically create a macOS virtual machine. You still need compatible OpenCore, OVMF firmware, and macOS recovery media or an existing bootable system disk. Full ISO import is not supported by the Manager.

Continue with the boot-file preparation below, then create a new VM in the Manager or use the command-line steps for an existing system disk.

### Prepare the boot files and install macOS

The preparation order is **BaseSystem.dmg → OpenCore.img → OVMF → ReimsGraphics.rom → Weims Manager → install macOS**. These are separate inputs, not successive conversions of one image. Use a compatible Intel/x86-64 macOS release; this path does not translate Apple Silicon/ARM macOS.

**Step 1 — Obtain BaseSystem.dmg**

Obtain Apple-provided recovery media for your intended macOS version. [OpenCorePkg's macrecovery utility](https://github.com/acidanthera/OpenCorePkg/tree/master/Utilities/macrecovery) can download recovery media from Apple; follow the instructions included with your chosen OpenCore release. This utility requires Python, and the requested model/board identifier affects which recovery version is returned.

Keep the downloaded `BaseSystem.dmg` and accompanying verification files. The Manager converts the DMG to raw `BaseSystem.img`; it also accepts an already converted raw `.img`. Renaming a DMG or ISO is not conversion. Recovery downloads installation files over the network, so the guest needs access to Apple's servers. The Manager checks basic image structure, not Apple's accompanying chunklist or compatibility of every release.

**Step 2 — Prepare OpenCore and the guest drivers**

Start with a compatible x86-64 QEMU OpenCore boot image; the tested baseline uses **OpenCore 1.0.8**. Obtain OpenCore and its configuration documentation from [OpenCorePkg releases](https://github.com/acidanthera/OpenCorePkg/releases). Its ZIP is not a ready-to-boot Weims disk. Creating the VM-specific base configuration and identity remains a manual prerequisite; the Manager does not generate them.

The procedure below assumes a **raw FAT filesystem image** containing `EFI/BOOT`, `EFI/OC` and a working `EFI/OC/config.plist`. Work on a copy from a stopped VM. The new graphics lane requires the matching **Graft, ReimsGraphics and RgfxGate** guest drivers.

Option B already fetches those files. Manager users who need them can install Git, PowerShell 7 and MSYS2 using the links above, then run the following in **PowerShell 7** without compiling the host runtime:

```powershell
git clone https://github.com/H0yeR404/weims-vgpu.git
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64'
pwsh -File scripts/fetch.ps1
```

Skip fetching if Option B already populated `sources/`; the script refuses existing source directories. Use guest files matching the selected host runtime release, not unrelated upstream prebuilts.

In **MSYS2 UCRT64**, update packages as described above and install the preparation tools:

```sh
pacman -S --needed git diffutils mtools mingw-w64-ucrt-x86_64-python
```

From the repository root in that terminal, replace the example input path and prepare a copy:

```sh
mkdir -p vm/boot-work
cp /c/VM-inputs/OpenCore.img vm/boot-work/OpenCore.img
mcopy -s -i vm/boot-work/OpenCore.img ::/EFI vm/boot-work/
/ucrt64/bin/python.exe scripts/prepare-opencore.py vm/boot-work/EFI
```

Validate `vm/boot-work/EFI/OC/config.plist` with OpenCore 1.0.8's `ocvalidate`. For example, in **PowerShell 7** from the repository root, adjusting the executable path to your extracted OpenCore release:

```powershell
& 'C:\Tools\OpenCore-1.0.8-RELEASE\Utilities\ocvalidate\ocvalidate.exe' `
  'vm/boot-work/EFI/OC/config.plist'
```

After validation succeeds, return to **MSYS2 UCRT64** and write the EFI tree back:

```sh
mcopy -o -s -i vm/boot-work/OpenCore.img vm/boot-work/EFI ::/
```

The helper disables Apple Secure Boot as in the tested setup and refuses unexpected enabled kexts. Inspect dependencies if it refuses the image; do not delete unrelated entries simply to bypass the check. These mtools commands do not support partitioned EFI disks: extract or mount their EFI partition instead. For QCOW2 boot images, first convert a copy with `qemu-img convert -f qcow2 -O raw source.qcow2 OpenCore.img`; conversion does not remove a partition table.

**Step 3 — Supply OVMF and ReimsGraphics.rom**

Prepare an x86-64 QEMU-compatible `OVMF_CODE.fd` and `OVMF_VARS.fd` from the same firmware build, or use copies from a stopped compatible VM. [TianoCore EDK II / OvmfPkg](https://github.com/tianocore/edk2/tree/master/OvmfPkg) provides upstream firmware source and build instructions. CODE contains firmware; VARS stores writable UEFI settings. Do not mix firmware layouts or share writable VARS between running VMs.

Use the ROM matching the guest drivers and host runtime:

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```

The ROM does not replace the OpenCore guest drivers. Keep it at a permanent path: the Manager copies OpenCore and OVMF into the new VM but **references the ROM in place**.

**Step 4 — Create a new VM in the Manager**

After completing Option A's runtime checks, open the recovery creation wizard and supply:

| Field | Value |
| --- | --- |
| VM name | For example, `macOS-Tahoe`; 1–48 ASCII letters, digits, underscores or hyphens, starting with a letter or digit |
| Recovery image | Your `BaseSystem.dmg` or raw `BaseSystem.img` |
| Storage parent | An existing local folder; the wizard creates a new child directory named after the VM |
| System disk capacity | For example, `200` GiB; supported range is 64–2048 GiB |
| OpenCore | The prepared `vm/boot-work/OpenCore.img` |
| Firmware CODE / VARS | The matching `OVMF_CODE.fd` and `OVMF_VARS.fd` |
| Graphics ROM | The permanent path to the matching `ReimsGraphics.rom` |
| QMP / SSH ports | Distinct unused localhost ports; wizard defaults are `4455` / `2233` |

The final VM directory must not already exist. Select **Create** and wait for conversion and disk creation. The sparse system disk grows as macOS writes data; allow enough physical host storage for installation. Failed or cancelled preparation retains its staging directory for inspection.

Preparation registers the VM without starting it. Review CPU and memory: recovery VMs default to 8 vCPUs, 8 GiB RAM and `1920x1080`. Adjust resources to fit the host, keep the initial resolution at `1920x1080`, and leave recovery attachment enabled. No SMC key entry is required in the Manager.

**Step 5 — Install macOS inside the VM**

1. Start the VM. QEMU/Reims opens a separate display window.
2. Select recovery in OpenCore. Press **Space** if auxiliary entries are hidden.
3. Open **Disk Utility → View → Show All Devices**. Identify the new empty system disk by capacity and erase **only that disk** using **GUID Partition Map** and **APFS**, for example as `Macintosh HD`. Do not erase OpenCore or recovery media.
4. Quit Disk Utility, choose the macOS installation option and select the new system volume. Allow downloads and reboots to finish. Continue through the installer entry and then the installed system entry, rather than beginning a fresh recovery installation after each reboot.
5. After reaching the desktop and completing Setup Assistant, shut down through the Apple menu. With QEMU stopped, disable recovery attachment in the Manager, save, and boot again to verify the installed system starts independently. Keep OpenCore, OVMF and the ROM.

If startup fails, preserve the VM's `logs` directory and record the error before changing inputs. Check file formats and matching driver/runtime versions first. The [First Installation Guide](docs/first-install.md) provides additional explanations and troubleshooting; the main workflow is above.

### Start an existing VM from the command line

After Option B and the boot-file preparation above, shut down the original VM and make an independent system-disk copy. In **PowerShell 7 from the repository root**, replace the example input paths with your files:

```powershell
$env:WEIMS_MSYS2 = 'C:\msys64'
$env:PATH = "$env:WEIMS_MSYS2\ucrt64\bin;$env:PATH"
New-Item -ItemType Directory -Force vm/disks
./build/port-qemu/qemu-img.exe convert -p -f qcow2 -O qcow2 `
  C:/VMs/original/current.qcow2 vm/disks/current.qcow2
pwsh -File scripts/init-vm.ps1 -Disk ./vm/disks/current.qcow2 `
  -OpenCore ./vm/boot-work/OpenCore.img `
  -FirmwareCode C:/VMs/original/OVMF_CODE.fd `
  -FirmwareVars C:/VMs/original/OVMF_VARS.fd
$env:WEIMS_OSK = Read-Host 'SMC key from your own authorized installation' -MaskInput
pwsh -File scripts/start.ps1 -DryRun
pwsh -File scripts/start.ps1
```

Use a fresh destination disk path, never an existing disk you want to keep. This example expects a QCOW2 source and flattens its backing chain while retaining logical capacity. `init-vm.ps1` uses the selected disk directly for writes and refuses an existing `vm/default` directory. Review `vm/default/state/config.json` before starting: CLI defaults are 8 vCPUs, 32 GiB RAM, `1920x1080`, QMP `4444` and SSH `2222`. Adjust resources and avoid port conflicts. Unlike the Manager, the CLI requires `WEIMS_OSK`; saved command logs redact it, but QEMU receives it in process arguments.

Release captured input with **Ctrl+Alt+Win** or **Ctrl+Alt+Home**. Shut down inside macOS. If QEMU remains after guest shutdown, `pwsh -File scripts/start.ps1 -Exit` closes the emulator; it is not a substitute for normal guest shutdown. Never open the same writable disk in two instances.

### Notes

- WSL2 is optional and does not replace MSYS2 for this native Windows build.
- Use matching Reims guest drivers and host runtime versions. For a source-built runtime with free-pointer support, see [Display and free pointer](manager/README.md#display-and-free-pointer).
- Other GPUs and Windows configurations have not yet been broadly validated.
- Always back up existing VM disks and firmware before making changes.
- macOS use remains subject to Apple's software licence terms.

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
