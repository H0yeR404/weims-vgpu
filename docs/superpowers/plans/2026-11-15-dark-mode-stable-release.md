# Live theme switching and 1.1.0 release

**Goal:** Release the pre.2 functionality as stable 1.1.0 with immediately switchable, persisted light/dark appearance.

**Architecture:** WPF dynamic palette resources, independent of the language dictionary. A sidebar selector changes resources without recreating windows or view models. Preserve existing VM and runtime data. Keep the pinned pre.1 graphics runtime.

**Scope:** English remains the default language; light remains the default appearance. Manager and CLI ship together. Existing freeze and GPU compatibility limitations remain documented. No VM startup or disk changes are required.

## Implementation and checks

- [x] Add light/dark dictionaries and theme-aware input controls. Preserve editable ComboBox behavior, focus feedback and high contrast support.
- [x] Add sidebar selector, bilingual labels and `theme.txt` preference. Keep language resources independent and retain unsaved form input.
- [x] Extend the actual WPF harness: switch both ways on the same window, check persistence, language independence, popup/editor readability, wizard appearance and minimum-size layout. Capture both palettes.
- [x] Set Manager/CLI/installer to 1.1.0, use the stable installer identity, reuse existing preview settings or an updater-selected custom profile; fresh installs use the stable profile.
- [x] Update English/Chinese/Japanese entry points and release notes; retain historical prerelease notes and pinned runtime links.
- [x] Run WPF, core, updater and handoff checks; request an independent code review and resolve important findings.
- [x] Build self-contained GUI/CLI ZIP and Inno Setup installer with existing icon and license notices. Check versions, package contents and SHA256.
- [ ] Commit/push, publish v1.1.0 as a regular Latest release, upload artifacts and verify release metadata/digests and pre.2 update discovery.

## Validation commands

Use `D:/Coding/qemu_macos26/tools/dotnet-sdk/dotnet.exe run --project manager/Weims.UiTests -c Release -- <isolated evidence directory>` for UI checks; run `Weims.Core.Tests` and `Weims.UpdateTests` similarly. Run `manager/test-update-handoff.ps1` with its existing test fixtures. Build with `manager/build.ps1 -SelfContained`, then `tools/inno-setup/compiler/ISCC.exe /Qp /DPublishDir=<published directory> manager/installer.iss`.

The user's request authorizes implementation and publication. This plan does not introduce an additional approval step.

## Validation record

71 core checks, 45 WPF checks, 22 updater checks (including live GitHub), six mocked installer-handoff checks and the actual-startup English/dark-preference probe passed. Both palettes were inspected in rendered screenshots. Desktop automation could not bind the portable window because its application identity was attributed to the installed Preview instance; no automated desktop click success is claimed. Independent review found no release blocker. Installer and ZIP include GUI/CLI, notices and three-language documentation; hashes and ZIP contents were checked. Real in-place installation remains unvalidated.
