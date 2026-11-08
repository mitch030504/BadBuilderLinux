# Agent Handoff
Last updated: 2026-11-08 11:04 CET by OpenCode (DeepSeek V4.1 Flash)

## Active Work
- None. `main` is clean at `260250e`; v2.4.1 is released.

## Recent Completions
- [2026-11-08 OpenCode] Bootstrapped `.ai/` handoff context, project/path/plan docs, decision record, and always-active snippets in agent config files. #setup
- [2026-11-07 OpenCode] Fixed all 12 audit findings across Linux disk protection, Windows identity, FAT32 limits/allocation, FreeMyXe, Peer Pressure FATX, launch.ini preflight, cleanup recovery, download fallback/timeouts, collision performance, and UI cleanup. Added regressions and released v2.4.1. #bugfix #deploy

## Pending Decisions
- None.

## Key Context
- Current version: `2.4.1`; tag `v2.4.1`; release: https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.4.1
- Current `main` HEAD: `260250e` (`test: keep FAT32 layout coverage within Windows runner capacity`).
- Test suite: **135 passed**; Linux and Windows self-contained single-file publishes and the release workflow are green.
- Release assets and `SHA256SUMS` were verified against GitHub asset digests.
- Automated tests do **not** cover physical USB drives, Windows runtime behavior, or privileged cleanup execution; treat those as manual checks.
- Linux must run as the desktop user, never root; the app requests sudo only for the scoped disk helper.
- Destructive flow: preflight → exact device-path confirmation → identity revalidation → format → copy → flush/unmount. Do not move launch.ini or other predictable validation after formatting.
- Release process: bump `VersionPrefix`, add `.github/release-notes/vX.Y.Z.md`, update `CHANGELOG.md`/`README.md`, push to `main` or dispatch `release.yml`.
