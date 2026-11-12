# Manager 1.0 delivery plan

Approved scope: bilingual Windows configuration manager, environment detection and automatic preparation, per-user installer, public v1.0 release. Guest display remains separate. Recovery media and prepared OpenCore/OVMF remain user supplied.

## Implementation

- [x] Add `EnvironmentSetup` in Core: Windows/x64, firmware virtualization, WHPX capability and Vulkan device checks; fixed HTTPS packages with compiled SHA256, bounded downloads, safe ZIP extraction into unique staging, non-overwriting publication, cancellation, then executable validation. Never modify drivers or reboot automatically.
- [x] Add runtime UI with detection report, prepare/cancel/retry, Windows Features and driver help. Preserve working custom paths on failure. Run read-only detection on first launch. Install PowerShell privately when absent; resolve it explicitly during launch.
- [x] Centralize Chinese/English strings for XAML and Core messages, persisted language preference, English default regardless of system language. Set assembly/product version 1.0.0 (UI 1.0).
- [x] Package QEMU/Reims and recursive non-system DLL dependencies; include notices and matching source/build material. Pin runtime ZIP hash in manager. Package .NET self-contained manager with Inno Setup, per-user install and safe uninstall.
- [x] Extend core tests for hash failures, ZIP traversal, cancellation and destination ownership; WPF tests for both languages and setup state. Test runtime outside MSYS2 PATH, installer in isolated location, and relevant integration suite. Review before public upload.
- [ ] Commit source, publish v1.0 with installer, portable ZIP, runtime ZIP, source archives and SHA256SUMS; verify uploaded assets. Explicitly document guest installation and hardware compatibility limits.

## Interfaces

`EnvironmentSetup.Detect(EngineSettings, CancellationToken)` returns a readable localized report. `EnvironmentSetup.Prepare(string dataDirectory, IProgress<string>, CancellationToken)` returns validated EngineSettings without changing preferences. UI saves only the returned settings. `EngineSettings.PowerShell` selects a private executable or PATH fallback. `Text.Get(zh, en)` localizes programmatic messages; WPF resource dictionaries localize static labels. Language changes apply after a controlled app restart, preserving current unsaved fields until the user restarts.

## Constraints

No user VM start/stop during testing. No macOS images, identities or credentials published. System feature changes require an explicit user action and normal Windows UAC. Binary distribution requires matching license/source material; unresolved distribution blockers must be reported instead of shipping incomplete bundles.

## Validation before publication

41 core checks, 13 bilingual WPF checks, 9 real-tool integration checks passed. Packaged runtime loads with only its own directory and Windows System32 on PATH. Hardware detection on the development machine reports WHPX and Vulkan shared-memory support. Official PowerShell ZIP download, SHA256 verification and private execution passed. Installer install/startup/uninstall passed in an isolated directory; preferences retained. Independent review found one post-save cancellation message issue, fixed before release. Full macOS installation and live guest pointer acceptance remain unverified.
