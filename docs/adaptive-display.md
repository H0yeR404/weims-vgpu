# Available in 1.1.0-pre.1

GUI and packaged CLI now include this feature. VM freezes remain a known issue and will be addressed; back up data. Stable 1.0.1 does not include it. The source-build validation record below describes development testing.

# Adaptive display (experimental source build)

The current Manager source accepts startup resolutions from 800–4096 pixels wide and 600–2160 high. The owner has validated 2560×1440 on the host described in the main README. Larger presets are available for testing, not certified on other hardware. This extension is not included in the released Manager 1.0.1 or its runtime downloaded by **Prepare / Retry** (the 1.1.0-pre.1 runtime includes it).

## Build and use

1. Complete the native Windows source build in the main README. Use its pinned Rust toolchain and MSYS2 UCRT64; install the .NET 8 SDK for the Manager.
2. From the repository root in Windows PowerShell 7:

   ```powershell
   $env:WEIMS_MSYS2 = 'C:/msys64'
   pwsh -File manager/build-display.ps1
   pwsh -File manager/test-display.ps1
   pwsh -File manager/build.ps1 -SelfContained
   ./manager/artifacts/Weims-Manager/Weims.Manager.exe --repo C:/src/weims-vgpu
   ```

   Replace the checkout/MSYS2 paths as needed. Stop VMs using `manager-display-runtime` before rebuilding it. The helper applies the input patch followed by the display patch, builds the DLL, and creates `build/manager-display-runtime`. It retains `build/manager-runtime` for rollback. Keep both capability JSON files beside the matching DLL and QEMU executables; the Manager checks the display capability and DLL SHA256.
3. On **Runtime**, select `build/manager-display-runtime/qemu-system-x86_64.exe`. Existing saved runtime settings take precedence over repository auto-detection. Select your UCRT64 `bin` dependency directory and `sources/qemu/pc-bios`, then check and save.
4. Import the VM configuration. Shut down the VM before editing. On **Display & Input**, set the startup resolution, select **Auto fit guest resolution**, disable borderless mode, and save. Free pointer can remain enabled.
5. Start the VM, log in, and drag an edge of the independent Reims window. Release the edge and allow macOS to finish its display change. Maximize/restore also requests a mode change. The startup mode is retained during the initial automatic window fit.

The Manager remains a configuration application; the Reims window displays the VM. No new guest service or kext installation is needed for this extension with the pinned new graphics-lane guest drivers. This work adds neither clipboard nor file sharing.

## Modes and fallback

| Setting / JSON `display_mode` | Behavior |
|---|---|
| Compatibility / `legacy` | Existing backend behavior, with no new debounce or scaling policy. Default for configurations without the field. Works with the published runtime. |
| Auto fit guest resolution / `auto` | After a user resize, coalesce changes for 200 ms, align width to 16 and height to 2, then offer the mode through the existing Reims display protocol. |
| Fixed guest resolution / `fixed` | Keep the configured startup mode and scale the complete image into the window while preserving its aspect ratio. |

Auto/fixed scale the existing image while window and guest sizes differ. Matching-size rendering uses the existing path. Guest RAM sharing, WHPX, graphics protocol and Metal command execution are unchanged. The software does not claim zero overhead while scaling.

Auto serializes requests: the next change waits for the guest's `SET_SCANOUT` acknowledgment. Hidden or undersized windows do not offer new sizes; hiding also pauses the acknowledgment timeout. If a visible guest fails to acknowledge for 10 seconds, the policy requests the last confirmed mode once and disables further adaptation for that run. This is a host request, not a guarantee that WindowServer accepts the restored mode. Restarting re-arms auto mode. It does not repeatedly retry a refused size.

## Logs and rollback

Open **Diagnostics & Logs** and inspect the run's `stderr.log`:

```text
display policy initialized: auto (startup 2560x1440)
resize offered: 1600x1140
resize acknowledged by guest scanout: 1600x1140
```

An acknowledgment confirms the guest changed its scanout dimensions. A differently sized host window or QMP screenshot alone does not prove a guest mode change. `serial.log`, when enabled by the VM configuration, can independently record `ReimsGraphics: display ... scanout ...`. A timeout logs the requested and restore sizes. Preserve both logs if it occurs.

To stop adapting while retaining complete-image scaling, shut down macOS, stop QEMU and choose **Fixed guest resolution**. To return to the previous runtime, first save **Compatibility**, then select the previous QEMU runtime directory. Existing VM disks need no conversion. Do not start another writable instance of the same VM. Closing the Reims window can terminate QEMU; use the macOS shutdown menu before stopping its process.

## Validation on 2026-11-14

- Manager: 59 core contract checks and 14 bilingual WPF interaction checks passed, including 2560×1440 save/preview, capability mismatch rejection, and default compatibility behavior.
- Rust: 9 standalone tests of the exact policy module and 99 window-backend tests passed. The Windows release DLL built successfully. The additive patch was checked against its baseline separately from the existing Windows/input patches.
- A cold boot used a temporary QCOW2 overlay and copied OpenCore/OVMF state, leaving the original VM files unchanged. The final runtime received guest scanout acknowledgments at 3440×1368, 2384×1344, 1600×1140 and 2544×1228. The owner confirmed complete-image following after the requested shrink/enlarge and minimize/restore sequence. These are smoke tests, not extended stability or performance measurements.
- An earlier test build timed out at 1888×1344 and stopped adapting as designed. It had the image-cropping issue subsequently corrected. Source inspection found no size-specific rejection, and the logs do not determine whether the change notification or WindowServer selection was responsible. Do not infer that the scaling correction proves this timeout's cause or that it can never recur.
- The upstream whole-library test harness does not compile on Windows because of Unix-only APIs (`ExitStatusExt`, `clock_gettime`, `mmap`, etc.). `cargo xtask check` also stops compiling its Unix-specific distribution code (`native`, `Permissions::from_mode`). These failures were recorded rather than changing unrelated test infrastructure. Focused tests and an actual Windows VM boot provide the evidence above.

Long-running resize stress, high-DPI/multiple-monitor movement, fixed-mode-only cold boots and other hardware remain to be validated. No GitHub release is implied by this source update.
