# Key Paths

## Application Code
- `BadBuilder/Application/` — menus, configuration flows, install orchestration (`InstallCoordinator`), cancellation gate, version display
- `BadBuilder/Configuration/` — artifact catalog, builder configuration, source/operation/layout models
- `BadBuilder/Services/` — downloads, archives, install planning, launch.ini, Peer Pressure extras, path/FAT helpers, pending cleanup store
- `BadBuilder/Services/Disks/` — disk inventory/identity, exclusive device handle, FAT32 formatter, Linux backend/helper/protected paths, Windows backend
- `BadBuilder/UI/` — Spectre.Console controls, theme, prompts, `IUserInterface`
- `BadBuilder/Program.cs` — entry point and `--disk-helper` dispatch

## Configuration
- `BadBuilder/BadBuilder.csproj` — target frameworks, pinned packages, `VersionPrefix`, publish settings
- `BadBuilder/app.manifest` — Windows administrator elevation manifest
- `BadBuilder.slnx` — solution projects
- `global.json` — pinned SDK feature band
- `NuGet.config` — cleared package sources, nuget.org only
- `BadBuilder/packages.lock.json`, `BadBuilder.Tests/packages.lock.json` — locked dependency graph
- `.github/workflows/ci.yml` — build/test/publish matrix
- `.github/workflows/release.yml` — version/tag detection and GitHub release publication
- `scripts/verify_xeunshackle_max_release.py` — pinned upstream ZIP verification

## Tests
- `BadBuilder.Tests/` — xUnit suite (`202` tests at v2.5.0; `135` at v2.4.1)
- `BadBuilder.Tests/InstallCoordinatorTests.cs` — destructive sequence, confirmation, cancellation, cleanup handling
- `BadBuilder.Tests/InstallExecutionTests.cs` — mount-point checks, source-size changes, exact-byte writes
- `BadBuilder.Tests/DiskFormatterTests.cs` — GPT erasure, FAT32 geometry boundaries, exclusive open
- `BadBuilder.Tests/ProcessRunnerTests.cs` — executable validation, timeouts, stdin, no shell
- `BadBuilder.Tests/InstallPlanSafetyTests.cs` — FAT32 limits, allocation, collision checks
- `BadBuilder.Tests/LinuxDiskInventoryTests.cs` — eligibility, protected disks, multi-device UUIDs, home/symlink behavior, helper parsing
- `BadBuilder.Tests/WindowsDiskBackendTests.cs` — post-format identity/volume validation, volume path normalization
- `BadBuilder.Tests/DownloadHardeningTests.cs` — local fallback, timeouts, cancellation
- `BadBuilder.Tests/LaunchPreflightTests.cs` — staged launch.ini/entry-point validation and injection rejection
- `BadBuilder.Tests/PendingCleanupTests.cs` — persisted cleanup recovery
- `BadBuilder.Tests/PeerPressureFatxTests.cs` — FATX limits
- `BadBuilder.Tests/LinuxLoopIntegrationTests.cs` — optional root-only loop-device test
- `BadBuilder.Tests/LiveSourceSmokeTests.cs` — optional `BADBUILDER_LIVE_SMOKE=1` check

## Reference Documents (current)
### Project Overview and Operations
- `README.md` — user workflows, safety, build/test/release instructions
- `CHANGELOG.md` — release history and validation notes
- `SECURITY.md` — private vulnerability reporting and scope
- `.github/release-notes/v2.5.0.md` — current published release notes

### CI/CD and Verification
- `.github/workflows/ci.yml` — restore, dependency audit, pinned archive check, build, tests, publish, smoke
- `.github/workflows/release.yml` — release preparation, both-platform build, asset assembly, GitHub release
- `.github/dependabot.yml` — weekly NuGet and Actions updates
- `.github/CODEOWNERS` — owner review for workflows and source
- `scripts/verify_xeunshackle_max_release.py` — XeUnshackle Max v1.0.0 SHA-256/ZIP layout check

### Agent & Project Config
- `AGENTS.md` — multi-agent handoff snippet
- `CLAUDE.md` — Claude handoff snippet
- `codex.md` — Codex handoff snippet
- `.github/copilot-instructions.md` — Copilot handoff snippet
- `.ai/` — cross-agent context bridge

## Feature Specs / Plans
No formal `specs/`, `rfcs/`, `adrs/`, or `docs/` directory is present. Work is tracked through user requests, repository audits, GitHub issues/PRs, and release notes. Each release has one notes file under `.github/release-notes/`.

## Decisions
- `.ai/conversations/decisions/2026-11-08-v2.4.1-safety-hardening.md` — v2.4.1 FAT32, cleanup, and download decisions
- `.ai/conversations/decisions/2026-11-08-trust-anchors-and-disk-identity.md` — artifact trust, manifest authority, disk eligibility, helper boundary, copy proof, and test seams

## Historical Release Notes
Reference only; not authoritative for current behavior:
- `.github/release-notes/v2.4.1.md`
- `.github/release-notes/v2.2.1.md`
- `.github/release-notes/v2.3.0.md`
- `.github/release-notes/v2.3.1.md`
- `.github/release-notes/v2.4.0.md`

## Agent Handoff
- `.ai/PROJECT.md` — stable project context
- `.ai/PATHS.md` — this file
- `.ai/PLAN.md` — active work and task status
- `.ai/conversations/HANDOFF.md` — rolling agent state
- `.ai/conversations/LOG.md` — append-only activity log
- `.ai/conversations/sessions/` — per-session records
- `.ai/conversations/decisions/` — significant design decisions
