#!/usr/bin/env python3
"""Verify the exact upstream XeUnshackle Max ZIP without extracting or flashing it."""

from hashlib import sha256
from io import BytesIO
from urllib.request import Request, urlopen
from zipfile import ZipFile

URL = (
    "https://github.com/klofi/XeUnshackle-Max/releases/download/v1.0.0/"
    "XeUnshackle-Max-v1.0.0.zip"
)
EXPECTED_SHA256 = "838cf16450790601158265c22c84d90c1f9ffff7ba07854b9781722dba2328e4"
EXPECTED_SIZE_LIMIT = 32 * 1024 * 1024

with urlopen(Request(URL, headers={"User-Agent": "BadBuilderLinux-CI"}), timeout=60) as reply:
    payload = reply.read(EXPECTED_SIZE_LIMIT + 1)

if len(payload) > EXPECTED_SIZE_LIMIT:
    raise SystemExit("XeUnshackle Max archive exceeds the expected download-size limit")

digest = sha256(payload).hexdigest()
if digest != EXPECTED_SHA256:
    raise SystemExit(f"Checksum mismatch: expected {EXPECTED_SHA256}, found {digest}")

with ZipFile(BytesIO(payload)) as archive:
    entries = [name.replace("\\", "/").rstrip("/") for name in archive.namelist()]
    entries = [name for name in entries if name]
    top_level = {name.split("/", 1)[0] for name in entries}
    if len(top_level) != 1:
        raise SystemExit(f"Expected exactly one top-level directory: {sorted(top_level)}")
    prefix = next(iter(top_level)) + "/"
    required = {
        prefix + "BadUpdatePayload/default.xex",
        prefix + "launch.ini",
    }
    actual = set(entries)
    missing = required - actual
    if missing:
        raise SystemExit(f"XeUnshackle Max archive is missing required files: {sorted(missing)}")
    if archive.testzip() is not None:
        raise SystemExit("XeUnshackle Max archive contains corrupt ZIP data")

print(f"XeUnshackle Max v1.0.0 verified: SHA256={digest}, {len(entries)} ZIP entries")
