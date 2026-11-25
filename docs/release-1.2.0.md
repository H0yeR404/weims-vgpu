# Weims Manager 1.2.0

Stable release, marked Latest. It contains the Windows GUI and CLI, Guest Tools for macOS, and a new runtime.

## What is new

- **Runtime 1.2.0, built directly from upstream.** Upstream [Reims vGPU](https://github.com/steelbrain/reims-vgpu) now supports Windows hosts itself, so this project no longer keeps a port of its own and adds one patch for the window. What changes: macOS has sound output; macOS's resolution follows the window, and the fixed and compatibility display modes are gone; QEMU exits at once when a VM is closed or shut down; the GPU needs Vulkan 1.3.
- **Guest Tools: clipboard.** While the VM's window is in front, text copied on one side can be pasted on the other, up to 4 MiB.
- **Guest Tools: copying and pasting files.** Copy files or folders in Explorer and paste them in Finder, of any size; copy in Finder and paste in Windows, up to 256 MiB at a time.
- **Guest Tools: shared folders.** Choose folders of this PC and they are mounted in macOS, read-only if you wish. No Windows share is created, no administrator rights are needed and the firewall is not touched.
- **Update channel.** Under **Software updates**, choose stable releases only, or previews and stable releases.
- Manager, the CLI and the host side of Guest Tools are built on .NET 10. The packages carry the runtime they need.

How to install Guest Tools and the details of each function are in the [Manager README](../manager/README.md).

## Installing and upgrading

Download `Weims-Manager-1.2.0-Setup-x64.exe`, or extract `Weims-Manager-1.2.0-win-x64.zip`.

Users of 1.1.0 and 1.1.1 can use **Software updates → Check for updates → Update now**; the 1.2.0-pre.1 preview is offered this release too. Users of 1.0.1 download the installer once by hand. Close VMs and other Manager windows first.

**The runtime must be updated after upgrading.** This version of Manager cannot use the earlier runtime. After the upgrade a notice appears at the top of Manager: click **Update runtime now** (about 95 MB). VMs cannot start before that; Manager also asks, and updates for you, when you click Start. If you chose the runtime's location yourself, replace it yourself.

Existing VMs need not be made again. A VM made with an earlier version gets its boot image made again at its first start, with the new runtime's graphics drivers and the same Mac identity; the previous image stays beside it as `OpenCore.img.previous`.

Where Guest Tools is already installed, it updates itself in macOS once it connects to the new Manager.

## Known issues

- A VM may freeze during use. This is still being looked into.
- Intel GPUs ([Issue #2](https://github.com/H0yeR404/weims-vgpu/issues/2)) have not been checked with the new runtime.
- Files cannot be dragged into or out of the VM's window, and USB devices cannot be passed through.

## Validation

Automated: 120 core checks, 343 for the host side of Guest Tools, 85 for the WPF interface, 31 for update selection.

With a real VM (macOS 26.7.2, one Windows 11 host with an NVIDIA GPU): boot, graphics, free pointer, window resizing, sound, shutdown; Guest Tools' text and file copy and paste, and shared folders; moving from the 1.2.0-pre.1 runtime to the packaged 1.2.0 runtime. The [validation record](validation.md) lists what was tested and what was not.

The runtime ZIP and its corresponding source archive are attached, and every file has a SHA256 checksum.
