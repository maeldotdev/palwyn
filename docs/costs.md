# Palwyn — Costs

Current total: **â‚±0**. Everything used so far is free: .NET SDK, Windows App SDK, Android SDK/Studio, public documentation.

Prices are estimates — verify at purchase time.

---

**SERVICE:** Google Play Console
**PURPOSE:** Distribute the Android app
**WHY REQUIRED:** Normal install path; avoids Android 13+ "restricted settings" friction for notification access
**ESTIMATED COST:** USD 25, one-time
**WHEN REQUIRED:** Phase 17 (release) — or earlier for a closed beta
**FREE ALTERNATIVE:** Direct APK download
**TRADE-OFF:** Users must allow restricted settings; no auto-update; less trust

---

**SERVICE:** Microsoft Store (individual developer)
**PURPOSE:** Distribute and sign the Windows MSIX
**WHY REQUIRED:** Store signs the package — no code-signing certificate needed
**ESTIMATED COST:** Currently free for individuals (verify)
**WHEN REQUIRED:** Phase 17
**FREE ALTERNATIVE:** —
**TRADE-OFF:** Store certification; restricted capabilities (e.g. `phoneLineTransportManagement`) likely refused â†’ HFP audio may be unavailable in the Store build

---

**SERVICE:** Windows code signing (e.g. Azure Artifact Signing)
**PURPOSE:** Sign a direct-download installer
**WHY REQUIRED:** Unsigned MSIX cannot be installed normally; unsigned EXE triggers SmartScreen
**ESTIMATED COST:** ~USD 10/month
**WHEN REQUIRED:** Only if distributing outside the Store
**FREE ALTERNATIVE:** Store-only distribution
**TRADE-OFF:** No direct download channel

---

**SERVICE:** Apple Developer Program
**PURPOSE:** Future iOS version
**ESTIMATED COST:** USD 99/year
**WHEN REQUIRED:** Only if iOS work starts
**FREE ALTERNATIVE:** —

---

Not needed for any planned feature: cloud hosting, databases, storage, push services, analytics, paid APIs.

Libraries are free and open source, and all can be used in a GPL-3.0 app (Android: AndroidX and Compose incl. material-icons-extended, Apache 2.0; Windows: Windows App SDK, MIT; QRCoder, MIT).
