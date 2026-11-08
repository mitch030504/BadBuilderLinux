# BadBuilderLinux

Prepare an Xbox 360 USB with BadUpdate, ABadAvatar, or the Peer Pressure softmod installer. BadBuilderLinux downloads the required files, verifies and stages them, then formats and installs the complete USB layout.

This is a Linux-first fork of [Pdawg-bytes/BadBuilder](https://github.com/Pdawg-bytes/BadBuilder), with self-contained Linux and Windows x64 builds. The executable and .NET project retain the `BadBuilder` name. Linux is tested primarily on CachyOS/Arch and targets mainstream systemd desktops; macOS is unsupported.

**New in v2.5.0:** a full-audit security release. Every built-in artifact is pinned to an exact tag, hash, and size; disk eligibility and protected paths are fail-closed; the target is formatted through an exclusive handle; copy success is verified and flushed; and the privileged helper refuses to run from a caller-writable location. **Linux users must install the binary root-owned** — see below. See the [changelog](CHANGELOG.md) for release history.

> [!CAUTION]
> Preparing a USB erases the entire selected disk. Check its device path, model, size, and serial before confirming. Save any console backups elsewhere before reusing an installer USB.

## Download and run

Get the binaries and `SHA256SUMS` from the [latest release](https://github.com/mitch030504/BadBuilderLinux/releases/latest). The published binaries include the .NET runtime.

| Platform | Download | Launch |
| --- | --- | --- |
| Linux x64 | `BadBuilder-linux-x64` | Install root-owned, then run as your desktop user |
| Windows x64 | `BadBuilder-win-x64.exe` | Launch and accept the administrator prompt |

On Linux, download the Linux binary and `SHA256SUMS` into the same directory, verify it, then install it to a root-owned location. The privileged helper refuses to run from a directory the calling user can modify, because sudo would otherwise execute a replaceable binary as root.

```bash
sha256sum --ignore-missing -c SHA256SUMS
chmod +x BadBuilder-linux-x64
sudo install -o root -g root -m 0755 BadBuilder-linux-x64 /usr/local/bin/badbuilder
badbuilder
```

Linux requires `sudo` and the standard util-linux/coreutils commands: `lsblk`, `findmnt`, `mount`, `umount`, and `sync`. `udevadm` is optional; DiscUtils creates FAT32, so `dosfstools` is unnecessary. On CachyOS/Arch these commands normally come from `sudo`, `util-linux`, `util-linux-libs`, `coreutils`, and `systemd`.

Run without sudo. BadBuilder requests sudo only for USB preparation and cleanup. On Windows, an Administrator terminal also works.

### Sudo rule

Grant sudo only to the installed helper, and only if you accept that the helper is the security boundary:

```
# /etc/sudoers.d/badbuilder
youruser ALL=(root) NOPASSWD: /usr/local/bin/badbuilder --disk-helper *
```

The helper independently validates the caller UID and GID, the device, its fingerprint, the per-device lease, and the mount path. It also refuses to run when its own executable or directory is writable by the calling user. Never point a sudoers rule at a binary in your home directory or in `~/Downloads`.

### Upgrading from 2.4.x

- Move the binary out of your home directory and install it root-owned (command above).
- Add the sudoers rule if you have not already. Without it, USB preparation fails with a permission error.
- `wipefs` and `blockdev` are no longer required; `sudo`, `lsblk`, `findmnt`, `mount`, `umount`, and `sync` are.
- A cached artifact whose upstream release changed will now fail its pinned hash instead of silently downloading new bytes. Delete the affected cache entry or wait for a catalog update.

## Choose a workflow

| Workflow | Select | Payload and homebrew |
| --- | --- | --- |
| BadUpdate | **Exploit → BadUpdate** | Rock Band Blitz demo, your chosen bootstrap, and USB homebrew |
| ABadAvatar | **Exploit → ABadAvatar** or **ABadAvatar 1.3 Beta** | Your chosen bootstrap and USB homebrew |
| Peer Pressure | **Exploit → Peer Pressure** | Official installer, Rock Band Blitz demo, bundled XeLL, and homebrew extras for the HDD |
| Official dashboard update | **Update Xbox Dashboard** | Official `2.0.17559.0` update files |

1. Open **Target drive** and choose the USB. Fixed, protected, and read-only disks are excluded.
2. Choose the workflow above. For BadUpdate or ABadAvatar, select XeUnshackle, XeUnshackle Max, or FreeMyXe under **Post-exploit bootstrap**. Open **Homebrew** to add or remove apps.
3. Choose **Install**. Downloads, checksums, extraction, layout validation, capacity checks, and the complete copy plan finish first.
4. Review the device identity and type its exact path to authorize erasing it.
5. Wait for completion. Linux flushes and unmounts the USB; Windows leaves it ready for normal eject. If Linux cleanup fails, close any programs using the drive and choose **Retry USB cleanup** from the main menu before starting another install or removing the drive.

Expected disk, network, cache, and archive failures return to the menu with an error message. Stack traces go to the per-user diagnostic log. Ctrl+C cancels preflight work; after formatting starts, cancellation is deferred until cleanup finishes.

> [!WARNING]
> ABadAvatar 1.3 Beta is a prerelease. Its upstream notes advise disconnecting Ethernet and Wi-Fi before triggering it and warn that a connected Kinect can reduce success rates.

## Peer Pressure and Aurora

Peer Pressure mode uses the [official v1.0.477 `SoftmodInstaller.zip`](https://github.com/grimdoomer/Xbox360PeerPressure/releases/tag/v1.0.477), pinned to its exact tag and upstream-published SHA-256. It includes its own exploit payload, so the bootstrap menu is hidden. The upstream license, extras catalog, and bundled XeLL are preserved.

### Prepare the USB

1. Select **Exploit → Peer Pressure**.
2. Open **Homebrew** to choose the packaged apps. Aurora, XeXMenu, and Simple 360 NAND Flasher are included by default; custom archives can be added.
3. For automatic Aurora startup, choose **Set default dashboard → Aurora**. Choose a dashboard here; maintenance tools such as NAND Flasher are also selectable and should not be used as dashboards.
4. Select the target USB and choose **Install**.

BadBuilder stages each app under `BadUpdatePayload/SoftmodExtras` and appends its installation entry to `SoftmodExtras.ini`. The selected dashboard gets a `DashXex` setting after its executable has been checked. Apps install to `Hdd1:\Apps\...`; XeXMenu retains its `Hdd1:\Content\...` layout. Complete HDD destinations and every copied entry are checked against FATX limits (42-character names and 240-character paths) during preflight. Peer Pressure uses its own dashboard setting. DashLaunch and its plugins are unsupported.

### Apply it on the Xbox

Follow the [official installation guide](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Installation). Launch the Rock Band Blitz demo and press A at the title screen. Create and keep a system backup, then enable **Install Extras** and select the apps you want, including **Aurora** to apply its default-dashboard setting. Use the appropriate installation option for your console; **Extras Only** installs the selected extras onto an existing Peer Pressure setup.

If Aurora is already installed and you forgot to set the default, use **Guide → Settings → Softmod Settings → Dashboard** to choose `Hdd1:\Apps\Aurora\Aurora.xex`. See the [upstream settings guide](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Softmod-Settings). A dashboard startup problem can be bypassed with [Peer Pressure safe mode](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Booting-Your-Console): with the console off, hold Sync, power on with Eject, and keep holding Sync until the MS dashboard loads.

> [!WARNING]
> Peer Pressure supports Xenon, Zephyr, Falcon, Jasper, and Trinity; Corona and Winchester are unsupported. An HDD is required, with a mechanical SATA HDD recommended. Installation modifies NAND and HDD security sectors; failure can cause data loss or prevent booting and may require a hardware NAND flasher. Back up the NAND, CPU fuses/key, and HDD security sectors with the official installer, and keep those files somewhere safe outside the USB.

Aurora default-dashboard startup has been confirmed on one physical setup. That report does not establish compatibility across all console and HDD combinations.

## Other bootstraps

XeUnshackle and XeUnshackle Max support default USB homebrew launching through `launch.ini`; FreeMyXe supplies a lightweight payload without that automatic-launch integration.

XeUnshackle Max is an independent **beta** fork pinned to [klofi/XeUnshackle-Max v1.0.0](https://github.com/klofi/XeUnshackle-Max/releases/tag/v1.0.0) and its upstream SHA-256. It adds configurable auto-start, CPU/DVD-key masking, and boot-video options; see the [upstream documentation](https://github.com/klofi/XeUnshackle-Max#features). BadBuilder copies its `BadUpdatePayload` and `launch.ini`. Bundled JRPC2/Xbdm remote-access binaries are omitted; the supplied `launch.ini` may reference plugins that are absent.

Both XeUnshackle variants can create or replace `BadUpdatePayload/default.xex` during preflight; other undeclared collisions are rejected. The selected homebrew entry point and `launch.ini` are validated and updated in staging before the installation plan is built, so a malformed configuration fails before the USB is erased. These bootstraps are not persistent softmods. Do not flash modified NAND images or overwrite console flash files when using them.

## Disk and download safeguards

BadBuilder offers only eligible writable USB or removable whole disks. `HOTPLUG` alone is not treated as removable, because many internal SATA ports report it. A disk must also expose a serial number, a WWN, or a stable `/dev/disk/by-id` name, so two identical sticks cannot be swapped between confirmation and formatting. It validates the complete installation plan before destructive confirmation, rejects unsafe archive paths, FAT-incompatible names, and unintended file collisions, and rechecks the disk identity before formatting.

On Linux, disks backing the OS, boot, any user's home, swap, workspace, or cache are excluded, along with separately mounted system volumes such as `/srv`, `/var`, `/usr`, `/opt`, `/efi`, and `/run`. Multi-device filesystems are protected as a whole: every member of a btrfs filesystem or mdraid array sharing the protected UUID is excluded. Protected paths and mount targets are resolved through symlinks, and the scoped sudo helper independently resolves the caller's home from their UID rather than trusting root's environment. The helper opens the target with `O_EXCL`, revalidates the fingerprint while holding it, verifies the size on the handle, erases the first and last MiB so old GPT headers cannot survive, creates an MBR with one `BADUPDATE` FAT32 partition, and mounts it below `/run/badbuilder/<uid>/<token>` with `nodev,nosuid,noexec` and caller ownership. Cleanup flushes and normally unmounts it; force and lazy unmounting are not used. A failed cleanup is persisted so **Retry USB cleanup** can reuse the authenticated lease after the app restarts, and a lease left by a killed helper is released automatically once its process is gone.

On Windows, system disks are excluded and USB/removable media is required. Disks that report a non-512-byte sector size are rejected. Every partition's volume access path is locked and dismounted, including unlettered and folder-mounted volumes, and the operation aborts if partitions exist but none can be locked. The selected disk identity is revalidated again after formatting before any drive letter is accepted. The destination must be exactly one FAT32 `BADUPDATE` volume, preventing a replacement device that reuses the same disk index from becoming the copy target. The copy is revalidated against the disk and flushed before the app reports success. The published Windows executable requests administrator privileges; the Linux executable has no elevation manifest.

Every built-in artifact is pinned to an exact release tag, a compile-time SHA-256, and an expected size. GitHub's asset digest is treated as transfer integrity only: it never substitutes for a catalog pin, so the approval prompt still runs for anything unpinned. Drafts are excluded and exactly one asset must match. Changed bytes require fresh approval, and failed downloads cannot replace valid cached archives. The cache manifest records a previous decision but is never a source of authority; its archive path must resolve inside the artifact directory, and a manifest whose trusted hash disagrees with its approved hash is discarded.

Downloads and manifests are cached at `$XDG_CACHE_HOME/badbuilder` (otherwise `~/.cache/badbuilder`) on Linux and `%LOCALAPPDATA%\BadBuilder\Cache` on Windows. The cache directory must not be writable by other users. Transient extraction directories are removed after each run. If online metadata is unavailable, a transfer fails, or a cached archive is invalid, BadBuilder offers a local archive through the same verification, approval, extraction, and planning checks. Separate deadlines cover GitHub metadata and each download, so a stalled server cannot hold preflight indefinitely. Configured built-in apps are never silently skipped.

## Build and test

The source requires the .NET 10 SDK.

```bash
dotnet restore BadBuilder.Tests/BadBuilder.Tests.csproj --locked-mode
dotnet build BadBuilder.Tests/BadBuilder.Tests.csproj -c Release --no-restore
dotnet test BadBuilder.Tests/BadBuilder.Tests.csproj -c Release --no-build --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r linux-x64 --self-contained true --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r win-x64 --self-contained true --no-restore
```

Nullable, platform, and analyzer warnings fail the build. The 202-test suite covers disk inventory, eligibility, and identity, symlinked and multi-device protected paths, exclusive-handle formatting, GPT erasure, downloads and cache integrity, timeout and local-archive fallback, archive limits and paths, install planning and execution, FAT32 allocation against disposable images from 1 to 16 GiB, launch.ini preflight, pending-cleanup recovery, the destructive install sequence, and Peer Pressure extras, FATX limits, default-dashboard paths, and catalog injection checks. Supported devices are 1 GiB to about 1 TiB; the 1–16 GiB images are test coverage, not a device limit.

Optional checks:

- `BADBUILDER_LIVE_SMOKE=1` resolves upstream sources without downloading assets or touching a disk.
- `BADBUILDER_PEER_PRESSURE_ARCHIVES=/path/to/archives` enables the real-archive integration test. Supply `SoftmodInstaller.zip`, `GameData.zip`, `Aurora.rar`, `MenuData.7z`, and `Flasher.7z`. The test verifies the installer checksum, extracts and installs into a temporary directory, compares copied-file hashes, and checks Aurora's generated dashboard setting.
- `BADBUILDER_LOOP_TEST=1` enables the root-only loop-device test. It creates and verifies its own disposable backing image before formatting, mounting, copying, and cleaning up. Normal CI never runs it.
- Physical USB tests are manual. Recheck the sacrificial device's path, model, size, and serial immediately before each destructive run.

## Releases

See [CHANGELOG.md](CHANGELOG.md) and the [release notes](.github/release-notes). To publish, increase `VersionPrefix` in `BadBuilder/BadBuilder.csproj`, add matching `.github/release-notes/vX.Y.Z.md`, update the changelog, and merge to `main`. GitHub Actions builds and tests Linux and Windows, validates versions and binary formats, generates checksums and build provenance, and publishes the tag and release. Releases are created as a draft, uploaded and verified, then published, so a failed upload can be repaired by rerunning the workflow. A manual dispatch from any branch other than `main` is refused, and a tag that points at a different commit is rejected.

Previous binaries remain available on the releases page. Returning to an older builder affects future USB preparation; use Peer Pressure's official recovery/uninstall procedures for an already modified console.

## Credits

- Pdawg11239 / [Pdawg-bytes](https://github.com/Pdawg-bytes/BadBuilder) — original BadBuilder
- Grimdoomer — [BadUpdate](https://github.com/grimdoomer/Xbox360BadUpdate) and [Peer Pressure](https://github.com/grimdoomer/Xbox360PeerPressure)
- Shutterbug2000 — [ABadAvatar](https://github.com/shutterbug2000/ABadAvatar)
- Bibarub — [ABadAvatar 1.3 Beta](https://github.com/bibarub/Xbox360BadUpdate/releases/tag/avatar-v1.3-beta)
- FreeMyXe Team / InvoxiPlayGames — [FreeMyXe](https://github.com/FreeMyXe/FreeMyXe)
- Byrom90 — [XeUnshackle](https://github.com/Byrom90/XeUnshackle)
- klofi — [XeUnshackle Max](https://github.com/klofi/XeUnshackle-Max)
- Swizzy — Simple 360 NAND Flasher
- Team XeDEV — XeXMenu
- Phoenix — Aurora Dashboard
