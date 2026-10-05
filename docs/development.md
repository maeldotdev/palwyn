# Palwyn — Development & Testing

## 1. Setup (all free)

### Windows app
- Windows 11, **.NET 10 SDK**.
- **Developer Mode** on (Settings → System → For developers). Needed to register the packaged app from the build output.
- **Windows App Runtime 2.5** (installed with the Windows App SDK developer package). The app is framework-dependent on it.
- Windows App SDK build tools come from NuGet. Visual Studio is optional (XAML hot reload, debugger UI).

```
cd apps/windows
dotnet test Palwyn.slnx -p:Platform=x64
./tools/run-dev.ps1                                   # stop running copy, build, register, launch
./tools/run-dev.ps1 -AppArgs "--demo=connected --flyout"   # Debug-only UI states
```

Debug switches (`DevArgs.cs`, compiled out of Release): `--demo=connected|charging|connecting|disconnected`, `--flyout`, `--settings`, `--calls` (Calls page), `--messages` (Messages page), `--notifications` (Notifications page), `--photos` (Photos page), `--drop` (Send to phone window), `--call=ringing|active|ended` (demo call card; its buttons fail since the call is fake). A flyout opened by `--flyout` can't take focus (Windows blocks focus for background launches) and renders empty; open it by clicking the tray icon instead.

Logs: `%LOCALAPPDATA%\Packages\Palwyn.Dev_*\LocalState\logs\` (Settings → About → Open log folder). Kept 7 days.

Icons are placeholders generated from Segoe Fluent Icons by `tools/make-icons.ps1`; replace in the design phase.

### Core on Linux
`Palwyn.Core` is plain `net10.0`, so its tests run on any OS with the .NET 10 SDK: `dotnet test apps/windows/tests/Palwyn.Core.Tests`. The Windows solution itself does not build on Linux. CI (`.github/workflows/ci.yml`) runs these tests on Ubuntu and Windows on every push.

### Linux app (in progress)
`apps/linux/src/Palwyn.Linux` is `palwyn-linux`, an Avalonia app: with no arguments it opens the tray and window (`--hidden`: tray only); the commands `pair` (or `pair --address <ip>`), `run`, `status` and `unpair` run it headless. The window also starts on Windows, which is handy for layout work (no discovery or notifications there). `tools/linux/build-appimage.sh <version>` builds the AppImage and tar.gz on Linux; CI runs it on every push. It needs the .NET 10 runtime, `avahi-browse` (avahi-utils) to find the phone, and `notify-send` (libnotify-bin) plus `gdbus` for desktop notifications. It stores its identity and paired phone in `$XDG_DATA_HOME/palwyn` (default `~/.local/share/palwyn`), and talks to phones running Palwyn 0.15 or later.

```
dotnet test apps/linux/tests/Palwyn.Linux.Tests
dotnet run --project apps/linux/src/Palwyn.Linux -- pair
```

### Android app
- **Android Studio** (bundles the JDK and Gradle). The Android SDK already exists at `%LOCALAPPDATA%\Android\Sdk`.
- Phone: Developer options → USB debugging on.
- realme UI: also enable *Disable permission monitoring* if installs over USB fail, and allow *Auto launch* / background activity for Palwyn.

```
cd apps/android
gradlew testDebugUnitTest assembleDebug
gradlew installDebug
```

The debug build installs as `dev.palwyn.debug` next to a future release build. `local.properties` (SDK path) is generated per machine and not committed. If Gradle fails with "Unable to establish loopback connection", point Java at a short temp folder: `-Djdk.net.unixdomain.tmpdir=C:\pbtmp`.

### Reference hardware
| Device | Details |
|---|---|
| Phone | realme narzo 50, RMX3286, realme UI 4.0, Android 13 |
| PC | Windows 11 build 26200, built-in Bluetooth (HFP audio route supported) |

A second phone from a different OEM (Samsung or Pixel) is recommended before Phase 14 (Reliability) to catch OEM-specific background behaviour.

## 2. Testing strategy

| Layer | What | Where | When |
|---|---|---|---|
| Protocol conformance | Every frame in `shared/protocol/test-vectors.json` accepted/rejected the same way | xUnit + JUnit (JVM) | Every build |
| Unit | Envelope validation, pairing proof/code maths, backoff schedule, state machine (fake clock + fake transport) | xUnit / JUnit | Every build |
| Integration (Windows) | Core client ↔ in-process fake phone server over **real TLS on loopback**: pairing, reject unpinned cert, revoke, heartbeat timeout, reconnect | xUnit | Every build |
| Integration (Android) | Link server ↔ JVM test client over loopback TLS | Robolectric or instrumented | Phase 4+ |
| Device | Scripted manual checklist per feature on the reference phone (e.g. "call from second phone → popup < 1 s → answer from PC → call connects") | Real hardware | End of each feature phase |
| Failure matrix | Wi-Fi off/on, PC sleep/wake, phone reboot, force-stop, battery saver, permission revoked | Real hardware | Phases 5, 15 |

Rules:
- Emulator results never count as passing for calls, SMS, Bluetooth or background survival.
- Every phase report lists which device checklist items passed on real hardware.
- Tests never contain real phone numbers or messages; use the `+1555…` fictional range.

## 3. Configuration

`Development`, `Beta`, `Production` differ only in: log level, a *diagnostics* screen, and package/app IDs (so a dev build installs beside a production build). No endpoints to configure — there is no server.
