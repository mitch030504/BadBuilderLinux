# BadBuilderLinux

Prepare an Xbox 360 USB with BadUpdate, ABadAvatar, or the Peer Pressure softmod installer. BadBuilderLinux downloads the required files, verifies and stages them, then formats and installs the complete USB layout.

This is a Linux-first fork of [Pdawg-bytes/BadBuilder](https://github.com/Pdawg-bytes/BadBuilder), with self-contained Linux and Windows x64 builds. The executable and .NET project retain the `BadBuilder` name. Linux is tested primarily on CachyOS/Arch and targets mainstream systemd desktops; macOS is unsupported.

**New in v2.4.0:** Peer Pressure installer support, homebrew installation through its extras catalog, and Aurora default-dashboard setup. See the [changelog](CHANGELOG.md) for release history.

> [!CAUTION]
> Preparing a USB erases the entire selected disk. Check its device path, model, size, and serial before confirming. Save any console backups elsewhere before reusing an installer USB.

## Download and run

Get the binaries and `SHA256SUMS` from the [latest release](https://github.com/mitch030504/BadBuilderLinux/releases/latest). The published binaries include the .NET runtime.

| Platform | Download | Launch |
| --- | --- | --- |
| Linux x64 | `BadBuilder-linux-x64` | Make executable and run as your desktop user |
| Windows x64 | `BadBuilder-win-x64.exe` | Launch and accept the administrator prompt |

On Linux, download the Linux binary and `SHA256SUMS` into the same directory, then run:

```bash
sha256sum --ignore-missing -c SHA256SUMS
chmod +x BadBuilder-linux-x64
./BadBuilder-linux-x64
```

Linux requires `sudo` and the standard util-linux/coreutils commands: `lsblk`, `findmnt`, `wipefs`, `blockdev`, `mount`, `umount`, and `sync`. `udevadm` is optional; DiscUtils creates FAT32, so `dosfstools` is unnecessary. On CachyOS/Arch these commands normally come from `sudo`, `util-linux`, `util-linux-libs`, `coreutils`, and `systemd`.

Run without sudo. BadBuilder requests sudo only for USB preparation and cleanup. On Windows, an Administrator terminal also works.

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
5. Wait for completion. Linux flushes and unmounts the USB; Windows leaves it ready for normal eject. Resolve any cleanup warning before removing the drive.

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

BadBuilder stages each app under `BadUpdatePayload/SoftmodExtras` and appends its installation entry to `SoftmodExtras.ini`. The selected dashboard gets a `DashXex` setting after its executable has been checked. Apps install to `Hdd1:\Apps\...`; XeXMenu retains its `Hdd1:\Content\...` layout. Peer Pressure uses its own dashboard setting. DashLaunch and its plugins are unsupported.

### Apply it on the Xbox

Follow the [official installation guide](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Installation). Launch the Rock Band Blitz demo and press A at the title screen. Create and keep a system backup, then enable **Install Extras** and select the apps you want, including **Aurora** to apply its default-dashboard setting. Use the appropriate installation option for your console; **Extras Only** installs the selected extras onto an existing Peer Pressure setup.

If Aurora is already installed and you forgot to set the default, use **Guide → Settings → Softmod Settings → Dashboard** to choose `Hdd1:\Apps\Aurora\Aurora.xex`. See the [upstream settings guide](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Softmod-Settings). A dashboard startup problem can be bypassed with [Peer Pressure safe mode](https://github.com/grimdoomer/Xbox360PeerPressure/wiki/Booting-Your-Console): with the console off, hold Sync, power on with Eject, and keep holding Sync until the MS dashboard loads.

> [!WARNING]
> Peer Pressure supports Xenon, Zephyr, Falcon, Jasper, and Trinity; Corona and Winchester are unsupported. An HDD is required, with a mechanical SATA HDD recommended. Installation modifies NAND and HDD security sectors; failure can cause data loss or prevent booting and may require a hardware NAND flasher. Back up the NAND, CPU fuses/key, and HDD security sectors with the official installer, and keep those files somewhere safe outside the USB.

Aurora default-dashboard startup has been confirmed on one physical setup. That report does not establish compatibility across all console and HDD combinations.

## Other bootstraps

XeUnshackle and XeUnshackle Max support default USB homebrew launching through `launch.ini`; FreeMyXe supplies a lightweight payload without that automatic-launch integration.

XeUnshackle Max is an independent **beta** fork pinned to [klofi/XeUnshackle-Max v1.0.0](https://github.com/klofi/XeUnshackle-Max/releases/tag/v1.0.0) and its upstream SHA-256. It adds configurable auto-start, CPU/DVD-key masking, and boot-video options; see the [upstream documentation](https://github.com/klofi/XeUnshackle-Max#features). BadBuilder copies its `BadUpdatePayload` and `launch.ini`. Bundled JRPC2/Xbdm remote-access binaries are omitted; the supplied `launch.ini` may reference plugins that are absent.

Both XeUnshackle variants can create or replace `BadUpdatePayload/default.xex` during preflight; other undeclared collisions are rejected. These bootstraps are not persistent softmods. Do not flash modified NAND images or overwrite console flash files when using them.

## Disk and download safeguards

BadBuilder offers only eligible writable USB/removable/hot-plug whole disks. It validates the complete installation plan before destructive confirmation, rejects unsafe archive paths, FAT-incompatible names, and unintended file collisions, and rechecks the disk identity before formatting.

On Linux, disks backing the OS, boot, home, swap, workspace, or cache are excluded. The scoped sudo helper independently checks the disk fingerprint, takes a per-device lease, unmounts child volumes, creates an MBR with one `BADUPDATE` FAT32 partition, and mounts it below `/run/badbuilder/<uid>/<token>` with `nodev,nosuid,noexec` and caller ownership. Cleanup flushes and normally unmounts it; force and lazy unmounting are not used.

On Windows, system disks are excluded and USB/removable media is required. Every volume lock and dismount must succeed, disk identity is revalidated, and drive-letter reassignment is checked after formatting. The published Windows executable requests administrator privileges; the Linux executable has no elevation manifest.

GitHub sources require exactly one matching asset and an explicit release policy. Drafts are excluded. Peer Pressure, ABadAvatar 1.3 Beta, and XeUnshackle Max use pinned releases and SHA-256 values. Other sources follow their configured stable/prerelease policies. GitHub asset digests and catalog pins are enforced; when no trusted checksum exists, BadBuilder asks you to approve the displayed source and computed hash. Changed bytes require fresh approval, and failed downloads cannot replace valid cached archives.

Downloads and manifests are cached at `$XDG_CACHE_HOME/badbuilder` (otherwise `~/.cache/badbuilder`) on Linux and `%LOCALAPPDATA%\BadBuilder\Cache` on Windows. Transient extraction directories are removed after each run; legacy `Work` directories are left alone. If online metadata and valid cache entries are unavailable, local archives go through the same verification, approval, extraction, and planning checks. Configured built-in apps are never silently skipped.

## Build and test

The source requires the .NET 10 SDK.

```bash
dotnet restore BadBuilder.Tests/BadBuilder.Tests.csproj
dotnet build BadBuilder.Tests/BadBuilder.Tests.csproj -c Release --no-restore
dotnet test BadBuilder.Tests/BadBuilder.Tests.csproj -c Release --no-build --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r linux-x64 --self-contained true --no-restore
dotnet publish BadBuilder/BadBuilder.csproj -c Release -r win-x64 --self-contained true --no-restore
```

Nullable, platform, and analyzer warnings fail the build. The suite covers disk inventory and identity, downloads and cache integrity, archive limits and paths, install planning, FAT32 formatting against disposable images, and Peer Pressure extras, default-dashboard paths, and catalog injection checks.

Optional checks:

- `BADBUILDER_LIVE_SMOKE=1` resolves upstream sources without downloading assets or touching a disk.
- `BADBUILDER_PEER_PRESSURE_ARCHIVES=/path/to/archives` enables the real-archive integration test. Supply `SoftmodInstaller.zip`, `GameData.zip`, `Aurora.rar`, `MenuData.7z`, and `Flasher.7z`. The test verifies the installer checksum, extracts and installs into a temporary directory, compares copied-file hashes, and checks Aurora's generated dashboard setting.
- `BADBUILDER_LOOP_TEST=1` enables the root-only loop-device test. It creates and verifies its own disposable backing image before formatting, mounting, copying, and cleaning up. Normal CI never runs it.
- Physical USB tests are manual. Recheck the sacrificial device's path, model, size, and serial immediately before each destructive run.

## Releases

See [CHANGELOG.md](CHANGELOG.md) and the [release notes](.github/release-notes). To publish, increase `VersionPrefix` in `BadBuilder/BadBuilder.csproj`, add matching `.github/release-notes/vX.Y.Z.md`, update the changelog, and merge to `main`. GitHub Actions builds and tests Linux and Windows, validates versions and binary formats, generates checksums, and publishes the tag and release. An existing release is skipped; rerun the workflow to retry a failed publication.

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
