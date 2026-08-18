# BadBuilderLinux

BadBuilderLinux is a Linux-first, safety-hardened fork of [Pdawg-bytes/BadBuilder](https://github.com/Pdawg-bytes/BadBuilder). It prepares a FAT32 USB drive for the Xbox 360 BadUpdate or ABadAvatar exploit. It downloads and verifies the selected exploit, bootstrap, dashboard update, and homebrew archives; validates and stages everything; then formats and installs the complete plan.

The executable and .NET project retain the `BadBuilder` name for compatibility. This fork supports self-contained `win-x64` and `linux-x64` builds, preserves Windows behavior, and adds a native Linux disk backend. Linux is tested primarily on CachyOS/Arch and targets mainstream systemd desktops. macOS is not supported.

> [!CAUTION]
> BadBuilder erases the selected USB disk. It deliberately offers only writable USB/removable/hot-plug whole disks and excludes devices backing the operating system, boot, home, swap, the current workspace, or its cache. Always verify the model, size, serial, and exact device path before typing the final confirmation.

## Safety model

All downloads, archive extraction, and installation planning finish before BadBuilder asks for destructive confirmation. The preflight phase enforces trusted SHA-256 metadata where available, requires explicit approval for checksum-less bytes, rejects unsafe archive paths and FAT-incompatible names, validates expected archive layouts, detects case-insensitive destination collisions, and checks staging and target capacity.

On Linux, run the interactive application as your normal desktop user. BadBuilder invokes its own hidden disk helper through `sudo` only for preparation and cleanup. The helper independently re-enumerates the selected disk, verifies its fingerprint, takes a per-device lease, normally unmounts child volumes, creates an MBR with one `BADUPDATE` FAT32 partition, and mounts it below `/run/badbuilder/<uid>/<token>` with `nodev,nosuid,noexec` and caller ownership. It flushes and normally unmounts the USB when installation ends. It never uses force or lazy unmounting.

On Windows, launch from an Administrator terminal. BadBuilder excludes the Windows system disk, accepts USB/removable media only, requires every volume lock and dismount to succeed, revalidates identity immediately before formatting, and waits for drive-letter reassignment after refreshing the partition table.

Ctrl+C cancels downloads, extraction, and other preflight work. Once the exact device-path confirmation has been accepted and formatting starts, cancellation is deferred until disk cleanup finishes.

## Linux prerequisites

Install `sudo` and the standard util-linux/coreutils commands: `lsblk`, `findmnt`, `wipefs`, `blockdev`, `mount`, `umount`, and `sync`. `udevadm` is optional; BadBuilder polls for the new partition when it is unavailable. DiscUtils creates FAT32 itself, so `dosfstools` is not required.

On CachyOS or Arch, these tools are normally supplied by the `sudo`, `util-linux`, `util-linux-libs`, `coreutils`, and `systemd` packages.

Launch the Linux binary without sudo:

```bash
./BadBuilder
```

Do not run `sudo ./BadBuilder`. Interactive root launches are refused.

## Using BadBuilder

1. Open **Target drive** and choose the USB device. Fixed and protected disks are not shown.
2. Select BadUpdate or ABadAvatar, a post-exploit bootstrap, and any desired homebrew. Dashboard Update is a separate install mode for official `2.0.17559.0` update files.
3. Choose **Install**. BadBuilder resolves releases, verifies or explicitly approves hashes, extracts into a unique temporary run directory, validates layouts, and builds the complete copy plan.
4. Review the displayed path, model, byte size, and serial/WWN. Type the exact device path to authorize formatting.
5. Wait for the success message. Linux automatically flushes and unmounts the USB. Windows leaves its assigned drive letter in place for normal eject.

Expected disk, network, cache, and archive errors are shown as actionable messages and return to the menu. Detailed stack traces are written only to the per-user diagnostic log.

## Storage locations

Persistent downloads and their manifests live in the user cache:

- Linux: `$XDG_CACHE_HOME/badbuilder`, or `~/.cache/badbuilder`
- Windows: `%LOCALAPPDATA%\BadBuilder\Cache`

Transient extraction uses a unique directory below the operating-system temporary directory and is removed after each run. Existing legacy `Work` directories are intentionally left untouched.

## Download integrity

GitHub release sources use an exact asset glob and an explicit release policy. Drafts are never selected; ABadAvatar may use its newest prerelease, while other moving sources use the newest stable release. Zero or multiple matching assets are errors. GitHub's per-asset `digest` metadata and catalog-pinned SHA-256 values are treated as trusted and enforced strictly.

When a trusted checksum is unavailable, BadBuilder displays the source, release, asset, and computed SHA-256 and asks for explicit approval. The approved bytes are recorded in the cache manifest; changed bytes trigger a new warning. Downloads stream into a partial file, verify their length and hash, and replace a cache entry only after success. A failed download cannot overwrite a valid cached archive.

## Building and testing

The project requires the .NET 10 SDK.

```bash
dotnet restore BadBuilder.Tests/BadBuilder.Tests.csproj
dotnet build BadBuilder/BadBuilder.csproj -c Release --no-restore
dotnet test BadBuilder.Tests/BadBuilder.Tests.csproj -c Release --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r linux-x64 --self-contained true --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r win-x64 --self-contained true --no-restore
```

Nullable, platform, and analyzer warnings fail the build. The unit suite covers Linux inventory parsing and system ancestry, identity changes, shell-free command construction, release and asset policies, checksum/cache behavior, unsafe archives and extraction limits, Xbox path handling, `launch.ini` preservation, preflight copy planning, and FAT32 formatting against disposable raw images.

Two destructive tests are opt-in and never run in normal CI:

- Set `BADBUILDER_LOOP_TEST=1` and run the loop integration test as root. It creates its own sparse image below the test temporary directory and proves the `/dev/loop*` backing file is that exact image before formatting, mounting, copying, flushing, unmounting, and detaching it.
- A physical USB end-to-end test must be performed manually. Re-enumerate the sacrificial device and obtain a fresh confirmation of its path, model, size, and serial immediately before every run.

Set `BADBUILDER_LIVE_SMOKE=1` to resolve all upstream catalog sources without downloading an asset or touching a disk.

## Offline archives

If release metadata is unavailable and no valid cache exists, BadBuilder asks for a local archive for every configured artifact. No configured built-in homebrew is silently skipped. Local files go through the same checksum, approval, extraction, layout, and preflight checks as downloads.

## Credits

- Pdawg11239 / [Pdawg-bytes](https://github.com/Pdawg-bytes/BadBuilder) — original BadBuilder project
- Grimdoomer — [BadUpdate](https://github.com/grimdoomer/Xbox360BadUpdate)
- Shutterbug2000 — [ABadAvatar](https://github.com/shutterbug2000/ABadAvatar)
- FreeMyXe Team / InvoxiPlayGames — [FreeMyXe](https://github.com/FreeMyXe/FreeMyXe)
- Byrom90 — [XeUnshackle](https://github.com/Byrom90/XeUnshackle)
- Swizzy — Simple 360 NAND Flasher
- Team XeDEV — XeXMenu
- Phoenix — Aurora Dashboard
