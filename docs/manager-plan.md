# Weims Manager — design and implementation plan

Approved 2026-11-12: Windows WPF configuration utility; independent Reims display window; recovery images only, no ISO. Initial delivery prioritizes existing-VM import and validated launch configuration. Recovery preparation may be implemented but must retain explicit validation limits.

## Design

WPF / .NET 8, no third-party UI dependencies. macOS Settings-inspired pale sidebar, grouped white cards, blue selected state, restrained vector graphics, native keyboard and accessibility semantics. Initial Chinese interface; superseded by the bilingual 1.0 plan with English default. Segoe UI / Microsoft YaHei fallback, no redistributed Apple fonts.

The ui-ux-pro-max design-system searches returned a marketing landing-page pattern twice. That pattern is unsuitable and is not adopted. Minimalism, readable contrast, spacing, accessible controls and the WPF binding/async guidance are useful; the settings layout follows the user requirement and general desktop defaults.

## Interfaces

- Existing state/config.json is preserved with unknown fields. Import registers a path, never copies or rewrites a disk automatically.
- QEMU arguments follow the published start.ps1 baseline. A typed manager adapter adds runtime paths, recovery attachment and display environment settings. Secrets remain process-only, command previews redacted.
- QMP: bounded requests, response IDs, skip events, verify VM name before power commands.
- Writes require stopped VM. Disk creation/conversion uses qemu-img with argument lists and async output, never shell string evaluation. No source disk deletion.
- Unsupported mouse settings are disabled unless a separately built backend supports them.

## Tasks and validation

- [x] Core: JSON round-trip, input/path validation, safe argument generation, QMP protocol and runtime identity. 20 contract checks passed.
- [x] WPF: library, overview, hardware, display/input, recovery wizard, runtime setup, logs; visible busy/error states. 7 WPF checks and render inspection passed. Native automation hit an app-identity tool error.
- [x] Recovery: only DMG/raw IMG; explicit firmware prerequisites, isolated staging, new qcow2, attach/detach. Real qemu-img tested with synthetic recovery-shaped fixture, not full installation.
- [x] Display/input: resolution bounds (width <=2048), borderless; optional hash-bound free-pointer backend. 98 Rust window tests passed.
- [x] Review and package: reviewer findings fixed, 9 real-tool checks, Release self-contained executable, docs. Initial checks preceded publication; see manager-1.0-plan.md for the release update.

The launch adapter uses typed C# argument generation plus a short PowerShell helper for detached stdout/stderr file handles. Capturing the helper's output caused a real inherited-pipe hang; it is now launched without captured pipes. QMP uses IDs, bounded reads and VM-name verification. A per-VM lock covers runtime publication; publication failure terminates only the newly started child.

## Limits

Do not stop or modify the user's running VM for testing. No ISO, saved state, live snapshots, audio/USB passthrough or dynamic guest resolution claims. Recovery installation requires authorized local boot files and a network connection.
