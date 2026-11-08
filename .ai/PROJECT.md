# Project: BadBuilderLinux

## Overview
BadBuilderLinux is a Linux-first fork of Pdawg-bytes/BadBuilder. It prepares an Xbox 360 USB drive by downloading and verifying exploit, bootstrap, homebrew, and dashboard artifacts; extracting and validating their layout; planning the FAT32 installation; formatting the selected USB; and copying the prepared files. It supports BadUpdate, ABadAvatar, Peer Pressure, and official dashboard-update workflows, including automatic homebrew startup through `launch.ini`.

## Tech Stack
- **Language:** C# / .NET 10 (`net10.0`)
- **Application:** Console app using Spectre.Console 0.57.2
- **Disk/filesystem:** LTRData.DiscUtils 1.0.89 (MBR + FAT32), SharpCompress 0.50.4 (archives)
- **Windows integration:** System.Management 10.0.12 (WMI)
- **Tests:** xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.10.0
- **Targets:** self-contained single-file `linux-x64` and `win-x64` builds

## Architecture
- `Program.cs` dispatches normal UI startup or the privileged Linux `--disk-helper` mode.
- `Application/BuilderApp*` owns menus, configuration, staging, preflight, and cleanup retry.
- `Application/InstallCoordinator` owns the irreversible sequence: identity revalidation, exact device-path confirmation, format, copy, and finalize. It takes `IDiskBackend`, `IUserInterface`, and `IPendingCleanupStore` so the safety properties are testable.
- `Services/Disks/*` owns disk inventory, identity, platform formatting, Linux helper/leases, and protected-path logic.
- `Services/DownloadService`, `GitHubReleaseClient`, `ArchiveService`, and `InstallService` handle verified acquisition, extraction, planning, and copying.
- `Services/PeerPressureHomebrewService`, `LaunchIniService`, and `XboxPath` handle Peer Pressure extras, `launch.ini`, and FAT/FATX path rules.
- Linux runs unprivileged; the scoped sudo helper independently validates the caller UID, disk fingerprint, lease, and mount, refuses to run from a caller-writable location, and formats through an `O_EXCL` handle. Windows requires elevation, locks volumes by access path, and revalidates disk identity after formatting.

## Key Documents
| Document | Path | Purpose |
|----------|------|---------|
| README | `README.md` | User workflows, safety notes, build/test/release instructions |
| Changelog | `CHANGELOG.md` | Release history and validation notes |
| Current release notes | `.github/release-notes/v2.5.0.md` | Published v2.5.0 changes and the Linux upgrade note |
| CI workflow | `.github/workflows/ci.yml` | Restore, audit, build, test, publish, smoke |
| Release workflow | `.github/workflows/release.yml` | Version/tag detection, builds, GitHub release publication |
| Pinned archive check | `scripts/verify_xeunshackle_max_release.py` | Verifies the pinned XeUnshackle Max ZIP |

## Conventions
- Nullable enabled; warnings and analyzers are errors.
- Prefer `internal` types and immutable records; tests access internals through `InternalsVisibleTo`.
- All relative paths go through `PathSafety`; FAT32 names through `FileServices.ValidateFatRelativePath`; Xbox HDD paths through `XboxPath.ValidateFatxPath`.
- Never build shell command strings: `ProcessRunner` receives argument lists.
- Trust comes only from the compiled artifact catalog. A server-supplied digest is transport integrity only.
- Destructive operations require complete preflight before confirmation, exact device-path confirmation, identity revalidation, a mount-point check, a flush, and cleanup handling.
- Disk eligibility is fail-closed: USB transport or removable flag, plus a serial/WWN/by-id identity.
- Tests use `TemporaryDirectory`; normal tests never touch a physical disk. Destructive-flow tests go through `InstallCoordinator` with fakes.

## How to Run
- Tests: `dotnet test BadBuilder.Tests/BadBuilder.Tests.csproj -c Release`
- Warning-free build: `dotnet build BadBuilder.Tests/BadBuilder.Tests.csproj -c Release`
- Locked restore: `dotnet restore BadBuilder.Tests/BadBuilder.Tests.csproj --locked-mode`
- Linux publish: `dotnet publish BadBuilder/BadBuilder.csproj -c Release -r linux-x64 --self-contained true`
- Windows publish: `dotnet publish BadBuilder/BadBuilder.csproj -c Release -r win-x64 --self-contained true`
- Linux requires `sudo`, `lsblk`, `findmnt`, `mount`, `umount`, `sync` (optional `udevadm`). Install the binary root-owned; the helper refuses to run from a caller-writable directory.
- Windows runs from an Administrator terminal.

## How to Deploy
1. Bump `VersionPrefix` in `BadBuilder/BadBuilder.csproj`.
2. Add `.github/release-notes/vX.Y.Z.md`.
3. Update `CHANGELOG.md` and `README.md` as needed.
4. Push to `main` or dispatch `.github/workflows/release.yml`.
5. The workflow builds/tests both platforms, validates executable versions and the pinned archive, attests build provenance, and publishes the tagged GitHub release with `BadBuilder-linux-x64`, `BadBuilder-win-x64.exe`, and `SHA256SUMS`. Releases are created as drafts and published after upload.
