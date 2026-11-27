# UsbDk in Weims

Weims passes a USB device on this PC into the macOS guest with QEMU's `usb-host`. On Windows that needs the
[UsbDk](https://github.com/daynix/UsbDk) driver. The official 64-bit installer is kept here so Manager can install it;
the user does not download it.

| File | What it is |
|---|---|
| `UsbDk_1.0.22_x64.msi` | The installer from the v1.00-22 release, unchanged |
| `LICENSE.txt` | Apache License 2.0, the license of that release |

SHA-256 of the installer: `91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5`.

Manager checks that hash before it runs the installer, then asks Windows for an administrator. Source and newer
releases: <https://github.com/daynix/UsbDk>.

QEMU 11 does not ask libusb for the UsbDk backend, and libusb 1.0.30 only uses it when asked after
`libusb_init`. `scripts/libusb-usbdk.c` is built in front of the runtime's `libusb-1.0.dll` so the VM and
Manager read the same ports. The original library is kept as `libusb_orig.dll`.
