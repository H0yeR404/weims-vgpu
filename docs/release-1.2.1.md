# Weims Manager 1.2.1

Stable release, marked Latest. It contains the Windows GUI and CLI, Guest Tools for macOS, and runtime 1.2.1.

## What is new

- **The Windows key works as Command.** With the free pointer on, the Windows key goes to macOS as Command while the pointer is inside the VM's image. With the pointer outside it, the key is Windows' own again. Other Windows shortcuts, Alt+Tab among them, stay with Windows.
- **Full screen while the VM runs.** Press **Ctrl+Alt+Enter** in the VM's window, or click **Full screen / window** in Manager; the VM need not be restarted. In full screen, rest the pointer at the top centre of the screen and a small bar appears, which minimizes the window or returns to a window.
- **Mac model.** macOS takes a VM for an iMac (27-inch, 2020) or a MacBook Pro (16-inch, 2019). Choose in **Create VM**; change it later under **Hardware & Network → Mac model** while the VM is stopped, or with `Weims.Cli model --model MacBookPro16,1`. Graphics and hardware video decoding work the same on both.

Another model comes with another serial number, so iCloud, iMessage and the App Store see a new device. Sign out of your Apple Account in macOS before the change and sign in again after it. The disk, the system UUID and the network address stay, and going back to a model brings back the serial number it had.

## Installing and upgrading

Download `Weims-Manager-1.2.1-Setup-x64.exe`, or extract `Weims-Manager-1.2.1-win-x64.zip`.

Users of 1.1.0, 1.1.1 and 1.2.0 can use **Software updates → Check for updates → Update now**; the 1.2.0-pre.1 preview is offered this release too. Users of 1.0.1 download the installer once by hand. Close VMs and other Manager windows first.

**Update the runtime after upgrading.** The three functions above are in runtime 1.2.1. After the upgrade a notice appears at the top of Manager: click **Update runtime now**. On the runtime of 1.2.0 VMs start as before, without the new functions. A runtime older than 1.2.0 must be updated before VMs can start. If you chose the runtime's location yourself, replace it yourself.

Existing VMs need not be made again, and they stay the iMac they were.

## Known issues

- Files cannot be dragged into or out of the VM's window, and this version cannot pass USB devices through yet.
- macOS names the GPU "AMD - " and the name of this PC's GPU, on every PC. The runtime's graphics driver does this on purpose: macOS and some apps choose their paths by the vendor in the name.

## Validation

Automated: 138 core checks, 88 for the WPF interface, 31 for update selection, and 127 tests of the patched window code.

With a real VM (macOS 26.7.2, one Windows 11 host with an NVIDIA GPU): the Windows key and full screen were used by hand; each Mac model was booted once without a network, with the same graphics drivers, Metal 3 and hardware decoding of H.264 and HEVC, every decoded frame compared with ffmpeg's. The model was changed with the CLI; the button in Manager runs the same code and was not clicked by hand. Not tested: installing macOS from recovery as a MacBook Pro, signing in to an Apple Account under either model, more than one display, Intel and AMD GPUs. The [validation record](validation.md) has the details.

The runtime ZIP and its corresponding source archive are attached, and every file has a SHA256 checksum.
