# Current Plan

## Active Work
None. **BadBuilderLinux v2.5.0** was released on 2026-11-08 from `main` at
`5157d95`; the working tree is clean and no feature branch is active.

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
| Merge `audit/full-sweep` and release v2.5.0 | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |

## Blockers
- None for release. Physical USB and Windows runtime behaviour remain manual
  checks and are the only outstanding validation.

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
2. Start from a user request or a new audit; update this file and `HANDOFF.md`
   when work begins.
