# Weims Manager 1.2.0-pre.1

Preview of 1.2. It contains the Windows GUI and CLI, Guest Tools for macOS, and a new QEMU/Reims runtime. Stable 1.1.1 remains Latest.

## New

- **Guest Tools: clipboard sharing.** Text copied in Windows can be pasted in macOS and the other way round, while the VM's window is in front. Text up to 4 MiB is carried. What a password manager marks as sensitive stays out of the clipboard history on the other side.
- **Installed from Manager.** Turn on **Enable Guest Tools integration for this VM** and click **Install Guest Tools…**. An install disk appears in macOS; double-click **Install Weims Guest Tools** on it. The helper installs for the current macOS user, pairs with this VM and ejects the disk. Nothing is typed and no number has to be compared.
- **Guest Tools updates itself.** When a newer Manager comes with a newer helper, the helper in macOS receives it over its connection to the host and restarts. It accepts only a helper signed with the same identity as itself.
- **Runtime 1.2.0-pre.1: QEMU exits at once.** With the earlier runtime, QEMU could stay behind for minutes after its window was closed or macOS had shut down, and the VM stayed locked that long. At exit the runtime waited for graphics threads that Windows had already ended, two seconds at a time, once per registered handler. It now sees that those threads are gone and does not wait.
- **Manager replaces an older runtime.** When the runtime that Manager installed is older than the one this version comes with, Manager says so at launch and on the Runtime page, and **One-click setup** installs the new one beside it and switches. A runtime you chose yourself is left alone. Starting a VM never downloads anything.
- Manager, CLI and the Guest Tools broker are built on .NET 10. The packages are self-contained; nothing has to be installed.

The command line has the same functions: `tools-enable`, `tools-disable`, `tools-install`, `tools-status`, `tools-pair`, `tools-reset-pairing`.

## Install / upgrade

Download `Weims-Manager-1.2.0-pre.1-Setup-x64.exe`, or extract `Weims-Manager-1.2.0-pre.1-win-x64.zip`. The preview installs as **Weims Manager Preview** beside a stable installation and uses the same settings and VMs.

Stable versions are offered stable releases only, so 1.1.1 users install this preview by hand. A preview installation finds it under **Software updates → Check for updates**.

After installing, open **Runtime** and click **One-click setup** to get runtime 1.2.0-pre.1 (95 MB). Existing VMs need no change. A VM that is running keeps the runtime it was started with until it is restarted.

To remove Guest Tools from macOS, run **Uninstall Weims Guest Tools.command** from the install disk.

## How Guest Tools connects

Integration is off by default and is switched per VM. While it is on, a small host process (`Weims.Broker`) listens on the loopback interface only; macOS reaches it through the VM's own network. The connection is TLS with a certificate made for this VM, which the helper remembers when it pairs. Nothing is added to the QEMU command line and the guest gets no access to QEMU's control channel.

The helper is signed with the project's own certificate (`guest-tools/signing-certificate.pem`), not one issued by Apple.

## Known issues

- VM freezes during use, including during resolution changes, are still under investigation.
- Rendering problems on Intel graphics ([issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2)) are not fixed in this runtime.
- Guest Tools carries text only. Images, files and drag-and-drop are not included.

## Validation

Automated: 97 core, 161 Guest Tools broker, 66 WPF and 22 update-selection checks.

With a real guest (macOS 26.7.2, one Windows 11 host with an NVIDIA GPU): Guest Tools installed from Manager's button and from the CLI with Gatekeeper on; text both ways including CJK, emoji and 4 MiB; recovery after the helper or the broker was ended; a helper update from the host. The exit fix was checked by freezing a graphics thread inside the GPU driver and then quitting: the new runtime exited in 4 seconds, the old one was still there after 20 minutes.

The runtime ZIP and the corresponding source archive are attached, with SHA256 checksums for all assets.
