# Weims Manager 1.0.1

> **Development status:** The interactive manager is still incomplete and under active development. Some preparation remains manual; a full recovery installation has not yet been validated end to end. See the [First Installation Guide](../docs/first-install.md).

Windows configuration manager for the shared-memory Reims graphics lane. The English/Chinese WPF interface uses a macOS Settings-inspired sidebar, grouped cards and switches. QEMU/Reims keeps its own native display window. The manager does not embed or stream the guest desktop.

[中文使用说明](README.zh-CN.md) · [Architecture and implementation plan](../docs/manager-plan.md)

## Install and prepare the environment

Download `Weims-Manager-1.0.1-Setup-x64.exe` from [v1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1). The per-user installer includes .NET and adds a Start menu shortcut. The portable ZIP is also available. Windows 10 2004 or newer on x64 is required; compatible CPU virtualization and GPU support are still necessary.

**English is the default**, regardless of Windows language. Select 简体中文 or English in the sidebar and reopen the manager to apply the saved choice. Closing the manager does not shut down VMs.

On **Runtime**, select **Check environment**, then **Prepare / Retry**. The manager downloads the pinned QEMU/Reims runtime from this project's release and verifies its SHA256 before extraction. If PowerShell 7 is missing, it downloads an official Microsoft portable build into the same private data area. No MSYS2 installation or compiler is needed for this path. Progress is shown and downloads can be cancelled. A successful executable/capability check is required before new paths replace saved settings. Retry creates a new directory and never overwrites an in-use runtime; older downloaded directories remain available.

Checks cover OS/architecture, firmware virtualization, WHPX, a Vulkan GPU with `VK_EXT_external_memory_host`, runtime executables and PowerShell. They are a readiness check, not proof that every macOS guest will boot. **Windows Features** opens the system dialog to enable Windows Hypervisor Platform; Windows may request administrator approval and a restart. BIOS virtualization and GPU drivers require your manual action. The app does not reboot or change security settings automatically.

Downloads and preferences live under `%LOCALAPPDATA%/Weims/Manager`. Uninstall removes the app, not VM disks, preferences or downloaded runtimes. Recovery images, prepared OpenCore/OVMF and the graphics ROM remain user-supplied.

## Build and run

Install the .NET 8 SDK on Windows. The manager has no NuGet UI dependencies. Build the host runtime using the repository's [reproduction guide](../docs/reproduce.md), then publish the manager:

```powershell
pwsh -File manager/build.ps1 -SelfContained
./manager/artifacts/Weims-Manager/Weims.Manager.exe --repo C:/src/weims-vgpu
```

Copy the entire output directory when moving the app. A self-contained build includes .NET; PowerShell 7 and QEMU/Reims can be prepared automatically, or configured from an existing build. Without `-SelfContained`, install the .NET 8 Windows Desktop runtime. Preferences and the registered configuration paths are stored under `%LOCALAPPDATA%/Weims/Manager`; use `--data-dir <directory>` for isolated testing.

## Configure an existing VM

Import its `state/config.json`. This registers the configuration without copying or writing its disk. Import requires existing input files and a new-lane OpenCore setup; it does not migrate old guest drivers. CPU, memory, localhost SSH/QMP ports, resolution, borderless mode and recovery attachment can be edited after QEMU stops. Unknown JSON fields are retained. Do not open the same writable disk from another launcher simultaneously.

Select `qemu-system-x86_64.exe`, the MSYS2 UCRT64 `bin` directory, and QEMU's `pc-bios` directory on the runtime page. The executable's directory must contain `qemu-img.exe` and the matching `reims_vgpu_c.dll`. Runtime checks verify that executables load and advertise WHPX and the shared-memory graphics device; they do not prove that the Windows WHPX feature is enabled or that Vulkan supports the required external-memory extension.

The manager embeds the fixed SMC OSK used by this Intel macOS/QEMU configuration; no manual input is required. An optional nonempty `WEIMS_OSK` environment variable overrides the default. This is not an Apple ID password or a unique machine identity. Overrides are not persisted by the manager. QEMU necessarily receives it in its process arguments. The command preview and saved command record redact it. Normal shutdown sends `system_powerdown`; macOS may require shutdown through its own menu. Force stop uses identity-checked QMP `quit` after a warning. Closing the manager leaves QEMU running and its logs attached to files.

## Display and free pointer

Resolution is the guest's startup mode, separate from host window dimensions. Presets and custom modes are supported within 800–2048 × 600–1536; wider Tahoe modes are intentionally rejected because of the existing WindowServer issue. Borderless mode uses `REIMS_VGPU_WINDOW_BORDERLESS`. Windowed mode can be resized manually and preserves image aspect ratio.

The original backend captures input on click. To enable free pointer, apply the incremental patch **after** the pinned base patches from `scripts/fetch.ps1`:

