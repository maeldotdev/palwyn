# Palwyn for Linux (PC side) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Linux PC app that pairs with the existing Android app and offers the same features as the Windows app wherever Linux allows it, built in phases the user can stop after any one.

**Architecture:**
- `Palwyn.Core` stays the one shared library and stays where it is (`apps/windows/src/Palwyn.Core`, referenced by relative path; moving it would churn the Windows solution, CI and docs for no gain until a third platform exists).
- Phase 3 moves the platform-neutral hub (`Link/LinkManager.cs`, 755 lines) into Core behind one `IPcHost` interface, because today it is welded to WinUI (`DispatcherQueue`, `App.Current`, `Toasts`, `ApplicationData`). Windows and Linux each implement `IPcHost` once.
- New `apps/linux/` holds the Avalonia app `Palwyn.Linux` and its tests. The Windows app stays WinUI 3: no shared UI, no rewrite.

**Tech Stack:** .NET 10, Avalonia 11.x (MIT; its tray icon is a StatusNotifierItem and it already depends on Tmds.DBus.Protocol), Tmds.DBus.Protocol + `Tmds.DBus.Tool` bindings (MIT), QRCoder 1.8.0 (MIT, already on Windows), adb r37.0.1 Linux build, xunit.

**Spec:** [docs/draft-other-platforms.md](../../draft-other-platforms.md) section 1 (an idea draft, not a spec). Each phase is re-planned in detail (a fresh `writing-plans` pass) when the user starts it; Phase 1 below is complete, later phases are task-level.

**Stays Windows-only:** WinUI pages, `Toasts`, `TrayIcon`, `Win32.cs`, MSIX, Windows Share target, GlobalSystemMediaTransportControls, Core Audio, `UserNotificationListener`, CNG identity key.

## Global Constraints

- Windows behaviour must not change. `dotnet test apps/windows/tests/Palwyn.Core.Tests` passes on Windows and Linux after every phase. Windows files are touched only in Phase 3 and in the moves each later phase lists.
- Protocol v1 is unchanged except one enum value: HELLO `platform` gains `"linux"` (Phase 2). Core (`Protocol/Catalog.cs:30`) and Android (`protocol/Protocol.kt:119`) both validate `platform` against `android|windows` today, so this is the one Android change (see Decision 4).
- New dependencies: MIT, Apache-2.0 or BSD only (GPL-3.0-or-later compatible). Each goes into `NOTICE` in the phase that adds it.
- Identity: self-signed P-256, 20 years, same fingerprint rules. The key lives in `$XDG_DATA_HOME/palwyn/identity.key`, created with mode 0600 at creation (`FileMode.CreateNew` + `UnixCreateMode`), never chmod-after-write. Weaker than Windows' non-exportable CNG key; documented in `docs/security.md`.
- Paths (XDG, defaults after `?`): config `$XDG_CONFIG_HOME ? ~/.config`/palwyn, data `$XDG_DATA_HOME ? ~/.local/share`/palwyn, logs `$XDG_STATE_HOME ? ~/.local/state`/palwyn/logs (7 days, as Windows).
- Honesty: the Linux HELLO lists only capabilities it really implements; `PC_REMOTE` reports `input:false`/`media:false` when unavailable; the UI never shows a control that does nothing, and says why (existing `StateView` wording style).
- Never log message bodies, notification text, phone numbers, clipboard, file names, secrets, full fingerprints (architecture section 7).
- UI copy uses the Windows app's words ("Calls on your PC", "Photos and videos on your PC").
- Reference distros: Ubuntu 24.04 LTS (GNOME) and a current Fedora KDE; others best effort.
- Agent safety: never restart, shut down or sign out of the user's PC. WSL and Hyper-V are **not installed/enabled** on the dev PC (checked 2026-10-05) and enabling them needs a reboot, so no phase may depend on them. Never install a VM or software without asking.

## Review Focus

