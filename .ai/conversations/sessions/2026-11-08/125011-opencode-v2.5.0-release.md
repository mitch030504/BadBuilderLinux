# v2.5.0 release — 2026-11-08

**Agent:** OpenCode (DeepSeek V4.1 Flash)
**Outcome:** Published `v2.5.0` from `main` at `5157d95`.

## What was released

The full audit remediation (9 commits) plus release preparation:

- `f6345e1` — bump to 2.5.0, release notes, changelog, README upgrade notes
- `de72f02` — make three Linux-only tests platform-aware
- `5157d95` — retry the pinned-archive check so a CDN timeout cannot fail a release

## Release

- URL: https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.5.0
- Tag `v2.5.0` → `5157d957068e61e475c03f6df0a831498bb27874`
- Assets: `BadBuilder-linux-x64` (78,447,335 B), `BadBuilder-win-x64.exe`
  (78,598,241 B), `SHA256SUMS`
- `SHA256SUMS` was compared against the GitHub asset digests and matches:
  - linux `42f991173831c2b19adebb5d006493a74da9d8afa28cbb6b693a30f2f98f6bc6`
  - windows `be0247f2c8dbde8490bfbc3b3cc63a0f6a50b87bd528d4ab01ace3858b6285b6`

## Failures encountered and fixed

1. **Windows unit tests failed** on the first release run. Three tests assumed
   Linux path semantics: `/dev` paths are not fully qualified on Windows,
   `/etc/passwd` homes start with `/`, and device paths are compared
   case-insensitively there. Fixed by guarding the parser tests, matching
   Unix-absolute homes by prefix, and splitting the case-change assertion.
2. **Linux build failed** on the pinned XeUnshackle Max check because GitHub's
   release CDN timed out after 60s. The script now retries three times with
   backoff, uses a 120s timeout, and rejects a non-HTTPS redirect target.

## Verification

- 202 tests pass locally and on both CI platforms.
- Both self-contained single-file publishes succeed; the Linux binary reports
  `2.5.0+<sha>`.
- The release workflow completed successfully and the assets are published.

## Remaining manual work

- Physical USB validation on Linux and Windows.
- The sudoers rule and root-owned install path on a real machine.
