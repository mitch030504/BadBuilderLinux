# Full repository audit and remediation — 2026-11-08

**Agent:** OpenCode (DeepSeek V4.1 Flash)
**Branch:** `audit/full-sweep`
**Base:** `main` at `3bc4d0a`
**Scope:** 10 parallel read-only audits covering every subsystem, followed by an
eight-phase remediation sweep.

## Starting point

Ten subagents audited: Linux privilege path, Windows disk backend, downloads,
archive/path/install, app orchestration, config/catalog/process runner,
launch.ini/Peer Pressure/cleanup, test suite, CI/release, and docs/dependencies.
Findings were deduplicated and the load-bearing claims were verified by hand.

## What changed

Eight commits on `audit/full-sweep`:

| Commit | Area |
|---|---|
| `b6f6d0f` | Artifact pinning and manifest trust |
| `30968f3` | `InstallCoordinator` + injectable seams |
| `1dd4b7e` | Disk eligibility, protected paths, `O_EXCL`, Windows locking |
| `d27433e` | Copy integrity, mount checks, flush/verify |
| `7eec910` | Helper hardening, token on stdin, stale-lease recovery |
| `b72abd5` | FAT32/launch.ini/capacity correctness |
| `75de842` | CI pinning, lock files, release guards, provenance |
| `a90f7aa` | README/CHANGELOG |

## Verification

- `dotnet build -c Release` — 0 warnings, 0 errors.
- `dotnet test -c Release` — **202 passed** (was 135).
- `dotnet restore --locked-mode` succeeds with the new lock files.
- `dotnet publish -r linux-x64 --self-contained` succeeds; the binary prints
  `2.4.1+<sha>`.
- `--disk-helper` without root prints a JSON error and exits 4.

## Not verified

- Physical USB behaviour on either platform.
- Windows runtime behaviour: WMI associations, `MSFT_Partition.AccessPaths`,
  `FSCTL_LOCK_VOLUME`, `FlushFileBuffers`, `GetFileSizeEx`.
- The sudoers rule and the root-owned install path on a real machine.
- `statx`, `statvfs`, `open(O_EXCL)`, `BLKGETSIZE64`, and `BLKRRPART` were
  compiled and the error paths were tested, but the success paths need a real
  block device.
- Upstream artifact hashes were recomputed on 2026-11-08 and matched GitHub's
  reported digests; they will drift when upstream publishes new releases.

## Deliberately not changed

- MBR partition alignment stays at LBA 63. Moving to 1 MiB alignment could
  break Xbox 360 compatibility and needs hardware validation.
- LVM volumes spanning multiple physical volumes are not fully protected: the
  kernel does not expose a VG UUID through `lsblk`, so only the member in the
  mount chain is protected. btrfs and mdraid are covered by UUID.
- `launch.ini` values are not quoted. The parser's escaping rules are not
  documented upstream, so the code rejects characters that would need quoting
  instead of guessing.

## Next

- Run the hardware validation checklist on a disposable USB stick.
- Decide whether to release this as 2.5.0 (breaking: root-owned install and
  sudoers rule are now required on Linux).
