# Reproducing the Windows build

This packages the source configuration of the working Windows/WHPX graphics lane. It is not a macOS installer or a disk image. Start with your own bootable x86-64 macOS VM and licensed materials. Source revisions are fixed in `sources.json`; upstream guest prebuilts and ROM are obtained from that same pinned source. Host binaries are built locally. Dependency versions observed on the tested host are recorded in `config/tested-msys2-packages.txt`; MSYS2 is rolling, so this is not a promise of byte-identical binaries.

For a new empty VM installed from recovery, see the [First Installation Guide](first-install.md). It explains the separately supplied boot files and the current Manager limitations.

## Host and tools

Use Windows x64 with Windows Hypervisor Platform available, a Vulkan-capable GPU/driver with `VK_EXT_external_memory_host`, PowerShell 7, Git, Rustup, and MSYS2 UCRT64. The tested host used an NVIDIA RTX 4090 Laptop GPU. The default VM uses 8 vCPUs, 32 GiB RAM and 1920×1080. Other hardware is not validated. Do not change VBS/HVCI or BIOS settings as part of these scripts.

Install MSYS2 from https://www.msys2.org/ and Rustup from https://rustup.rs/. In an MSYS2 UCRT64 terminal, update packages (`pacman -Syu`, restart the terminal if requested, then repeat). Install:

```sh
pacman -S --needed git make diffutils bison flex mtools \
  mingw-w64-ucrt-x86_64-{clang,llvm,llvm-tools,lld,cmake,meson,ninja,python,glib2,pixman,pkgconf,libslirp,libusb,SDL2,vulkan-headers,vulkan-loader,vulkan-validation-layers,spirv-tools}
```

In PowerShell 7, use an ordinary checkout path without apostrophes (a short path without spaces is recommended):

```powershell
git clone https://github.com/H0yeR404/weims-vgpu
cd weims-vgpu
$env:WEIMS_MSYS2 = 'C:\msys64' # your MSYS2 directory
rustup toolchain install nightly-2026-11-03-x86_64-pc-windows-gnu --profile minimal
pwsh -File scripts/fetch.ps1
pwsh -File scripts/build.ps1
```

`fetch.ps1` refuses existing source directories, verifies patch hashes and applies them to the pinned upstream commits. It produces sibling `sources/reims`, `sources/qemu` and `sources/metal2vulkan`. The Cargo path overrides intentionally depend on that layout. `build.ps1` builds the DLL, its import library and the QEMU executables. Outputs are `build/port-dist` and `build/port-qemu`; logs are under `logs/`. Keep MSYS2 available at runtime for dependency DLLs.

Run the targeted C API tests in a fresh PowerShell process:

```powershell
. ./scripts/environment.ps1
Push-Location sources/reims
cargo test --locked -p reims-vgpu-c --lib
Pop-Location
```

## Prepare the guest boot environment

Use your own working OpenCore 1.0.8 raw FAT image and matching OVMF CODE/VARS pair. OpenCore comes from https://github.com/acidanthera/OpenCorePkg; OVMF is the EDK II firmware at https://github.com/tianocore/edk2. Their source/build instructions and licenses apply. Retain the firmware pair and your own valid VM identity; do not borrow the screenshot owner's serial numbers. This guide migrates a working VM, not a fresh macOS installation.

The old graphics lane's OpenCore alone is insufficient: inject the new lane's Graft, ReimsGraphics and RgfxGate set. Shut down the VM, then work on copies. In MSYS2, from the repository root, extract your boot image's EFI directory:

```sh
mkdir -p vm/boot-work
cp /path/to/your/OpenCore.img vm/boot-work/OpenCore.img
mcopy -s -i vm/boot-work/OpenCore.img ::/EFI vm/boot-work/
/ucrt64/bin/python.exe scripts/prepare-opencore.py vm/boot-work/EFI
```

The helper copies the pinned kext set, preserves your other configuration, disables Apple Secure Boot as in the tested setup, and refuses unexpected enabled kexts rather than deleting their dependencies silently. Validate `vm/boot-work/EFI/OC/config.plist` with OpenCore 1.0.8's `ocvalidate` before copying the EFI tree back:

```sh
mcopy -o -s -i vm/boot-work/OpenCore.img vm/boot-work/EFI ::/
```

These mtools commands assume a raw FAT filesystem image as used by this project, not a partitioned disk or QCOW2 OpenCore image. For a QCOW2 boot image, convert a copy with `qemu-img convert -f qcow2 -O raw source.qcow2 OpenCore.img` first; partitioned EFI images require mounting/extracting the EFI partition instead. Keep your original boot files unchanged.

