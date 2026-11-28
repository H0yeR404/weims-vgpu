# Building the runtime and Manager from source

The runtime is upstream [Reims vGPU](https://github.com/steelbrain/reims-vgpu) at the commit `sources.json` names, with one patch, `manager/patches/window.patch`. The patch adds the free pointer, gives the guest the Windows key while the pointer is over its image, makes the window take a size the guest fills after you resize it, and switches full screen while the VM runs, with a bar at the top of the screen. QEMU is that commit's `vendor/qemu` submodule and metal2vulkan one of its Cargo dependencies; the build fetches both at the revisions the commit names, and neither is patched.

Releases up to 1.2.0-pre.1 used a Windows port of an earlier Reims that this project carried as three large patches. Upstream has supported Windows hosts itself since 2026-11-15, so the port is gone from the tree. Its sources are in the tags of those releases.

This document is about building. It is not a macOS installer or a disk image; Manager downloads Apple's recovery image when it creates a VM.

## Host and tools

- Windows 10 2004 or later, or Windows 11, x64, with the **Windows Hypervisor Platform** turned on.
- A GPU and driver with Vulkan 1.3. The runtime checks the features it needs as a VM starts and names what is missing in the VM's log.
- [Git for Windows](https://git-scm.com/downloads/win) and [PowerShell 7](https://github.com/PowerShell/PowerShell/releases).
- [MSYS2](https://www.msys2.org/), installed in a path without spaces. The build uses its CLANG64 environment.
- The [.NET 10 SDK](https://dotnet.microsoft.com/download) for Manager, the CLI and the Guest Tools broker.

The build installs the MSYS2 packages it lacks with `pacman`, and the Rust toolchain upstream pins with `rustup` (rustup itself from https://rustup.rs if the PC has none). The package versions on the tested host are in `config/tested-msys2-packages.txt`. MSYS2 is a rolling distribution, so byte-identical binaries are not promised.

## Fetch and build

In PowerShell 7, in a checkout path without spaces or apostrophes:

```powershell
git clone https://github.com/H0yeR404/weims-vgpu
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64' # your MSYS2 directory
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

`fetch.ps1` fetches `sources/reims-vgpu` and `sources/reims-util` at the pinned commits, checks each patch's SHA-256 and applies it. It refuses a source directory that already exists.

`build.ps1` runs upstream's own installer, `sources/reims-vgpu/scripts/install-ctl.sh`, in the CLANG64 shell. That builds the graphics library, links QEMU with it, collects the DLLs QEMU loads, and installs upstream's own `reims-util` beside its reims-ctl, which this project does not use. What the installer would put in your user profile goes under `build/upstream` instead, so a reims-ctl you installed for yourself is not touched. `build.ps1` then builds `reims-util` from `sources/reims-util`, which carries `manager/patches/util.patch`, on the toolchain the installer set up. The logs are `logs/upstream-build.log` and `logs/util-build.log`. A first build took about ten minutes on the tested host; later ones rebuild only what changed.

The script then assembles `build/weims-runtime`, the folder Manager runs:

| Path | Content |
| --- | --- |
| `qemu-system-x86_64.exe`, `qemu-img.exe` | QEMU 11 with the `reims-graphics-pci` device; the graphics library is linked in |
| `reims-vgpu-compile.exe` | the library's helper process, which it starts from beside QEMU |
| `*.dll` | the CLANG64 libraries those programs load |
| `reims-util.exe` | makes OpenCore images, for an iMac20,1 or a MacBookPro16,1, and downloads Apple's recovery image |
| `boot/` | the guest drivers (Graft, ReimsGraphics, RgfxGate), their `set.toml`, and the option ROM |
| `pc-bios/` | the firmware files QEMU reads |
| `weims-runtime.json` | says this is a build of this project, and names what the patches add |

The folder runs with nothing but Windows around it. `pwsh -File scripts/build.ps1 -AssembleOnly` assembles it again without building.

## Manager, CLI and broker

```powershell
pwsh -File manager/build.ps1 -SelfContained
./manager/artifacts/Weims-Manager/Weims.Manager.exe
```

Started from a checkout with no saved settings, Manager uses `build/weims-runtime`. Otherwise choose it on **Runtime → Advanced**: `qemu-system-x86_64.exe` in that folder, the folder itself as the dependency directory, and its `pc-bios` as the firmware directory. A profile of its own keeps a test build apart from an installed Manager: `--data-dir D:\some\folder`, for Manager and the CLI alike.

Manager writes QEMU's command line itself (`manager/Weims.Core/LaunchPlan.cs`). It follows the one upstream's reims-ctl gives an x86-64 guest on Windows: guest RAM in QEMU's shared RAM backend, WHPX, a qemu-xhci controller for keyboard and tablet, and two virtio sound devices. The host's microphone is not given to the guest.

## VMs made with an earlier version

The guest drivers in a VM's OpenCore image and the option ROM belong to one build of the runtime. At the first start on another runtime, Manager makes the OpenCore image again with that runtime's drivers and the same Mac (model, serial number, board serial, UUID and ROM are read from the old image), and puts the ROM beside it. The previous image stays as `OpenCore.img.previous`. `reims-util` needs the network for this unless its cache already holds OpenCore. Boot files you supplied yourself in **Create VM → Advanced** are never replaced.

## Tests

```powershell
dotnet run --project manager/Weims.Core.Tests -c Release
dotnet run --project manager/Weims.UiTests -c Release -- <new output directory>
dotnet run --project manager/Weims.UpdateTests -c Release
dotnet build manager/Weims.Broker -c Release
dotnet run --project manager/Weims.Broker.Tests -c Release -- <repository root>
dotnet run --project manager/Weims.IntegrationTests -c Release -- <repository root>
```

The last two run the built QEMU without a guest: QMP over a Unix socket, the Guest Tools install disk as a hot-plugged USB stick, shutdown keys, a clean quit. The patch's own tests are in upstream's window crate; in an MSYS2 CLANG64 shell with the build's environment, `cargo test -p reims-vgpu-window --lib` runs them.

## Release packaging

Packaging needs Python 3 and Inno Setup 6.

```powershell
python manager/package-runtime.py --msys C:/msys64 --version <version>
pwsh -File manager/build.ps1 -SelfContained -OutputDirectory manager/artifacts/Weims-Manager-<version>
ISCC.exe manager/installer.iss
```

`package-runtime.py` checks that `build/weims-runtime` is the build of the sources on disk, runs its programs with only Windows on `PATH`, and writes the runtime ZIP and the source archive to `manager/artifacts/release`. The source archive holds the patched upstream tree with QEMU, reims-util, the Cargo dependencies of both (metal2vulkan and SDL3 among them), the MSYS2 source packages of the DLLs shipped, and the SeaBIOS and iPXE sources of the firmware files. It then writes the runtime's URL, size and SHA-256 into `manager/runtime-catalog.json`, which Manager is built with. `--runtime-only` writes the runtime ZIP alone and leaves the catalog as it is.

## What was run

On 2026-11-24, on the host in the main README (RTX 4090 Laptop GPU, Windows 11), with a macOS 26.7.2 guest on a copy-on-write overlay of an existing VM:

- The VM's boot image was made again at its first start and macOS booted to the desktop with the same Mac identity.
- Graphics, the free pointer, resizing the window (the guest's mode equalled the window's pixels afterwards), Guest Tools with the clipboard both ways, the install disk hot-plugged into the running guest, sound output, and shutdown from the CLI, after which QEMU and the broker were gone within 15 seconds.
- Automated: 115 core checks, 66 WPF checks, 22 update checks, 198 broker checks, 27 integration checks, and upstream's 117 window tests.

Not run: Intel and AMD GPUs, creating a VM and installing macOS from recovery on this runtime, relative mouse motion for games, more than one display, long sessions, and the full release packaging.
