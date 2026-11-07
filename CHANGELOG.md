# Changelog

## [2.4.1](https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.4.1) — 2026-11-07

### Fixed

- Linux disk inventory now protects the caller's actual home directory and resolves symlinked workspace/cache paths to their backing mounts. The privileged helper resolves home from the caller UID instead of root's environment.
- Windows drive-letter discovery revalidates the selected disk identity after formatting and accepts only one FAT32 `BADUPDATE` volume, so a replacement device reusing the same disk index cannot become the copy target.
- Install preflight rejects planned files larger than `uint.MaxValue` and estimates FAT32 cluster/directory allocation, including empty directories, using the formatter's actual layout. The estimate is correct across 1–16 GiB targets.
- FreeMyXe now installs with the original ABadAvatar archive when it has no existing `BadUpdatePayload/default.xex`.
- Peer Pressure validates complete HDD destinations and every copied entry against FATX limits (42-character names and 240-character paths).
- Automatic homebrew launch validates the selected executable and updates `launch.ini` in staging before the installation plan is built. Malformed UTF-16 fails before disk erasure.
- Failed Linux cleanup now persists the pending target and exposes **Retry USB cleanup** from the main menu. A missing `/run` lease is treated as complete only when the mount is gone.
- Failed transfers and invalid cached archives now use the same verified local-archive fallback as missing metadata. A linked deadline covers response headers, streamed bodies, and GitHub metadata.
- Install-plan collision validation is linear instead of comparing every file with every other file.
- Removed unused multi-selection and progress-display UI helpers.

### Validation

- 135 tests pass, including new regressions for 4 GiB files, FAT32 allocation from 1–16 GiB, symlinked protected paths, FATX limits, launch.ini preflight, download fallback/timeouts, and pending cleanup storage.
- Linux and Windows x64 self-contained single-file publishes succeed; the Linux binary passes `--version`.
- The pinned XeUnshackle Max archive check passes; NuGet reports no known vulnerable dependencies.

## [2.4.0](https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.4.0) — 2026-11-07

### Added

- Peer Pressure as a separate exploit choice, with the official v1.0.477 installer pinned to its release and SHA-256. The USB includes Rock Band Blitz, the upstream license, and bundled XeLL.
- A separate Peer Pressure homebrew path for Aurora, XeXMenu, built-in utilities, and custom archives. Apps are packaged as HDD installation extras while the upstream extras catalog is preserved.
- **Set default dashboard** through Peer Pressure's `DashXex` setting, including preflight validation of the selected executable. Select the dashboard under **Install Extras** on the console to apply it.
- Regression coverage for installer selection and layout, app staging, dashboard paths, catalog preservation, and INI injection. An optional real-archive test checks the complete USB plan without formatting a disk.

### Changed

- Peer Pressure hides the separate bootstrap menu and uses its own dashboard configuration. BadUpdate and ABadAvatar retain their existing bootstrap and USB homebrew paths.
- Reworked the README around downloads, workflow selection, Peer Pressure installation, Aurora setup, recovery, and development checks.

### Validation and limits

- The 68-test suite and real-archive integration check pass locally; Linux and Windows self-contained builds have been checked.
- Aurora default-dashboard startup was confirmed on one physical setup. Other console and HDD combinations still require testing.
- Peer Pressure requires a compatible console and HDD; Corona and Winchester are unsupported. DashLaunch plugins are unsupported. The dashboard picker accepts executable utilities too, so choose a dashboard rather than a maintenance tool.
- USB preparation erases the selected disk. Console softmod installation modifies NAND/HDD data; keep the official installer's system backup somewhere safe.

## [2.3.1](.github/release-notes/v2.3.1.md) — 2026-10-22

- Fixed preflight failures when XeUnshackle or XeUnshackle Max adds `BadUpdatePayload/default.xex` to an exploit archive that does not already contain it.
- Preserved strict collision checks and added regression tests for optional overlays.

## [2.3.0](.github/release-notes/v2.3.0.md) — 2026-10-22

- Added the pinned XeUnshackle Max v1.0.0 bootstrap and default USB homebrew launching.
- Omitted bundled JRPC2/Xbdm remote-access binaries from automatic installation.

## [2.2.1](.github/release-notes/v2.2.1.md) — 2026-10-22

- Integrated upstream changes through September 14, 2026, including Windows administrator elevation, while retaining Linux's scoped sudo helper and disk safeguards.

## [2.2.0](https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.2.0) — 2026-09-28

- Added the pinned ABadAvatar 1.3 Beta option alongside the original Public Beta.
- Included submenu Back navigation, dependency updates, and archive-layout validation.
