# Current Plan

## Active Work
Full audit remediation is complete on branch `audit/full-sweep` (8 commits on
top of `main` at `3bc4d0a`). The branch is **not merged** and **not released**.

## Task Status
| Task | Status | Agent | Date |
|------|--------|-------|------|
| Audit every subsystem with 10 parallel subagents | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 1 — artifact pinning and manifest trust | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 2 — install seams and characterization tests | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 3 — disk eligibility, protected paths, O_EXCL, Windows locking | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 4 — copy integrity and mount verification | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 5 — helper hardening and lease recovery | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 6 — FAT32/launch.ini/capacity correctness | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 7 — CI pinning, lock files, release guards | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Phase 8 — README and CHANGELOG | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |
| Hardware validation on a disposable USB | pending | — | — |
| Merge `audit/full-sweep` and release | pending | — | — |

## Blockers
- Physical USB and Windows runtime behaviour cannot be verified in this
  environment. The disk, copy, and helper changes are code-verified only.
- The Linux install workflow changed (root-owned binary + sudoers rule). That
  needs a deliberate release decision.

## Key Decisions
- Trust comes only from the compiled artifact catalog; the GitHub asset digest
  is transport integrity only → `.ai/conversations/decisions/2026-11-08-trust-anchors-and-disk-identity.md`
- Disk eligibility is fail-closed; the target is opened with `O_EXCL` and
  formatted through the verified handle.
- The privileged helper refuses to run from a caller-writable location and
  reads the cleanup token from stdin.
- Copy success requires a mount-point check before and after, a flush, and a
  length/byte verification.
- The destructive sequence is testable through `InstallCoordinator` injections.

## Next
1. Run the hardware validation checklist on a disposable USB stick on Linux and
   Windows.
2. Review the branch, then merge to `main`.
3. Decide the version: the Linux install change is breaking, so 2.5.0 is the
   likely choice. Add `.github/release-notes/v2.5.0.md` and bump
   `VersionPrefix`.
