# Agent Handoff
Last updated: 2026-11-08 12:03 CET by OpenCode (DeepSeek V4.1 Flash)

## Active Work
- Branch `audit/full-sweep`, 8 commits on top of `main` at `3bc4d0a`. **Not merged.**
- The branch contains the full audit remediation described in
  `.ai/conversations/decisions/2026-11-08-trust-anchors-and-disk-identity.md`
  and `.ai/conversations/sessions/2026-11-08/120305-opencode-full-audit-remediation.md`.

## Recent Completions
- [2026-11-08 OpenCode] Full audit (10 subagents) and remediation sweep. 202 tests pass, warning-free build, linux-x64 single-file publish verified. #audit #security #bugfix
- [2026-11-08 OpenCode] Bootstrapped `.ai/` handoff context. #setup
- [2026-11-07 OpenCode] Fixed all 12 audit findings and released v2.4.1. #bugfix #deploy

## Pending Decisions
- **Release version.** The Linux install workflow changed: the binary must be
  installed root-owned and sudo must be granted to that path. That is a breaking
  change, so this likely ships as 2.5.0 rather than 2.4.2.
- **Hardware validation.** Phases 3–5 change disk behaviour that cannot be
  exercised here. A disposable USB stick is required before release.

## Key Context
- `main` is at `3bc4d0a`; `audit/full-sweep` is 8 commits ahead.
- Test suite: **202 passed** (was 135). `dotnet restore --locked-mode` works.
- Linux now requires a root-owned install and a sudoers rule; see README
  "Sudo rule". Running the downloaded binary from `~/Downloads` will make the
  helper refuse to run, by design.
- `wipefs` and `blockdev` are no longer required. Required commands are
  `sudo`, `lsblk`, `findmnt`, `mount`, `umount`, `sync`, optional `udevadm`.
- Every built-in artifact has an exact tag, literal SHA-256, and size. Updating
  an upstream artifact now requires editing the catalog deliberately.
- The destructive sequence lives in `Application/InstallCoordinator.cs`, not
  `BuilderApp`. Add safety tests there, not against the static UI.
- Deliberately unchanged: MBR alignment at LBA 63 (Xbox compatibility), LVM
  multi-PV protection, and quoting of `launch.ini` values.
- Release process: bump `VersionPrefix`, add `.github/release-notes/vX.Y.Z.md`,
  update `CHANGELOG.md`/`README.md`, merge to `main` or dispatch `release.yml`
  from `main`. Releases are created as drafts and published after upload.
