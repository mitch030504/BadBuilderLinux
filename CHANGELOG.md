# Changelog

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
