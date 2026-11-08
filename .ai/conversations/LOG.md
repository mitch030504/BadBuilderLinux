# Agent Log

---
## 2026-11-07 20:50 CET — OpenCode (DeepSeek V4.1 Flash) #bugfix #deploy
**Task:** Fix the 12 findings from the full repository audit and publish a release.
**Outcome:** Fixed disk-safety, FAT32 planning, FreeMyXe, Peer Pressure FATX, launch.ini preflight, cleanup recovery, download fallback/timeout, collision-performance, and dead-UI issues. Added regression coverage, updated docs, and published v2.4.1.
**Files:** `BadBuilder/`, `BadBuilder.Tests/`, `README.md`, `CHANGELOG.md`, `.github/release-notes/v2.4.1.md`, `BadBuilder/BadBuilder.csproj`
**Next:** No active work; start from a new user request or audit.

---
## 2026-11-08 11:04 CET — OpenCode (DeepSeek V4.1 Flash) #setup
**Task:** Bootstrap the shared agent-handoff context.
**Outcome:** Created `.ai/PROJECT.md`, `PATHS.md`, `PLAN.md`, `HANDOFF.md`, `LOG.md`, a v2.4.1 decision record, and a bootstrap session file. Injected the always-active handoff snippet into `AGENTS.md`, `CLAUDE.md`, `codex.md`, and `.github/copilot-instructions.md`.
**Files:** `.ai/`, `AGENTS.md`, `CLAUDE.md`, `codex.md`, `.github/copilot-instructions.md`
**Next:** Future agents should read the four `.ai/` startup files before work and write back after file changes or decisions.

---
## 2026-11-08 12:03 CET — OpenCode (DeepSeek V4.1 Flash) #audit #security #bugfix
**Task:** Full repository audit (10 parallel subagents) followed by an eight-phase remediation sweep.
**Outcome:** Pinned all 12 artifacts and removed the server-digest trust anchor; confined the download manifest; added `O_EXCL` handle-bound formatting and GPT erasure; tightened Linux eligibility, protected paths, and multi-device protection; added Windows volume-GUID locking, sector-size rejection, and flush-on-finalize; made copy success verifiable; hardened the sudo helper and made stale leases recoverable; fixed FAT32/launch.ini/capacity correctness; pinned CI actions, added lock files, release guards, and provenance. Test suite 135 → 202, all passing.
**Files:** `BadBuilder/`, `BadBuilder.Tests/`, `.github/workflows/`, `scripts/`, `README.md`, `CHANGELOG.md`, `SECURITY.md`, `global.json`, `NuGet.config`, `packages.lock.json`
**Branch:** `audit/full-sweep` (8 commits, not merged)
**Next:** Hardware validation on a disposable USB stick; decide whether this ships as 2.5.0 because the Linux install workflow changed.
