# Security Policy

## Supported versions

The latest published release receives security fixes. Older releases are not
supported.

## Reporting a vulnerability

Report vulnerabilities privately through GitHub Security Advisories:

https://github.com/mitch030504/BadBuilderLinux/security/advisories/new

Please do not open a public issue for a security problem.

## Scope

BadBuilder formats removable disks and downloads third-party payloads, so the
highest-value reports concern:

- the privileged Linux disk helper (`--disk-helper`) and its sudo boundary,
- artifact verification, pinning, and the download cache,
- archive extraction and path handling,
- disk selection, identity revalidation, and protected paths.

## What to include

A description of the issue, the affected version, reproduction steps, and any
evidence that the problem is reachable without the attacker already having
root or the user's own account.