- A TLS 1.3 client-certificate rejection looks different under OpenSSL than SChannel: the engine must still end in `Unauthorized` after 3 rejections, not loop. Pinned in Phase 1 (the existing EngineTests rejection cases run on Linux).
- Avahi returns IPv6 link-local (`fe80::`) and several interfaces per phone: discovery must hand the engine a routable IPv4 (the Windows code does `ip.Contains('.')`) and fall back to the last known address when Avahi is absent or stopped. Pinned in Phase 2 (record parser test).
- Phone notification text containing `<b>`, `&`, `<a href>`: freedesktop servers render a markup subset, so text must be escaped and never become a link. Pinned in Phase 2 (`NotifyText.Escape` test).
- Stock GNOME has no tray host: the app must stay reachable (window opens at first start, hint explains the AppIndicator extension) instead of silently living nowhere. Pinned in Phase 4 (on-device check on Ubuntu GNOME).
- File names from the phone (`a/b`, `..`, `.`, NUL, 300 bytes, `ñ`, only dots): saved only inside the chosen folder, within Linux's 255-byte limit. Pinned in Phase 6 (`SafeName` Linux tests); Rescue paths go through the existing `ShellQuote`/`LocalPath` tests.

---

## Open decisions (answer before the phase that needs them)

| # | Decision | Needed by | Recommended default |
|---|---|---|---|
| 1 | Target desktops | Phase 4 | GNOME on Ubuntu 24.04 as the reference, KDE Plasma second; XFCE/Cinnamon/MATE best effort (they have tray hosts) |
| 2 | X11 vs Wayland priority | Phase 4 | Wayland session first, X11 fully supported. The window runs through XWayland (Avalonia has no native Wayland backend yet; re-check at Phase 4). Limits are listed per phase |
| 3 | Test environment | Phase 1 / Phase 2 | **The user has no Linux machine (2026-10-05).** Phase 1: GitHub Actions `ubuntu-latest`, nothing to install. CI (with Xvfb for UI screenshots) can check builds, unit tests and layout, but never a real phone link. So from Phase 2 the only way to test on this PC is a VirtualBox VM (Hyper-V is off, so it runs without enabling Windows features; installing VirtualBox needs the user's OK and must not need a reboot, else stop). Without a VM, Phases 2 to 8 ship untested on real devices and must say so in their release notes. Phase 2 on: a real desktop on the same LAN as the phone, which means a VM with a **bridged** adapter (VirtualBox or VMware Player; Ubuntu 24.04 GNOME, later Kubuntu) or a spare PC / live USB. NAT networking hides the phone from mDNS. Phase 8 needs USB pass-through of the phone (VirtualBox: USB 2/3 needs its free-for-personal-use Extension Pack, a dev tool only, never shipped) or a real Linux box |
| 4 | Android change for `platform:"linux"` | Phase 2 | Widen the enum in `Protocol.kt:119` and a test vector, ship an Android release first. Old phones will refuse a Linux HELLO (a protocol close), so the Linux app needs the new Android build. Alternative: Linux poses as `"windows"`: no Android change but dishonest, and it hides the platform from future UI. Not recommended |
| 5 | Identity key storage | Phase 2 | 0600 file (as ssh keys). libsecret later only if asked: needs a running keyring, absent on minimal setups |
| 6 | mDNS | Phase 2 | Avahi over D-Bus (installed by default on Ubuntu and most desktops), fallback "connect by address" (already built) and last known address. A managed .NET mDNS library only if Avahi proves unreliable on the reference distros |
| 7 | Packaging | Phase 4 / final section | AppImage + tar.gz from CI, from Phase 4 on. Flatpak as a later extra; .deb/.rpm not planned |
| 8 | Windows UI | now | Stays WinUI 3. No Avalonia rewrite of the Windows app |

## Phase overview

| # | Phase | Size | Ships | Stop gate |
|---|---|---|---|---|
| 1 | Foundation: Core builds and tests pass on Linux | S | CI job | Green Linux run, same test count as Windows |
| 2 | Headless Linux link: pair, connect, desktop notifications | M | `palwyn-linux` console app | Real phone pairs and stays connected from Linux |
| 3 | Share the link hub (Windows-touching refactor) | M | Windows app unchanged | Windows tests + smoke check identical |
| 4 | App shell: tray, Home, Add a phone, Settings, AppImage CI | M | First usable Linux build | App pairs and shows live status |
| 5 | Calls, messages, notifications pages | L | The core daily value | Answer a call, send a text, reply to a notification |
| 6 | Files, photos, Quick Drop, clipboard, contacts | L | Data features | Photo saved, file both ways |
| 7 | PC control: remote, media, volume, lock, keep awake | M | Phone-as-remote | Remote works on X11 and Wayland, or says it can't |
| 8 | Phone screen, USB link, emergency screen, Rescue files | M | Power features | Emergency screen from the cable |

Phase 4 is the earliest point at which a normal person could use it. Stopping after Phase 2 still leaves a working headless notification mirror. Token figures are order-of-magnitude guesses for one focused session each, not promises.

---

## Phase 1: Foundation (Core on Linux)

**Goal:** Prove `Palwyn.Core` and its tests behave the same on Linux, cheaply, before any Linux code exists.

**Files:**
- Create: `.github/workflows/ci.yml`
- Modify: `docs/development.md` (a short "Linux" section), plus whatever Core/test files the first Linux run shows to be Windows-only (expected: few or none)

**Interfaces:** none.

- [ ] **Step 1: Record the Windows baseline**

Run: `dotnet test apps/windows/tests/Palwyn.Core.Tests`
Expected: all pass; note the total test count `N`.

- [ ] **Step 2: Create the CI workflow**

Test the test project directly, not `Palwyn.slnx` (the solution contains the `net10.0-windows` app, which cannot build on Linux).

```yaml
# Runs the platform-neutral tests on Linux and Windows. Release builds stay in release.yml.
name: CI
on:
  push:
    branches: [main]
  pull_request:
  workflow_dispatch:
jobs:
  core-tests:
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet test apps/windows/tests/Palwyn.Core.Tests --logger "trx" --nologo
```

- [ ] **Step 3: Commit** `CI: run the Core tests on Linux and Windows`; the user pushes it (or says "push") and starts it from the Actions tab (workflow_dispatch).
- [ ] **Step 4: Read the Linux result.** Expected: green, and the same `N` tests as Step 1. If red, triage each failure into exactly one of:
  - the test assumes Windows (paths, `\`, culture, line endings): fix the test;
  - Core misbehaves on Linux (TLS, sockets, `X509CertificateLoader`, file locking): fix Core and keep the Windows run green;
  - OpenSSL-specific TLS 1.3 rejection behaviour: see Review Focus 1; the fix belongs in `LinkEngine`'s rejection counting, with a test that passes on both.
- [ ] **Step 5: Add the Linux section to `docs/development.md`:** `dotnet test apps/windows/tests/Palwyn.Core.Tests` works on any OS with the .NET 10 SDK; the Windows solution does not build on Linux.
- [ ] **Step 6: Commit** `Core: pass on Linux` (only if Step 4 needed changes) and `Docs: running the Core tests on Linux`.

**Exit check (user):** the Actions page shows both matrix jobs green with the same test count.
**Won't work yet:** everything; there is no Linux app.
**Size / effort:** S. About 30 to 60k tokens if the tests pass untouched; more if OpenSSL TLS behaviour needs a Core fix. No local Linux needed (WSL/Hyper-V are off and enabling them needs a reboot).

**STOP: wait for the user's approval before Phase 2.**

---

## Phase 2: Headless Linux link

**Goal:** A console app that pairs with the phone and stays connected on a real Linux desktop, mirroring phone notifications as desktop notifications. This proves Linux identity, mDNS and TLS against a real phone before any UI exists.

**Files:**
- Create: `apps/linux/Palwyn.Linux.slnx`; `apps/linux/src/Palwyn.Linux/Palwyn.Linux.csproj` (net10.0, references Core by relative path); `Program.cs` (`pair`, `run`, `status`, `unpair` commands); `Platform/Paths.cs` (XDG); `Platform/IdentityFile.cs`; `Platform/AvahiDiscovery.cs`; `Platform/DesktopNotifier.cs` (+ `NotifyText.Escape`); `Platform/Log.cs`
- Create: `apps/linux/tests/Palwyn.Linux.Tests/` (xunit): `IdentityFileTests.cs`, `AvahiRecordTests.cs`, `NotifyTextTests.cs`, `PathsTests.cs`
- Modify: `apps/windows/src/Palwyn.Core/Protocol/Catalog.cs:30` and `apps/android/.../protocol/Protocol.kt:119` (accept `"linux"`), `shared/protocol/test-vectors.json` (a Linux HELLO vector), `docs/protocol.md` (platform enum)
- Uses (no change): `PairedPhones`, `PcPairing`, `PairingInvite`, `LinkEngine`, `LinkConnection`

**Tasks:**
- [ ] Android and Core accept `platform:"linux"`; new vector passes in both test suites; the Android release is built and installed on the test phone first (Decision 4).
- [ ] `IdentityFile.Load()`: creates the P-256 key and self-signed cert if missing (0600 at creation), reloads it; test: a second `Load` returns the same fingerprint; the file mode is 600 on Unix.
- [ ] `AvahiDiscovery`: browse `_palwyn._tcp` through `org.freedesktop.Avahi` (ServiceBrowser, ServiceResolver, TXT `id`, `pair`, `n`), same `DiscoveredPhone` shape as the Windows `PhoneDiscovery`; pure parser test for TXT and address choice (IPv4 only, never `fe80::`).
- [ ] `pair`: print the invite as a terminal QR (QRCoder ASCII output) and the `palwyn://pair` text, wait for the phone, save to `paired-phones.json`.
- [ ] `run`: `LinkEngine` with the pinned-TLS connector, last-known-address fast path, reconnect (`Kick` on rediscovery); print state changes.
- [ ] `DesktopNotifier`: `org.freedesktop.Notifications.Notify` for each phone notification, replace-by-id on updates, close on removal; `NotifyText.Escape` test for `<`, `&`, `<a>`; no actions yet.
- [ ] Spike note (kept in the PR text, not a doc): which of Avahi / address-only worked on Ubuntu GNOME and KDE.

**Exit check (user, on the VM or Linux box with the new Android build):** run `palwyn-linux pair`, scan, then `run`; a phone notification appears as a desktop notification; toggling phone Wi-Fi reconnects; `unpair` and a wrong-fingerprint attempt are refused.
**Won't work yet:** any UI, tray, calls, messages, files. Notification replies and actions. Needs `avahi-daemon` (else address only) and a session D-Bus.
**Size / effort:** M. About 200 to 350k tokens; the D-Bus bindings (Avahi, Notifications) are the main cost.

**STOP: wait for the user's approval before Phase 3.**

---

## Phase 3: Share the link hub

**Goal:** Make `LinkManager` usable by both apps with no behaviour change on Windows. This is the only phase that restructures Windows code, so it is its own gate.

**Files:**
- Move: `apps/windows/src/Palwyn.App/Link/LinkManager.cs` to `apps/windows/src/Palwyn.Core/Link/LinkManager.cs`; likewise the platform-neutral parts of `Link/UsbLink.cs` (adb parsing/forward logic; the `DeviceWatcher` trigger stays in the app) and `Emergency/Adb.cs` (runner takes the adb path as a parameter)
- Create: `apps/windows/src/Palwyn.Core/Link/IPcHost.cs`, `apps/windows/src/Palwyn.Core/Link/IPhoneDiscovery.cs`; `apps/windows/src/Palwyn.App/WindowsHost.cs`
- Modify: `apps/windows/src/Palwyn.App/App.xaml.cs`, `Link/PhoneDiscovery.cs` (implements `IPhoneDiscovery`), `Program.cs` and the pages that construct or read `LinkManager`; `apps/linux/src/Palwyn.Linux` (Phase 2 `run` switches to the hub; `LinuxHost.cs`)
- Test: `apps/windows/tests/Palwyn.Core.Tests/LinkManagerTests.cs` (fake host + the existing fake-phone TLS loopback from `EngineTests`)

**Interfaces (decide once, in this phase's detailed plan):** one `IPcHost` with roughly: `DataDir`, `Post(Action)` (UI thread), `LoadIdentity()`, `ActivePhone` get/set, `AppVersion`, `Platform` (`"windows"`/`"linux"`), `Capabilities`, the network and resume signals (`NetworkChanged`, `Suspending`, `Resumed`), `IPhoneDiscovery`, a USB-change trigger, and the inbound callbacks (`OnSms`, `OnNotification`, `OnNotificationRemoved`, `OnCall`, `OnLinkLost`, `SetStatus`, `ClipboardReceived`, `DropOffered`, `ScreenState`, `EmergencyHint`, `RemoteInput`, `RemoteAct`). It exists because both platforms genuinely differ in every one; nothing else gets an interface.

**Tasks:**
- [ ] Write `LinkManagerTests` against a fake host first (connect, `OnSms` raised on the host's thread, `ActivePhone` switch, emergency hint after 20 s): red.
- [ ] Move the file, replace the six `App.Current`/`Toasts`/`PcRemote`/`ClipboardSync`/`ScreenWindow`/`Receiving` calls with `IPcHost` callbacks, `WindowsHost` forwarding to exactly those classes. Green.
- [ ] Build the Windows app (user runs `tools/run-dev.ps1`) and run the Windows smoke list below.

**Exit check (user):** Windows tests green on both OSes; on Windows, pair or reconnect, receive a notification toast, see a call card, send a text, and a Wi-Fi off/on reconnects, exactly as before.
**Won't work yet:** no new Linux feature; this is enabling work.
**Size / effort:** M. About 200 to 300k tokens; risk is Windows regressions, hence the smoke list. Skip-able if the user prefers the Linux app to carry its own hub copy (not recommended: 755 lines to maintain twice).

**STOP: wait for the user's approval before Phase 4.**

---

## Phase 4: App shell

**Goal:** The first release a normal user can install: tray presence, Home, Add a phone, Settings.

**Files:**
- Create: `apps/linux/src/Palwyn.Linux/App.axaml(.cs)`, `MainWindow.axaml(.cs)`, `Pages/HomePage.axaml`, `Pages/AddPhonePage.axaml`, `Pages/SettingsPage.axaml`, `Controls/StateView.axaml`, `TrayController.cs`, `Platform/Settings.cs` (JSON in the config dir, same keys as Windows `AppSettings`), `Platform/SingleInstance.cs` (owns D-Bus name `dev.palwyn.Palwyn`; a second launch raises the window), `Platform/Autostart.cs` (`~/.config/autostart/dev.palwyn.Palwyn.desktop`), `Assets/` (tray icons as PNG/SVG from `Assets/Tray`; JetBrains Mono, OFL, already bundled on Windows)
- Create: `tools/linux/build-appimage.sh`; modify `.github/workflows/release.yml` (a `linux` job: test, `dotnet publish -r linux-x64 --self-contained`, AppImage + tar.gz, `SHA256SUMS` already covers `dist/*`)
- Modify: `NOTICE` (Avalonia MIT, Tmds.DBus MIT), `README.md`, `docs/install.md`

**Tasks:**
- [ ] Avalonia shell, dark theme default like Windows; Home shows the same tiles from `DeviceStatus` (battery, signal, storage, model), recent activity, unpaired onboarding.
- [ ] Add a phone: QR with QRCoder (`PairingInvite.ToUri`), code confirmation, same wording as Windows.
- [ ] Tray via Avalonia `TrayIcon` (SNI). A normal small window for the tray's status/quick actions: no anchored flyout, because Wayland gives apps no global coordinates (`TrayPlacement` is Windows-only).
- [ ] No tray host detected (stock GNOME): open the main window at start and show the AppIndicator-extension hint (Review Focus 4).
- [ ] Settings: phones (add, switch, remove), theme, notifications on/off, autostart, log folder.
- [ ] Linux CI job and AppImage; install and uninstall text.

**Exit check (user, Ubuntu GNOME and KDE):** install the AppImage, pair by QR, see live battery and status on Home, tray icon on KDE and (with the extension) GNOME, autostart toggle works, second launch focuses the running window.
**Won't work yet:** calls, messages, notification list, files, remote. GNOME without the AppIndicator extension: no tray icon (window only). Anchored tray popup: not possible on Wayland.
**Size / effort:** M. About 400 to 700k tokens; first Avalonia and AppImage work dominates.

**STOP: wait for the user's approval before Phase 5.**

---

## Phase 5: Calls, messages, notifications

**Goal:** The daily-use trio, using the same hub methods and the same words as Windows.

**Files:**
- Create: `Pages/CallsPage.axaml`, `Pages/MessagesPage.axaml`, `Pages/NotificationsPage.axaml`, `Windows/CallWindow.axaml` (small topmost card; no focus steal where the compositor allows), `Platform/DesktopNotifier.cs` (extend: actions, reply), `Platform/AppIcons.cs` (PNG cache in the data dir)
- Test: `apps/linux/tests/Palwyn.Linux.Tests/NotifyActionTests.cs`

**Tasks:**
- [ ] Calls page and call card: answer, decline, end, call log by day; the card never offers PC audio.
- [ ] Messages page: conversations, bubbles, composer, `SmsLength` indicator, new-message notification.
- [ ] Notifications page: live list, dismiss, actions, reply box, per-app off, history and search (Core).
- [ ] Desktop notifications: freedesktop `actions` (answer/decline, notification actions), replace and close with the phone. Inline reply uses KDE's `x-kde-reply-placeholder-text` hint where present; elsewhere a "Reply" action opens the app on that thread.
- [ ] Emoji: no OS panel call; document `Ctrl+.` (GNOME/KDE) in the composer hint.

**Exit check (user, on device):** ring the phone, answer from the card; send and receive a text; reply to a WhatsApp notification; dismiss on PC removes it on the phone.
**Won't work yet:** inline reply on GNOME (opens the app instead). Call card cannot be forced topmost by some Wayland compositors (GNOME), so it may sit behind other windows; the desktop notification is the reliable alert there.
**Size / effort:** L. About 500 to 900k tokens (three pages plus notification actions across two desktops).

**STOP: wait for the user's approval before Phase 6.**

---

## Phase 6: Files, photos, Quick Drop, clipboard, contacts

**Goal:** Data features, with Linux-honest clipboard behaviour.

**Files:**
- Create: `Pages/PhotosPage.axaml`, `Pages/ContactsPage.axaml`, `Windows/QuickDropWindow.axaml`, `Platform/Receiving.cs` (or move `Receiving.cs` to Core behind `IPcHost`), `Platform/ClipboardSync.cs`, `Platform/FileManagerIntegration` (a `.desktop` file with `MimeType=*/*` and `%F`, so "Open With Palwyn" and Dolphin/Nautilus actions send files)
- Modify: `apps/windows/src/Palwyn.Core/Photos.cs` (`SafeName` gains a Linux 255-byte/NUL rule; Windows rules stay a superset), `ContactBook` neutral parts into Core if cheap
- Test: `PhotosSafeNameTests.cs` (Core.Tests, run on both OSes), `ClipboardEchoTests.cs`

**Tasks:**
- [ ] Photos grid with previews (same 4-at-a-time loading), albums, save to `~/Pictures/Palwyn`, batch save, camera button.
- [ ] Quick Drop window (drop, pick, type, folders up to 1,000 files), offline queue; "Open With" entry; Receiving into `~/Downloads/Palwyn` and `~/Pictures/Palwyn`, using `xdg-user-dir` for the localized names.
- [ ] Contacts: browse, search, add/edit/delete, photos.
- [ ] Clipboard phone to PC: always works through the explicit send. PC to phone, automatic: Avalonia clipboard on X11; on Wayland only where the compositor allows background reads (KDE and wlroots via `wl-paste --watch`/data-control; **not** stock GNOME, where the setting is disabled with an explanation and a manual "Send clipboard" button remains). Password-manager hints (`x-kde-passwordManagerHint`) skipped.
- [ ] `SafeName` Linux cases (Review Focus 5).

**Exit check (user, on device):** a photo saves to Pictures; a file and a folder go both ways; a text copied on the PC arrives on the phone (X11 or KDE) or the setting honestly says it can't (GNOME Wayland).
**Won't work yet:** automatic clipboard sync on GNOME Wayland; Windows "Share" integration (replaced by the file-manager entry).
**Size / effort:** L. About 400 to 700k tokens.

**STOP: wait for the user's approval before Phase 7.**

---

## Phase 7: PC control

**Goal:** The phone as remote, plus desktop integration that depends on Linux services. Each item is independent, so any can be cut.

**Files:**
- Create: `Platform/Mpris.cs` (org.mpris.MediaPlayer2 players: state, play/pause/next/previous), `Platform/Volume.cs` (parse `pactl get-sink-volume/get-sink-mute @DEFAULT_SINK@`; works on PulseAudio and PipeWire's pulse layer; no libpulse binding), `Platform/Lock.cs` (`org.freedesktop.login1.Session.Lock`), `Platform/KeepAwake.cs` (portal Inhibit, else login1 `Inhibit("idle:sleep")` fd held while connected), `Platform/RemoteInput.cs` (X11: XTest via `libXtst` P/Invoke; Wayland: RemoteDesktop portal `NotifyPointerMotion`/`NotifyKeyboardKeycode`, asks consent once and keeps a restore token), `Platform/RunCommands.cs` (`/bin/sh -c`, as the user, no window), `Platform/CallMediaPause.cs` (MPRIS pause and resume), `Platform/PcNotificationForwarder.cs` (last)
- Test: `MprisParseTests.cs`, `VolumeParseTests.cs`

**Tasks:**
- [ ] Settings switches matching Windows ("Mouse and keyboard" off by default; "Music, volume and lock" on).
- [ ] `PC_REMOTE` state reports `input:false` honestly when neither XTest nor the portal is usable.
- [ ] Touchpad, typing (Unicode via keysym), presentation keys, media, volume, mute, lock, commands.
- [ ] Keep PC awake and pause media during calls.
- [ ] PC notifications to phone: D-Bus `BecomeMonitor` on the session bus for `Notify` calls, text only. Cut it if it is unreliable.

**Exit check (user, on device):** touchpad, typing and slides work on an X11 session and on GNOME Wayland (portal consent appears once) or the Remote screen says why not; media/volume/lock work; `systemd-inhibit --list` shows Palwyn while connected.
**Won't work yet / limits:** no remote input on the login screen or lock screen; Wayland remote needs the portal (GNOME and KDE have it; some compositors don't); `uinput` is not used (needs a root udev rule). PC-to-phone notification forwarding will not work inside Flatpak (monitoring is blocked). Media pause works only for MPRIS players (most browsers and players, not all).
**Size / effort:** M. About 300 to 600k tokens; Wayland input is the risky part.

**STOP: wait for the user's approval before Phase 8.**

---

## Phase 8: Phone screen, USB link, emergency screen, Rescue files

**Goal:** The adb-based and mirroring features, mostly reuse.

**Files:**
- Move to Core (if still in the app after Phase 3): `Emergency/EmergencySession.cs`, `Emergency/Rescue.cs`, `ScreenSources.cs`; they already use no Windows API (`IScreenSource`, `LinkScreenSource`, `EmergencyScreenSource`)
- Create: `tools/linux/fetch-deps.sh` (pinned platform-tools **linux** zip with SHA-256 filled in when this phase is planned, plus the emergency jar from `apps/android/emergency`); `Windows/ScreenWindow.axaml` (image view, click/drag/wheel/key mapping to the existing 0 to 10000 touch units, rotation); `Windows/RescueWindow.axaml`; `Platform/UsbWatcher.cs` (`adb track-devices`, no udev dependency)
- Modify: adb discovery splits PATH on `Path.PathSeparator` and looks for `adb` (not `adb.exe`); `release.yml` Linux job runs `fetch-deps.sh`; `NOTICE` (platform-tools and scrcpy already listed)

**Tasks:**
- [ ] Phone screen with Android's consent, then control; "Keep screen sharing ready".
- [ ] USB link: forward 47801 to the phone, switch to the cable and back, as on Windows.
- [ ] Emergency screen and Rescue files over the cable; "Emergency access: ready" in Settings; the unreachable-phone hint as a notification.
- [ ] Device access note: if `adb devices` shows `no permissions`, Settings explains the udev rule (`android-udev-rules` / distro `adb` package) instead of failing silently.

**Exit check (user, real Linux box or VM with USB pass-through):** plug in a phone with USB debugging, open the emergency screen with no prompt on the phone, wake it, unlock and tap; Rescue copies a test folder byte-identical and a second run skips it.
**Won't work yet:** phone screen sound (none, as on Windows); wireless emergency access (as on Windows); a VM without USB pass-through can't run this check.
**Size / effort:** M. About 300 to 500k tokens, mostly the Avalonia screen window (input mapping, rotation).

**STOP: Linux port feature-complete; user decides on Flatpak and the release.**

---

## Packaging, release and docs

**Release integration (`.github/workflows/release.yml`, tag `v*`):**
- Phase 4 adds a `linux` job beside `windows` and `android`: `dotnet test` of Core, `dotnet publish -r linux-x64 --self-contained`, `Palwyn-<version>-linux-x64.AppImage` and `.tar.gz` into `dist/`. The `release` job's `sha256sum *` and draft release already pick up every file in `dist/`. Add `needs: [windows, android, linux]`.
- AppImage needs FUSE 2 on some distros; the tarball is the fallback. Self-contained (no .NET install for the user); the AppImage bundles nothing that is not MIT/Apache/BSD/LGPL-dynamic.
- arm64 (`linux-arm64`): not planned; add a matrix entry if asked.
- Flatpak (optional, after Phase 8, own plan): sandbox args `--share=network`, `--socket=wayland --socket=fallback-x11`, Avahi system-bus access, `--talk-name` for Notifications/MPRIS/login1, and a bundled adb with `--device=all` for the cable. Not available inside it: PC-notification forwarding, host `pactl` unless the pulse socket is shared. Flathub review of an app that runs shell commands and adb needs extra justification.

**Docs to update (in the phase that ships the feature):**
- `README.md`: Linux as a supported platform once Phase 4 ships, with the Wayland/GNOME limits in one short table.
- `docs/install.md`: a Linux section (download, `chmod +x`, run; AppImage/FUSE note; uninstall by deleting the file and `~/.config/palwyn`, `~/.local/share/palwyn`; Avahi and AppIndicator notes).
- `docs/feature-matrix.md`: a "Linux" column, or one status line per row, written only from device-verified results; unsupported rows say why.
- `docs/architecture.md`: repo layout (`apps/linux`), `IPcHost` in the Phase 3 note, Linux row in the storage table, the Linux platform decisions table.
- `docs/security.md`: identity storage on Linux (0600 file, not hardware-bound), no change to the protocol rules; `docs/protocol.md`: `platform` enum.
- `docs/development.md`: Linux dev setup (bridged VM, `dotnet run`).
- `docs/draft-other-platforms.md`: mark the Linux section as in progress and link this plan.
- `NOTICE`: Avalonia (MIT), Tmds.DBus (MIT), platform-tools Linux (Apache-2.0, already listed for Windows), any later library found by `dotnet list package` per phase; JetBrains Mono OFL already listed.
- `docs/releases/vX.Y.Z.md`: lists Linux as new, with known limits.

**Self-review notes:**
- Spec coverage: every Windows feature row maps to a phase (calls, messages, notifications 5; photos, files, clipboard, contacts 6; remote, media, volume, lock, keep awake, PC notifications 7; screen, USB, emergency, rescue 8; tray, settings, autostart, pairing 4; discovery, identity, notifications 2).
- Not planned: macOS, Bluetooth transport, Windows UI rewrite, `.deb`/`.rpm`, libsecret.
- Correction to the brief: the Android app is **not** entirely unchanged; the one-line `platform` enum change (Decision 4) is required.
