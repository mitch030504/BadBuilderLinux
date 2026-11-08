# Current Plan

## Active Work
None. **BadBuilderLinux v2.4.1** was released on 2026-11-07; the repository is clean and no feature branch is active.

## Task Status
| Task | Status | Agent | Date |
|------|--------|-------|------|
| Fix 12 repository-audit findings | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-07 |
| Add regression coverage and verify publishes | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-07 |
| Publish v2.4.1 release | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-07 |
| Bootstrap `.ai/` agent handoff context | done | OpenCode (DeepSeek V4.1 Flash) | 2026-11-08 |

## Blockers
- None.

## Key Decisions
- FAT32 capacity preflight mirrors the pinned DiscUtils formatter, including uncapped allocation geometry and directory/empty-directory clusters → `.ai/conversations/decisions/2026-11-08-v2.4.1-safety-hardening.md`
- Failed Linux cleanup is persisted and retried through the existing authenticated helper lease; stale leases are accepted only when the mount is gone.
- Failed downloads and invalid cached archives use the verified local-archive fallback; a linked deadline covers headers, body reads, and metadata.
- Destructive work remains gated by complete preflight, exact device-path confirmation, identity revalidation, and post-format verification.

## Next
No queued work. Start from a user request or a new audit; update this file and `HANDOFF.md` when work begins. Use the release process in `PROJECT.md` for any new version.