## Configure and start

With the source VM fully stopped, create an independent system-disk copy (merges its backing chain). A 200 GiB disk retains its logical size; no resize is needed:

```powershell
$env:PATH = "$env:WEIMS_MSYS2\ucrt64\bin;$env:PATH"
New-Item -ItemType Directory -Force vm/disks
./build/port-qemu/qemu-img.exe convert -p -f qcow2 -O qcow2 C:/VMs/original/current.qcow2 vm/disks/current.qcow2
pwsh -File scripts/init-vm.ps1 -Disk ./vm/disks/current.qcow2 `
  -OpenCore ./vm/boot-work/OpenCore.img `
  -FirmwareCode C:/VMs/original/OVMF_CODE.fd `
  -FirmwareVars C:/VMs/original/OVMF_VARS.fd
$env:WEIMS_OSK = Read-Host 'SMC key from your own authorized installation' -MaskInput
pwsh -File scripts/start.ps1 -DryRun
pwsh -File scripts/start.ps1
```

Replace example input paths with your files. `init-vm.ps1` uses the selected system disk directly for writes; it copies boot firmware and generates a fresh network MAC. It refuses existing VM directories. The ignored local configuration lives in `vm/default/state/config.json`. Adjust resources there if needed. Never open the same writable disk concurrently. Default QMP 4444 and SSH 2222 bind to localhost; stop conflicting VMs first. QMP has no authentication and is intended only for local use. The OSK is omitted from saved command logs but is necessarily passed to QEMU's process arguments.

Release captured input with Ctrl+Alt+Win or Ctrl+Alt+Home. Shut down macOS normally first. If QEMU remains after shutdown, `pwsh -File scripts/start.ps1 -Exit` closes the emulator; do not use it as a normal guest shutdown.

## What was validated

The source implementation passed 22 C API tests (one manual measurement ignored), built the release DLL and QEMU, and booted macOS. The shared-memory version achieved 54.17 unique FPS in one 10-second, 60 Hz window-drag test at 1920×1080; maximum observed frame gap was 211 ms. This is not a same-scene comparison against the old lane or a long stability result. The screenshot's 180 Hz display mode is not a measured 180 FPS claim. Guest RAM section sharing bypasses the main read-back bottleneck, but device-cache downloads remain: this is not full zero-copy.

This packaging separately checks source reconstruction and script syntax. See `validation.md` for the publication checks and any remaining limits. Full cross-platform `cargo xtask check` has not been run. Video acceleration, audio and broad GPU compatibility are not claimed.

## Preview 1.1.0-pre.1 additions

After the native build, install .NET 8 SDK and run in Windows PowerShell 7:

```powershell
pwsh -File manager/build-display.ps1
pwsh -File manager/build-setup.ps1
pwsh -File manager/build.ps1 -SelfContained
```

The setup helper is pinned to reims-util commit `84e5c98ae7fa6b0b68e7be690525dda29697ab34`. `build-setup.ps1` expects unchanged sources and fresh boot output; it refuses to overwrite an existing boot kext directory. Stop VMs before rebuilding their runtime. Use the packaged CLI documented in the main README for automatic recovery and boot preparation. This does not require the manual EFI procedure above. The helper generates a GPT-partitioned OpenCore image, so the raw-FAT mtools commands above do not apply to its output.

Release packaging requires Python 3, Inno Setup 6 and the source dependencies. From `sources/reims`, vendor both Rust dependency sets with an absolute helper manifest path:

```powershell
cargo vendor --locked --sync C:/src/weims-vgpu/sources/reims-util/Cargo.toml ../../build/release-sources/vendor
```

Then from the repository root:

```powershell
python manager/package-runtime.py --msys C:/msys64 --version 1.1.0-pre.1
pwsh -File manager/build.ps1 -SelfContained -OutputDirectory manager/artifacts/Weims-Manager-1.1.0-pre.1
ISCC.exe manager/installer.iss
```

Packaging verifies dependency closure without MSYS2 on PATH and writes the pinned runtime catalog before building the GUI/CLI. Sources ZIP includes patched upstream trees, combined Cargo vendor sources, MSYS2 package sources and licenses. Manager/CLI sources are in tag `v1.1.0-pre.1`. Apple recovery and generated machine identities are not distributed. The runtime is uploaded under the exact catalog URL; SHA256SUMS accompanies the installer, portable app and runtime/source archives.
