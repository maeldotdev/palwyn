# Draft: other platforms (batch 7)

Status: **idea, not started** (2026-09-29). The user wants it "soon"; research first, like batch 8. Nothing below is built.

Today Palwyn is an Android phone app plus a Windows PC app. Batch 7 takes it further. There are three possible targets.

## 1. Linux PC app (recommended first)

**What carries over:** `Palwyn.Core` (.NET) is reused unchanged:
- link engine, TLS pinning and pairing
- protocol catalog and validation
- notification history and search

The Android app needs no changes.

**What's new:**
- **UI:** the WinUI 3 app is Windows-only, so the pages move to **Avalonia** (MIT, runs on Linux and macOS).
- **Linux equivalents** for the Windows-specific parts:

| Windows today | Linux |
|---|---|
| Toasts (with reply) | Freedesktop notifications over D-Bus (actions supported; inline reply depends on the desktop) |
| Tray icon and popup | StatusNotifierItem (KDE, GNOME with the AppIndicator extension) |
| SendInput (remote, batch 5) | X11: XTest. Wayland: `uinput` / ydotool, or the RemoteDesktop portal (asks the user). Harder |
| GlobalSystemMediaTransportControls | MPRIS over D-Bus |
| Core Audio volume | PipeWire / PulseAudio (`wpctl` or libpulse) |
| UserNotificationListener (PC notifications to phone) | D-Bus monitor of `org.freedesktop.Notifications` (desktop-dependent) |
| mDNS discovery | Avahi (or the built-in mDNS code if it works there) |
| Credential store for the identity key | libsecret / a file with 0600 permissions |
| LockWorkStation | `loginctl lock-session` |
| MSIX, startup task | Flatpak or AppImage, XDG autostart |

- **Cost:** ₱0.
- **Effort:** large, about three batches (UI port, platform services, packaging and testing).
- **Note:** KDE Connect already serves Linux well; Palwyn's value there is its protocol and features being the same as on Windows.

## 2. macOS PC app

- **Reuse:** the same Avalonia UI and Core, so it's much cheaper right after Linux.
- **Platform services:**
  - UNUserNotificationCenter for notifications
  - CGEvent for input (needs the user's Accessibility permission)
  - a menu-bar item for the tray
  - Bonjour for discovery (built in)
- **Now playing:** there's no public API for what's playing, only the private MediaRemote framework, which Apple can block. Media control may have to be limited to media keys.
- **Cost:** Developer ID and notarization need an Apple Developer account at **US$99/year (about ₱5,600)**. Without it, users have to bypass Gatekeeper to open the app.
- **Testing:** needs a Mac.

## 3. iPhone app (not recommended now)

**iOS blocks most core features:**
- No reading or sending SMS.
- No answering or declining calls, and no call history.
- No reading other apps' notifications from an app. Phone Link does it over Bluetooth ANCS, which would be a second transport on the PC.
- No clipboard in the background.
- Strict background limits, so the link drops shortly after leaving the app.

**What's possible:**
- photos and files (PhotoKit, share sheet)
- manual clipboard send
- battery level
- the PC remote (batch 5)
- screen to the PC (ReplayKit broadcast extension)

**Cost:**
- A Mac to build it.
- **US$99/year** for the App Store; free sideloading must be re-signed every 7 days.

**Effort:** very large for a small feature set.

## Plan when we start

1. Research note with sources (like `research-screen-mirroring.md`). Confirm:
   - Avalonia on Linux and macOS
   - D-Bus notification actions
   - Wayland input options
   - that the Core's TLS pinning works with OpenSSL on Linux
2. **Linux app:**
   - move the pages to Avalonia
   - write Linux versions of the platform services behind small interfaces, which the Windows app then uses as well
   - package as Flatpak or AppImage
3. **macOS:** only if the user accepts US$99/year or unsigned builds.
4. **iPhone:** revisit later.

## Open decisions

- Linux only, or Linux and macOS together?
- Whether the Windows app also moves to Avalonia: one UI, but a big rewrite and a loss of WinUI polish (Mica, native look). Or it keeps WinUI 3 while only Core and the service interfaces are shared.
- Pay US$99/year for Mac signing, or ship unsigned.
