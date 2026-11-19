# Weims Manager 1.1.1

Maintenance release for 1.1.0, published as Latest. Includes the Windows GUI and CLI. It fixes one security problem and the shutdown problems reported in [issue #3](https://github.com/H0yeR404/weims-vgpu/issues/3).

## Changes

- **Security: software in the guest could reach QEMU's control channel.** Versions 1.0 to 1.1.0 started QEMU with its control interface (QMP) on a local TCP port, without authentication. Software inside the VM could connect to that port through the VM's own network and control QEMU on the host. QMP now uses a Unix domain socket under `%USERPROFILE%\.weims\q`, which the guest network cannot reach. Details: [security-qmp-socket.md](security-qmp-socket.md).
- **Shut down in the Manager works** (issue #3). The button and `Weims.Cli shutdown` used to send a power-button press that lasts a few milliseconds, and macOS ignores a press shorter than 0.35 s. They now send the key combination macOS uses to quit all apps and shut down (Control-Option-Command-Power). macOS asks about unsaved documents first, as it does from the Apple menu.
- **QEMU exits when macOS powers off** (issue #3). Before, QEMU stayed behind as a black window after the guest had shut down. The VM stayed locked and its forwarded port stayed in use until that window was closed.
- The QMP port setting is gone from the Manager and the recovery wizard. Existing VM configurations are kept and can still be opened by 1.1.0.

## Install / upgrade

Download `Weims-Manager-1.1.1-Setup-x64.exe`, or extract `Weims-Manager-1.1.1-win-x64.zip`. Both contain the GUI and CLI; no separate .NET installation is needed.

1.1.0 and pre.2 users can select **Software updates → Check for updates → Update now**. Close VMs and other Manager windows before installation. Versions 1.0.1 and pre.1 require a manual download once. Profiles, VM disks and the runtime are not changed.

The fixes apply to VMs started by 1.1.1. A VM still running from an older version keeps the old behaviour until it is restarted. 1.1.1 can still manage it, and `Weims.Cli stop` still releases its QEMU after the guest has shut down.

Scripts of your own that connected to the QMP TCP port need the socket path instead. It is `qmp_socket` in the VM's `state\runtime.json`.

## Runtime and known issues

The QEMU/Reims runtime is unchanged and remains pinned to [v1.1.0-pre.1](https://github.com/H0yeR404/weims-vgpu/releases/tag/v1.1.0-pre.1). Its runtime ZIP and corresponding source archive remain available there. Upgrading Manager does not replace a valid runtime.

**VM freezes are still possible, including during resolution changes. They remain under investigation.** This release does not fix them. [Issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2) reports an Intel Arc 140T graphics-format error and a freeze during macOS setup; Intel Arc compatibility is not fully validated. Clipboard sharing, file sharing and cross-system file drag-and-drop are not included.

Shut down from the Manager needs a logged-in macOS session that responds to the keyboard. It has not been verified at the login window, in the macOS installer or recovery. Use the on-screen Shut Down there. If macOS is frozen, Force stop remains the last resort. Keep backups of important VM data.

## Validation

Automated: 86 core, 20 integration (a real QEMU process and QMP, no guest), 46 WPF, 22 update-selection and 4 runtime-setup checks, plus six installer-handoff checks with mocked process and install boundaries.

With a real guest (macOS 26.7.2, one Windows 11 host): start and status from the CLI over the socket; the former QMP port was no longer reachable from inside the guest; the old power-button request was confirmed ignored and the new key combination shut macOS down; a VM was started and shut down from the Manager window, after which QEMU exited by itself and released its port.

Not verified: a real in-place upgrade from 1.1.0, Windows 10, user profile paths with characters outside the system code page, shutdown from the login window or the installer, other macOS versions. SHA256 checksums accompany the assets.
