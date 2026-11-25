# Validation scope

## Moving from the 1.2.0-pre.1 runtime to 1.2.0, 2026-11-25

Tested with the 1.2.0 Manager build and the packaged runtime ZIP, on one Windows 11 host with a macOS 26.7.2 guest:

- The setup test program on the packaged ZIP: SHA-256, extraction, validation with the packaged QEMU, environment detection, and that a managed runtime of another version is reported, goes on to the catalog's download and stays in place when that is cancelled.
- A settings folder holding the real 1.2.0-pre.1 runtime as a Manager-installed runtime: `Weims.Cli start` refuses with the notice that the runtime must be updated, and starts nothing; `status` still works.
- The 1.2.0 runtime unpacked beside it under the catalog's name, after its checksum was compared with the catalog: the VM starts from it, the desktop shows, Guest Tools connects 39 s after the start command and both shared folders are mounted.
- The banner and the texts of a required update, in the UI tests.
- A runtime of 1.1.0-pre.1 in a folder of the user's own choosing: recognised as one that cannot be used and reported as a required update (the setup test program with `--old-runtime`; nothing was started). Before this was fixed, such a runtime was taken for an incomplete one, Manager went straight to the download, and no notice showed.
- A download answered with 404: the message names the package and its address, and nothing is left behind.

Not tested: the download of the runtime from the release page (it does not exist before the release), the banner's button and the dialog before a start clicked in a real Manager window, the updater taking a 1.1.1 installation to 1.2.0, and a fresh installation from recovery to the desktop on this runtime. A VM of an earlier version getting new boot files at its first start was tested on 2026-11-24 (below), not again with the packaged runtime.

## File copy and paste, 2026-11-25

Copy and paste of files between Windows and macOS was checked on the machine and guest of the shared folders below, with helper 0.3.0 built in the guest and the broker of this tree in place of the running one.

- **Automated tests.** 343 broker checks, 68 of them new: the list of files as Windows keeps it on the clipboard, the share that copied files are read from and brought to (with the library's own SMB client standing in for macOS), and the rules for when files cross, against stand-ins for the clipboard and the helper. Also 115 core, 76 WPF and 22 update checks.
- **Windows to macOS.** Three items were put on the Windows clipboard as Explorer does: a file with a Chinese name, a 64 MiB file, and a folder of 152 files with Chinese and emoji names. When the VM's window came to the front, the guest's pasteboard held their addresses in the hidden share. Command-V in Finder pasted them on the desktop, and all 154 files had the SHA-256 of the originals. The folder the guest was given listed the three items and not a fourth file that lay beside them in Windows.
- **macOS to Windows.** Three items were copied in Finder with Command-C: a file with a Chinese name, a 32 MiB file, and a folder of 23 files with a symbolic link and a `.DS_Store` in it. 1.9 seconds after the VM's window was left they were on the Windows clipboard. All 25 files had the same SHA-256; the link and `.DS_Store` were left out; `slash:name?.txt` and `ends with dot.` arrived as `slash_name_.txt` and `ends with dot`. Explorer's paste verb then copied the 25 files into a folder. macOS asked for no permission; the files were in a folder of the home folder that macOS does not guard.
- **Size and calling off.** A 300 MiB file was not fetched, and the broker's state said why. With uploads limited to 4 MiB/s, fetching 32 MiB took 7.9 seconds. Text copied in Windows while files were being fetched called the fetch off, and nothing of it stayed in the inbox.
- **Text and the older helper.** Text still crossed both ways. Helper 0.2.0 kept its two shared folders with the new broker and was not offered the hidden share.
- **A crash found on the way.** When macOS connects to a share it sends control requests that name no file. The first build of the hidden share did not expect a request without a handle, and the exception ended the broker; Manager started it again. The share now answers such requests, and the SMB library closes a connection whose request throws instead of ending the process (the second change in `manager/third_party/SMBLibrary`). The checks above were made after that.

Not checked: copying in Explorer's own window and pasting there by hand (the Windows clipboard was written and read by a script, and pasted with Explorer's paste verb); cancelling a paste in Finder; files in the folders macOS guards (Desktop, Documents, Downloads), where it asks before the helper may read them; the switch and the progress line in Manager's window on a running VM; files from several folders in one copy on a real guest; the broker updating a 0.2.0 helper to 0.3.0; several macOS users; the frame rate during a fetch; Intel and AMD GPUs; and the release packages.

## Shared folders, 2026-11-25

Guest Tools' shared folders were checked on one machine: Windows 11, RTX 4090 Laptop GPU, a macOS 26.7.2 guest on a copy-on-write overlay of an existing VM, on the runtime built from source.

