# Decision: trust anchors, disk identity, and the privileged helper

**Date:** 2026-11-08
**Status:** Accepted
**Context:** Full repository audit found that several documented safety
properties did not hold in code.

## 1. The GitHub asset digest is not a trust anchor

`GitHubReleaseClient` used `ValidatePinnedHash(...) ?? digest`, so an artifact
without a catalog pin was silently "verified" against a digest from the same
API response that supplied the download URL. The unverified-artifact approval
prompt therefore never ran for 9 of 12 artifacts.

**Decision:** trust comes only from a compile-time catalog pin. The API digest
is recorded as `TransportSHA256` and checked for transfer corruption only. All
12 built-in artifacts now have an exact tag, a literal SHA-256, and an expected
size. The hashes were recomputed from the published bytes on 2026-11-08 and
matched GitHub's reported digests.

**Consequence:** a new upstream release no longer installs silently. The
catalog must be updated deliberately, which is the intended behaviour.

## 2. The download manifest is a record, not authority

`TrustedSHA256` was read back out of the manifest as an expected hash, and
`ArchivePath` could be absolute or contain `..`. A writable cache directory was
therefore a trust anchor.

**Decision:** the manifest may only record a previous decision. Expected hashes
come from the catalog; `ArchivePath` is stored relative and must resolve inside
the artifact directory; links and oversized manifests are rejected; a manifest
whose `TrustedSHA256` disagrees with `ApprovedSHA256` is discarded. The cache
directory must not be group/other-writable.

## 3. Disk eligibility is fail-closed

`HOTPLUG` was treated as evidence of a removable disk, but many internal SATA
ports report it. Protected paths covered only the caller's home and a few
system roots.

**Decision:** require a USB transport or the removable flag; require a serial,
WWN, or `/dev/disk/by-id` name; protect every passwd home and every system mount
root; protect all members of a multi-device filesystem by UUID/MD_UUID. The
target is opened with `O_EXCL`, revalidated while held, and formatted through
that handle.

## 4. The privileged helper must not be replaceable

`sudo -- <Environment.ProcessPath>` executes whatever binary is at that path.
If the path is user-writable, the "scoped helper" is arbitrary root code.

**Decision:** the helper refuses to run when its executable, its directory, or
the app base directory is writable by the calling UID/GID (checked with
`statx`, whose layout is architecture-independent). Commands are resolved from
a fixed system path when running as root, group/other-writable binaries are
refused, `LD_*` is cleared, and the cleanup token moves from argv to stdin.

**Consequence:** Linux users must install the binary to a root-owned location
and grant sudo to that path. This is a breaking change for anyone running the
downloaded binary from their home directory.

## 5. Copy success requires proof

The copy reported success based only on `Directory.Exists` and a free-space
check. On Windows the destination was a mutable drive letter; on Linux an
automounter unmount could redirect writes to the underlying directory.

**Decision:** require an exact mount point before and after the copy, revalidate
the disk identity and mount association immediately before writing, flush every
file to disk, verify bytes copied and resulting length against the plan, and
require the mounted volume to report `vfat` and `BADUPDATE`. Windows finalize
now calls `FlushFileBuffers`.

## 6. Tests must exercise the destructive path

No test referenced `BuilderApp`, `DiskService`, `PrepareAsync`, or the helper's
validation. The safety properties were unprovable.

**Decision:** extract the destructive sequence into `InstallCoordinator` with
injected `IDiskBackend`, `IUserInterface`, and `IPendingCleanupStore`. The
suite grew from 135 to 202 tests, including confirmation mismatch, pre-format
cancellation, copy failure after format, cleanup-record handling, lease
validation, and hash/size rejection.
