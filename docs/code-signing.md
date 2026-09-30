# Code signing policy

Palwyn's release files are built from this repository by the [Release workflow](../.github/workflows/release.yml) on GitHub Actions, never on a personal computer, so every signed file can be traced to the source code it was built from.

## Windows

Palwyn has applied to the [SignPath Foundation](https://signpath.org) for free code signing. Once approved, Windows releases will say here:

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Until then, Windows releases are signed with Palwyn's own self-signed certificate (subject `CN=Mael`), which the installer asks you to trust once. See [install.md](install.md).

## Android

Android releases are signed with Palwyn's release key (`CN=Mael`, certificate SHA-256 `f367beb4b61272c5664d3edf858724f07f0ba4179646e886e56df7e87e732db2`), also listed in each release's notes, so you can check an APK with `apksigner verify --print-certs`.

## Team and roles

| Role | Who |
|---|---|
| Committers and reviewers | [Mael](https://github.com/MaelPlays) |
| Approvers (release signing) | [Mael](https://github.com/MaelPlays) |

The project doesn't accept outside code yet ([CONTRIBUTING.md](../CONTRIBUTING.md)). All accounts with access to the repository and signing use multi-factor authentication.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Palwyn only talks to the phone or PC you pair it with, on your own network or cable; see [privacy.md](privacy.md).
