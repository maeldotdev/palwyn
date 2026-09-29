# Palwyn

Native Android ↔ Windows phone companion. Local-first: phone and PC talk directly over your Wi-Fi with end-to-end mutual TLS. No cloud, no account.

- Windows: C# / .NET 10 / WinUI 3, lives in the system tray.
- Android: Kotlin / Jetpack Compose.

## Status

| Phase | State |
|---|---|
| 0 — Feasibility | Done ([report](docs/phase-0-feasibility.md)) — calls control-only, no PC call audio |
| 1 — Architecture | Done |
| 2 — Windows tray foundation | Done |
| 3 — Android companion foundation | Done |
| 4 — Secure pairing | Done |
| 5 — Connection engine | Done |
| 6 — Calls | Done (control only; audio stays on the phone) |
| 7 — Messages | Done (SMS only) |
| 8 — Notifications | Done |
| 9 — Photos | Done |
| 10 — Files / Quick Drop | Done |
| 11 — Clipboard / deep links | Done (phone → PC needs a tap: Android limit) |
| 12 — Dashboard / Actions | Done (incl. media controls) |
| 13 — Premium UI | Done |
| 14 — Reliability | Done on an Android 16 emulator ([reliability.md](docs/reliability.md)) |
| 15 — Security audit | Done ([security.md §9](docs/security.md)) |
| 16 — Performance | Done except the unplugged battery A/B test ([performance.md](docs/performance.md)) |
| 17 — Release | |

## Docs

- [Architecture](docs/architecture.md)
- [Protocol v1](docs/protocol.md) · [conformance vectors](shared/protocol/test-vectors.json)
- [Security model](docs/security.md)
- [Feature matrix](docs/feature-matrix.md)
- [Development & testing](docs/development.md)
- [Costs](docs/costs.md)
- [Security policy](SECURITY.md)

## License

GNU General Public License v3.0 or later. See [LICENSE](LICENSE) and [NOTICE](NOTICE). You can use, study, share and change Palwyn; if you share a changed version, it must stay under the same licence with its source code, and keep the credits.

The name and logo are not part of the licence: a changed version must use a different name. See [TRADEMARKS.md](TRADEMARKS.md).

## Contact

- Bugs and ideas: [Issues](https://github.com/maeldotdev/palwyn/issues)
- Questions: [Discussions](https://github.com/maeldotdev/palwyn/discussions)
- Security problems: privately, see [SECURITY.md](SECURITY.md)
- Contributing: see [CONTRIBUTING.md](CONTRIBUTING.md)