```powershell
$env:WEIMS_MSYS2 = 'C:/msys64'
pwsh -File manager/build-input.ps1
```

The helper builds the DLL and assembles a separate `build/manager-runtime` directory, with a SHA256-bound `weims-input-v1.json` capability marker. Choose that QEMU executable in the manager. The free-pointer switch stays disabled for an unmarked or mismatched DLL. This option sets `REIMS_VGPU_WINDOW_FREE_POINTER=1` at process startup. On Windows, a click then forwards absolute pointer input without activating mouse confinement or keyboard grab. Focus loss still releases held input. Capture mode is unchanged when the option is off; Ctrl+Alt+Win or Ctrl+Alt+Home releases it. This is not clipboard sharing, file drag-and-drop, or guest-relative game input. Other OS defaults remain unchanged.

## Recovery preparation

The wizard accepts a local `BaseSystem.dmg` or raw `.img`, a new destination, 64–2048 GiB system-disk capacity, and your prepared new-lane OpenCore raw FAT image, matching OVMF CODE/VARS, and Reims ROM. It does not generate machine identities or patch arbitrary OpenCore images. Prepare those files using the existing reproduction guide. Never select firmware from a running VM.

`qemu-img info` inspects the image, then `convert` expands it to raw in an isolated staging directory. Basic partition/filesystem signatures are checked; these checks do not certify that the image is a valid or compatible recovery system. A sparse QCOW2 system disk and independent firmware copies are created. Publication uses a non-replacing directory move. Existing destinations and ISO input are rejected. Cancellation or failure retains the staging directory for inspection; original inputs are untouched.

The manager registers the result without starting it. Start the VM, select recovery in OpenCore (Space may reveal auxiliary entries), use Disk Utility to erase **only the new empty system disk** as GUID/APFS, then install macOS over the network. After installation, shut down and turn off recovery attachment. The manager does not erase disks inside the guest, download macOS, support full ISO, or guarantee every recovery version is compatible.

## Validation

```powershell
dotnet run --project manager/Weims.Core.Tests -c Release
dotnet run --project manager/Weims.UiTests -c Release -- C:/temp/weims-ui-evidence
dotnet run --project manager/Weims.IntegrationTests -c Release -- C:/src/weims-vgpu C:/msys64/ucrt64/bin
# With the repository Rust/MSYS2 environment configured:
cargo test --manifest-path sources/reims/Cargo.toml --locked -p reims-vgpu-window --lib
```

On the development host, 41 core contract checks, 13 bilingual WPF interaction checks, 9 real-tool integration checks and 98 Rust window tests passed. Integration uses a synthetic raw fixture, a fresh QCOW2 disk, and diskless stopped QEMU; it never boots a guest OS. It verifies source preservation, staging publication, capacity, launch serialization, QMP identity/quit and child rollback after runtime-record failure. WPF evidence covers six pages, 960×660 layout, the recovery form, model validation after navigation and the actual save/preview handlers. Native desktop automation could not bind the window because of a host tool identity error; screenshots were produced through WPF rendering instead.

Full recovery installation, free-pointer behavior in a running macOS guest, high-contrast mode, screen-reader operation and extended stability have not been manually validated. No performance claim is made for this manager release. Audio, USB passthrough, disk expansion of existing guests, live resolution switching, snapshots and saved state are outside version 1.0.

## Source and licenses

Manager code is GPL-2.0-or-later under the repository [LICENSE](../LICENSE). The incremental Reims patch retains the upstream licensing and attribution in [NOTICE.md](../NOTICE.md). UI graphics are repository-authored vector shapes; no Apple fonts or macOS media are bundled. The local self-contained .NET output retains its runtime license notices. No prebuilt QEMU/Reims binaries or macOS images are committed with the manager sources.

## Reproduce release packages

Build the runtime and input extension first. With the pinned Rust toolchain configured, run `cargo vendor --locked ../../build/release-sources/vendor` from `sources/reims`. From the repository root, run:

```powershell
python manager/package-runtime.py --msys C:/msys64
pwsh -File manager/build.ps1 -SelfContained -OutputDirectory manager/artifacts/Weims-Manager-1.0
& 'C:/path/to/Inno Setup/ISCC.exe' manager/installer.iss
```

The runtime packager computes the recursive DLL closure, downloads exact MSYS2 source packages, retains license notices, archives patched upstream sources and Cargo dependencies, and writes `manager/runtime-catalog.json` with the runtime ZIP checksum. It tests the binaries without MSYS2 on PATH. Rebuilt ZIP bytes may differ: publish the matching catalog with every rebuilt manager; never silently replace an existing release asset. The source ZIP and repository tag together supply the build material. `SHA256SUMS.txt` covers release downloads. Packages are not Authenticode-signed.