- **Automated tests.** 275 broker checks, 77 of them new. The library's own SMB client stands in for macOS against the broker's real listener: logon with the right and a wrong password, reading and writing, a read-only share, a junction that leads out of the folder, the upload limit, a changed list of folders, and the time the folders stay served after the helper left. Also 115 core, 72 WPF and 22 update checks.
- **Mounting.** The helper in the guest updated itself from 0.1.0 to 0.2.0 and mounted two folders. They were mounted again without anyone doing anything after the broker was killed and restarted, after the VM was paused for 80 seconds, after a folder was changed from read-only to read-write, and after a shutdown and a new boot with automatic login. A pause of 10 seconds leaves the mounts as they are.
- **Through the guest's loopback.** At first the folders were mounted straight from the host's address, 10.0.2.2. A mount then took 11 to 21 seconds, and the entry "10.0.2.2" that Finder put into its sidebar stayed at "Connecting…" when clicked. Both had one cause: besides the port it is given, macOS tries the standard SMB ports of a server's address, and at 10.0.2.2 those are Windows' own file sharing (seen as connections from QEMU to this PC's port 445 during every mount). The helper now carries the SMB connection through a port on the Mac's own loopback and keeps the mounts out of Finder's sidebar, with links to them in the folder `Weims Shared Folders` of the home folder. Measured after that: a mount takes 0.2 to 0.4 seconds; no connection to port 445 or 139 is made; both folders are back 6 seconds after the broker was killed and 8 seconds after an 80-second pause; a helper that was killed finds its mounts still working when launchd has started it again; a folder unmounted behind its back is mounted again within 11 seconds; a helper that is ended by launchd unmounts first. 256 MiB in each direction had the same SHA-256 on both sides, and the junction that leads out of the folder stayed closed. Against a mount without the relay, 1000 small files took a fifth longer to copy (14.7 s against 11.9 s) and large files went as fast.
- **Content.** A 1 GiB file in each direction and 10,000 small files copied to the share and back had the same SHA-256 on both sides. Chinese, Japanese and emoji names and extended attributes arrived unchanged. Symbolic links are not supported.
- **What must be refused.** Creating, deleting and appending on a read-only share failed. Through a junction that leads out of the shared folder, listing, reading and creating failed, and nothing appeared outside the folder.
- **A broker that is gone.** Listing a mounted folder blocked for 10 seconds and then failed with a timeout; it did not hang.
- **Frame rate while uploading.** Dragging a window in macOS, five runs of ten seconds each, counted on the host's screen: 56.1 frames per second with nothing else going on, 59.2 while macOS wrote to a share under a limit of 12 MiB/s, 57.6 while it wrote without a limit at 41.5 MiB/s. Uploads are therefore not limited unless the user sets a limit. That was measured before the relay. With the relay, in another arrangement of windows: 57.8 with nothing else going on and 59.3 while macOS wrote without a limit at 56.0 MiB/s; a limit of 12 MiB/s gave 11.8 MiB/s.

Not checked: the large-file and 10,000-file checks and the broker-gone timeout with the relay in place (they were run before it), copying and cancelling in Finder (the large files were copied with `cp`), several users logged in to macOS, a shared folder that disappears while it is shared, the shared folder controls of Manager's window on a running VM (the guest was driven through the CLI), Intel and AMD GPUs, and the release packages.

## The upstream-based runtime, 2026-11-24

The source tree builds its runtime from upstream reims-vgpu `c8419476` with `manager/patches/window.patch` (see [Building from source](reproduce.md)). What was checked on the host in the main README:

- **Sources.** `scripts/fetch.ps1` was run into an empty folder against local copies of the two repositories: the patch's checksum matched, it applied to the pinned commit, and reims-util was checked out at the commit upstream names. The fetch from GitHub itself was not repeated in that run.
- **Build.** `scripts/build.ps1` built the library, QEMU 11.1.90 and reims-util in MSYS2 CLANG64 and assembled `build/weims-runtime`. Its programs ran with only Windows' system directory on `PATH`. `manager/package-runtime.py --runtime-only` produced a 94.8 MB runtime ZIP.
- **Automated tests.** 115 core, 66 WPF, 22 update, 198 broker and 27 integration checks, the last two groups against the built QEMU without a guest; and the 117 tests of upstream's window crate with the patch applied, three of them the patch's own.
- **A real guest.** macOS 26.7.2 on a copy-on-write overlay of an existing VM, RTX 4090 Laptop GPU. The VM's boot image was made again at its first start with the same Mac identity, and macOS reached the desktop. Graphics, the free pointer, window resizing, Guest Tools with the clipboard both ways, the install disk hot-plugged into the running guest, sound output, and shutdown from the CLI were exercised; QEMU and the broker had exited 14 and 11 seconds after the two shutdown requests. After a resize the window's client area and the guest's mode were both 1712×1148.

Not checked: Intel and AMD GPUs, creating a VM and installing macOS from recovery on this runtime, relative mouse motion for games, more than one display, sessions longer than a few minutes, the source archive and catalog that `package-runtime.py` writes without `--runtime-only`, the installer, and an update from a released version.

## The Windows port of releases up to 1.2.0-pre.1, 2026-11-12

The publication was prepared on 2026-11-12. Each pinned upstream revision was fetched directly from GitHub in a fresh directory and its checksum-verified patch applied successfully. All 44 changed Reims files, 33 changed QEMU files and 12 changed metal2vulkan files matched the recorded working source endpoints byte for byte (the Reims submodule pointer is excluded by design).

PowerShell scripts passed parser checks. VM initialization and redacted startup dry-run passed without starting or changing the running VM. Cargo metadata with `--locked` passed. The OpenCore helper was tested on a disposable fixture. A separate read-only review found no outstanding critical packaging issue.

The portable publication build script successfully rebuilt the release DLL and QEMU from the freshly fetched and patched source directories. QEMU version and graphics-device help checks passed, including the guest-memory property. This was on the existing Windows toolchain with cached Cargo dependencies, not a clean Windows installation.

The original source implementation passed 22 C API tests, produced a release DLL and QEMU executable, and booted the owner's macOS VM. Its performance measurement is in the main README. These results are distinct from the publication-script checks. Full cross-platform `cargo xtask check`, a new macOS installation, and a second complete boot using only the published setup procedure have not been performed. Third-party tools and guest materials remain prerequisites.

QEMU patch application reports inherited whitespace warnings due to an earlier local WHPX diagnostic file's line-ending normalization. The patch is intentionally kept exact to the tested source. MSYS2 packages are recorded, but not archived or installed at historical versions; byte-for-byte binary reproducibility is not claimed.

The patches, scripts and sources of that port are in the tags of those releases.
