---
agent: OpenCode (DeepSeek V4.1 Flash)
started: 2026-11-08 11:04 CET
task: Bootstrap shared agent-handoff context
tags: [#setup, #docs]
---

## Summary
Bootstrapped the `.ai/` shared context for BadBuilderLinux after the v2.4.1 release. The project is a .NET 10 console application with xUnit coverage, Linux/Windows disk backends, verified downloads, and a GitHub Actions release workflow. No prior handoff files or agent config files existed.

## Files Created/Modified
- `.ai/PROJECT.md` — stack, architecture, conventions, run/deploy instructions
- `.ai/PATHS.md` — application, test, config, release, and documentation map
- `.ai/PLAN.md` — completed v2.4.1 work and current no-active-work state
- `.ai/conversations/HANDOFF.md` — rolling state for the next agent
- `.ai/conversations/LOG.md` — initial activity entries
- `.ai/conversations/decisions/2026-11-08-v2.4.1-safety-hardening.md` — safety design record
- `.ai/conversations/sessions/2026-11-08/110417-opencode-agent-handoff-bootstrap.md` — this file
- `AGENTS.md`, `CLAUDE.md`, `codex.md`, `.github/copilot-instructions.md` — always-active handoff snippet

## Decisions Made
- Place `.ai/` at the repository root.
- Document v2.4.1 as the current released baseline (`main` at `260250e`, tag `v2.4.1`).
- Record the key safety decisions from the audit-fix release in a decision file rather than duplicating them across PLAN/HANDOFF.
- Inject the handoff snippet into the three always-required root configs plus Copilot because `.github/` exists. No `.cursor`, `.windsurf`, `.gemini`, or `.opencode` configs were present.

## Issues/Blockers Found
- None.
- Automated coverage does not include physical USB drives, Windows runtime behavior, or privileged cleanup execution.

## What's Next
- Future agents should read `.ai/PROJECT.md`, `.ai/PATHS.md`, `.ai/PLAN.md`, and `.ai/conversations/HANDOFF.md` before work.
- Start new work by updating `PLAN.md`/`HANDOFF.md`; use the release process in `PROJECT.md` for version bumps.
- If project structure or important docs change, update `PATHS.md`.

## References
- `README.md`, `CHANGELOG.md`, `.github/release-notes/v2.4.1.md`
- `BadBuilder/BadBuilder.csproj`, `.github/workflows/ci.yml`, `.github/workflows/release.yml`
- Git history through `260250e`; release https://github.com/mitch030504/BadBuilderLinux/releases/tag/v2.4.1
- Agent-handoff skill templates and inject snippet
