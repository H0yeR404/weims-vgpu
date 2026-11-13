# First installation with Weims Manager

This guide describes **BaseSystem.dmg → OpenCore.img → OVMF → ReimsGraphics.rom → Weims Manager → install macOS** on Windows x64. This is the preparation order: the files are separate components, not successive conversions of one image. At boot, OVMF starts OpenCore, which loads the recovery system or installed macOS.

> **Under development:** Weims Manager is not yet a complete installation or management solution. It prepares recovery media and VM files, but does not download macOS, generate a compatible OpenCore configuration, or perform the installation inside the guest. The full recovery-to-desktop workflow has not yet been validated end to end with the Manager. The project's confirmed desktop results came from an existing VM.

The current path is for compatible **Intel/x86-64 macOS** guests. It does not translate Apple Silicon/ARM macOS to x86. Full ISO import is not supported. Use Apple-provided recovery media subject to Apple's software licence terms.

## 1. Obtain BaseSystem.dmg

Obtain recovery media for an Intel-compatible macOS release. One upstream tool is `Utilities/macrecovery/macrecovery.py` in [OpenCorePkg](https://github.com/acidanthera/OpenCorePkg/tree/master/Utilities/macrecovery); follow the instructions shipped with your chosen OpenCore release to download from Apple's recovery service. This download utility requires Python, independently of the Manager. The requested model/board identifier affects which recovery version Apple returns, so do not assume that a generic download command selects Tahoe or the version you want.

Keep the downloaded `BaseSystem.dmg` and any accompanying verification files together. Select `BaseSystem.dmg` in the Manager; it converts the image to raw `BaseSystem.img` automatically. Renaming a DMG or ISO to `.img` does not convert it. The Manager also accepts an already converted raw recovery `.img`.

Recovery is a small boot environment, not a complete offline installer. The guest needs network access to Apple's installation servers. The Manager checks image format, expanded size (128 MiB–32 GiB) and basic partition/filesystem signatures; it does not validate Apple's accompanying chunklist or guarantee macOS compatibility.

## 2. Prepare OpenCore.img

Use a **VM-compatible x86-64 OpenCore configuration** for the selected macOS version and this QEMU machine. The existing reproduction baseline uses OpenCore 1.0.8. Obtain OpenCore and its documentation from [OpenCorePkg releases](https://github.com/acidanthera/OpenCorePkg/releases). The upstream ZIP is not a ready-to-boot Weims disk: renaming it to `OpenCore.img` will not work.

The Manager expects a raw boot disk image. The repository's injection procedure specifically supports a raw FAT filesystem image containing `EFI/BOOT` and `EFI/OC`, with `EFI/OC/config.plist` already configured for your VM. It does not create that baseline configuration or your machine identity. If you have no compatible boot image yet, this remains a manual prerequisite; consult the OpenCore configuration documentation supplied with the release before proceeding. Do not use another person's serial numbers.

The boot configuration must load the matching **Graft, ReimsGraphics and RgfxGate** guest set. An old-channel OpenCore image alone is insufficient. Fetch the repository's pinned sources using [Option B in the README](../README.md#option-b-build-from-source-advanced-users); source fetching supplies the prebuilts without requiring the host build step. Follow [Prepare the guest boot environment](reproduce.md#prepare-the-guest-boot-environment) to inject them into a copy of your boot image.

For an already compatible raw FAT boot image, the commands below run in **MSYS2 UCRT64 from the repository root**, with Python and mtools installed:

```sh
mkdir -p vm/boot-work
cp /c/VM-inputs/OpenCore.img vm/boot-work/OpenCore.img
mcopy -s -i vm/boot-work/OpenCore.img ::/EFI vm/boot-work/
/ucrt64/bin/python.exe scripts/prepare-opencore.py vm/boot-work/EFI
```

Replace `/c/VM-inputs/OpenCore.img` with your source image. Validate the resulting `vm/boot-work/EFI/OC/config.plist` with **OpenCore 1.0.8's `ocvalidate`** before writing it back:

```sh
mcopy -o -s -i vm/boot-work/OpenCore.img vm/boot-work/EFI ::/
```

The helper disables Apple Secure Boot as in the tested configuration and refuses unexpected enabled kexts. If it refuses your image, inspect its dependencies; do not delete unrelated kext entries simply to bypass the check. A partitioned EFI disk requires extracting/mounting its EFI partition instead of the raw-FAT commands above. QCOW2 boot images must first be converted to raw copies as explained in the reproduction guide.

## 3. Prepare matching OVMF firmware

OVMF is the VM's UEFI firmware. Obtain an x86-64 QEMU-compatible `OVMF_CODE.fd` and matching `OVMF_VARS.fd` from the same firmware build, or reuse copies from your stopped, compatible VM. [TianoCore EDK II](https://github.com/tianocore/edk2/tree/master/OvmfPkg) is the upstream source and build documentation. This repository does not currently provide a universal preconfigured firmware pair.

CODE contains firmware instructions; VARS contains writable UEFI settings. Do not mix different firmware layouts or share one writable VARS file between running VMs. The Manager copies both selected files into the new VM directory. Select files from a stopped VM or independent templates, not files currently in use. Its file checks cannot establish that an arbitrary CODE/VARS pair is compatible.

## 4. Obtain ReimsGraphics.rom

After fetching the pinned sources, use:

```text
sources/reims/firmware/prebuilt/ReimsGraphics.rom
```

This is the graphics device's boot ROM; it does not replace the guest kexts in OpenCore. Use the ROM and guest prebuilts corresponding to the selected host runtime. For the published runtime, consult its release's source material and pinned revisions rather than taking files from the upstream default branch. The repository records its revisions in [sources.json](../sources.json).

Store the ROM at a permanent local path. **The recovery wizard references the selected ROM in place; it does not copy it into the VM directory.** Moving or deleting that source file will prevent later startup until the path is corrected.

## 5. Prepare the VM in Weims Manager

Install [Weims Manager 1.0.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.0.1), open **Runtime → Check environment**, then select **Prepare / Retry**. The prebuilt runtime path requires no WSL2, MSYS2 or compiler. Enable CPU virtualization and Windows Hypervisor Platform if the readiness checks require them, and install a compatible Vulkan GPU driver. Restart Windows if enabling WHPX requires it, then rerun the checks. English is the default; Chinese can be selected in the sidebar.

Open the recovery creation wizard and supply these inputs:

| Input | What to select |
| --- | --- |
| VM name | 1–48 ASCII letters, digits, underscores or hyphens; begin with a letter or digit, for example `macOS-Tahoe` |
| Recovery image | Your local `BaseSystem.dmg` or raw `BaseSystem.img` |
| Storage parent | An existing local folder with sufficient free space; the wizard creates a new child directory using the VM name |
| System disk capacity | 64–2048 GiB; for example, `200` creates a sparse 200 GiB QCOW2 disk |
| OpenCore | The prepared raw `OpenCore.img` with matching Reims guest drivers |
| Firmware CODE / VARS | The matching OVMF pair from step 3 |
| Graphics ROM | The permanent path to `ReimsGraphics.rom` from step 4 |
| QMP / SSH ports | Distinct unused localhost ports; the wizard defaults to `4455` and `2233` |

The final VM directory must not already exist. Keep enough physical disk space for the expanded recovery image and the installed system; a sparse disk's configured capacity is not reserved host storage. The wizard's minimum space check is not a complete installation-space estimate.

Select **Create** and wait for preparation to finish. The Manager converts the recovery image, creates an empty system disk, copies OpenCore and OVMF, generates a network MAC address, and registers the VM. Source images remain untouched. A failed or cancelled operation retains a staging directory for inspection; retry with a new destination after examining the reported error.

The resulting layout is:

```text
<storage-parent>/<VM-name>/
  disks/
    BaseSystem.img
    current.qcow2
  firmware/
    OpenCore.img
    OVMF_CODE.fd
    OVMF_VARS.fd
  state/
    config.json
```

Review the registered settings before starting. The recovery creation service defaults to 8 vCPUs, 8 GiB RAM and `1920x1080`; allocate resources within your host's available capacity. Keep the initial resolution at `1920x1080` for the first boot. The Manager currently validates startup sizes within 800–2048 × 600–1536. Leave recovery attachment enabled. No SMC key entry is required in the Manager.

## 6. Boot recovery and install macOS

Start the VM from the Manager. QEMU/Reims opens a separate display window. In the OpenCore picker, select the recovery entry; its label depends on the image. Press **Space** if auxiliary entries are hidden. If OpenCore never appears, check the boot image and firmware before attempting to format any disk.

In recovery, open **Disk Utility**, select **View → Show All Devices**, and identify the **new empty system disk** by its capacity. Erase only that disk using **GUID Partition Map** and **APFS**, with a name such as `Macintosh HD`. Do not erase the recovery disk or OpenCore boot disk. If you cannot identify the target unambiguously, stop and check the VM configuration.

Quit Disk Utility and choose the macOS installation option. Select the newly formatted system volume and allow the installer to download its files. Installation can reboot several times; let the installation continue using its installer entry, then the installed system entry. Do not keep restarting a fresh recovery installation after each reboot, and do not force-stop QEMU while installation is writing to disk.

After reaching the macOS desktop and completing Setup Assistant, shut down through the Apple menu. With QEMU stopped, turn off recovery attachment in the Manager, save, and start again to check that the installed system boots independently. Retain OpenCore, OVMF and the graphics ROM: the installed system still needs them.

## If installation stops

| Symptom | First checks |
| --- | --- |
| Environment check fails | BIOS virtualization, WHPX/restart, GPU driver and `VK_EXT_external_memory_host`; retry runtime setup after resolving the reported failure |
| Destination already exists | Choose a new VM name/destination; do not overwrite an existing system disk |
| Recovery conversion fails | Verify it is a supported BaseSystem DMG/raw image, inspect the error and staging folder, and check host free space |
| No OpenCore picker | Check raw boot image format, EFI contents and matching x86-64 OVMF CODE/VARS |
| Recovery is absent | Check recovery attachment, press Space in OpenCore, and verify the recovery version is compatible |
| Black screen or guest panic | Preserve logs and check that the ROM, OpenCore guest kexts and host runtime match; a missing file check alone cannot validate the boot configuration |
| Installer cannot download | Check the guest network and access to Apple's servers; recovery is not an offline installer |

Keep the VM's `logs` directory and record the visible error and time before retrying. Avoid repeatedly changing firmware, graphics versions and recovery media together: change one input at a time so the result is attributable. See [Manager Documentation](../manager/README.md) and the [Complete Reproduction Guide](reproduce.md) for runtime details and existing-VM migration.
