# Weims Manager 1.1.0

Regular release based on 1.1.0-pre.2, published as Latest. Includes the Windows GUI and CLI.

## Changes

- New light/dark appearance selector in the sidebar. Switch immediately without restarting; the choice is remembered. English and light appearance remain the defaults.
- Theme changes retain the current page and unsaved inputs, and update open setup windows. Windows high contrast takes precedence.
- Includes pre.2's one-click runtime preparation, verified software updates, release notes, and live English/Chinese switching.
- Includes guided recovery installation preparation and adaptive guest resolution from pre.1.
- GUI and CLI honor the updater's retained profile. Existing preview settings are reused; fresh installs use the stable Manager profile.

## Install / upgrade

Download `Weims-Manager-1.1.0-Setup-x64.exe`, or extract `Weims-Manager-1.1.0-win-x64.zip`. Both contain the GUI and CLI; no separate .NET installation is needed. The installer and app retain the Weims icon.

Pre.2 users can select **Software updates → Check for updates → Update now**. Close VMs and other Manager windows before installation. Versions 1.0.1 and pre.1 require a manual download once. The 1.1.0 installer uses the stable Weims Manager application identity and folder. Old Preview shortcuts remain; use the stable shortcut after updating.

Updating a portable copy installs into the per-user application folder. The updater preserves the selected profile. Without an explicit or updater-retained profile, an existing `%LOCALAPPDATA%\Weims\Manager-Preview` is reused; otherwise `%LOCALAPPDATA%\Weims\Manager` is used. GUI and CLI both accept `--data-dir`. VM disks are not converted or replaced.

## Runtime and known issues

The QEMU/Reims runtime is unchanged and remains pinned to [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1). Its runtime ZIP and corresponding source archive remain available there. Upgrading Manager does not replace a valid runtime.

**VM freezes are still possible, including during resolution changes. They remain under investigation.** Formal release status does not mean these issues are fixed. [Issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2) reports an Intel Arc 140T graphics-format error and a freeze during macOS setup; Intel Arc compatibility is not fully validated. Broad hardware testing, a complete fresh recovery-to-desktop installation, and long-term stability remain unvalidated. Clipboard sharing, file sharing and cross-system file drag-and-drop are not included.

The release changes the Manager appearance and release channel, not the guest graphics implementation. Keep backups of important VM data.

## Validation

WPF checks cover live theme/language switching, preference reload, editable selectors, unsaved inputs, both palettes, minimum window layout and the recovery wizard. Core checks cover preview/stable/custom profile selection. Update selection verifies that pre.2 can advance to 1.1.0. Installer-handoff tests use mocked process/install boundaries; a real in-place upgrade is not claimed. SHA256 checksums accompany the assets.
