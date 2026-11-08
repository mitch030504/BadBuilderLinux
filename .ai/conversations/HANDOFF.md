# Agent Handoff
Last updated: 2026-11-08 12:50 CET by OpenCode (DeepSeek V4.1 Flash)

## Active Work
- None. **BadBuilderLinux v2.5.0** was released on 2026-11-08 from `main` at
  `5157d95`; the working tree is clean and no feature branch is active.

## Recent Completions
- [2026-11-08 OpenCode] Released v2.5.0: full audit remediation, root-owned
  Linux install, artifact pinning, exclusive-handle formatting, copy
  verification, helper hardening, CI pinning. #release #security #bugfix
- [2026-11-08 OpenCode] Full audit (10 subagents) and eight-phase remediation. #audit
- [2026-11-08 OpenCode] Bootstrapped `.ai/` handoff context. #setup

## Pending Decisions
- **Hardware validation.** Physical USB behaviour, Windows runtime behaviour,
  and the sudoers boundary are still manual checks. Run the hardware checklist
  before relying on v2.5.0 for a real console.

## Key Context
- Current version: `2.5.0`; tag `v2.5.0`; release:
  https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.5.0
- Tag `v2.5.0` points at `5157d95`. `main` is at the same commit.
- Test suite: **202 passed** on Linux and Windows CI.
- Release assets verified: `SHA256SUMS` matches the GitHub asset digests
  (linux `42f99117…`, windows `be0247f2…`).
- **Breaking change:** Linux users must install the binary root-owned and grant
  sudo to that path. The helper refuses to run from a caller-writable
  directory. See README "Sudo rule" and "Upgrading from 2.4.x".
- `wipefs` and `blockdev` are no longer required. Required commands: `sudo`,
  `lsblk`, `findmnt`, `mount`, `umount`, `sync`; optional `udevadm`.
- Every built-in artifact has an exact tag, literal SHA-256, and size. Updating
  an upstream artifact now requires editing the catalog deliberately.
- The destructive sequence lives in `Application/InstallCoordinator.cs`.
- Deliberately unchanged: MBR alignment at LBA 63 (Xbox compatibility), LVM
  multi-PV protection, and quoting of `launch.ini` values.
- Release process: bump `VersionPrefix`, add `.github/release-notes/vX.Y.Z.md`,
  update `CHANGELOG.md`/`README.md`, merge to `main`, then either push the
  csproj change or dispatch `release.yml` from `main`. Releases are created as
  drafts and published after upload.
- The pinned-archive check retries three times; a CDN timeout no longer fails
  a release.
