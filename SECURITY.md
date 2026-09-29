# Security policy

Palwyn reads and sends some of the most private data on a phone: text messages, calls, contacts, notifications and the clipboard. Security reports are welcome and taken seriously.

## Reporting a vulnerability

Please report privately, not in a public issue:

- On GitHub, open the repository's **Security** tab and choose **Report a vulnerability**.

Include what you found, how to reproduce it, and which versions of the Windows and Android apps you tested. You'll get an answer as soon as possible; please give a reasonable time to fix it before disclosing it publicly.

## What's in scope

- The link between phone and PC: pairing (QR and code), certificate pinning, TLS, the protocol's message validation ([docs/protocol.md](docs/protocol.md)).
- Anything that lets another device on the network read or send data, pose as a paired phone or PC, or get past a permission the user hasn't granted.
- Data the apps store or log that they shouldn't ([docs/security.md](docs/security.md) lists what is kept).

The threat model and its known limits are in [docs/security.md](docs/security.md). Things listed there as accepted risks (for example, malware already running on your phone or PC, or a rooted or compromised OS) are out of scope unless you find a way to make them worse.

## Supported versions

Palwyn hasn't had a first release yet. Fixes go into the latest code on the default branch.
