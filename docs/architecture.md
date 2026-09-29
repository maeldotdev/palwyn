# Palwyn — Architecture

Status: Phase 1 · Constraints come from [phase-0-feasibility.md](phase-0-feasibility.md).

## 1. System overview

```
┌──────────────── Android (Kotlin) ─────────────────┐        ┌──────────────── Windows (C#/.NET) ─────────────────┐
│ Compose UI (pairing, permissions, status)         │        │ WinUI 3: tray popup · dashboard · call popup        │
│                                                   │        │                                                     │
│ LinkService  (foreground service, connectedDevice)│  TLS   │ Palwyn.Core                                     │
│  ├ Server: TLS listener + DNS-SD advert  ◄────────┼─1.3────┼─ Client: DNS-SD browse + connect + reconnect        │
│  ├ Session: frames, HELLO, heartbeat              │  LAN   │  ├ Session: frames, HELLO, heartbeat                 │
│  └ Feature handlers                               │        │  └ Feature services                                  │
│     calls · sms · notifications · media · device  │        │     calls · messages · notifications · device        │
│                                                   │        │                                                     │
│ Android APIs: Telecom, Telephony, SMS provider,   │        │ Windows APIs: Shell_NotifyIcon, App Notifications,  │
│ NotificationListener, MediaSession, MediaStore    │        │ SQLite                                              │
└───────────────────────────────────────────────────┘        └─────────────────────────────────────────────────────┘
```

No server, no cloud, no account. Everything travels phone ↔ PC. Call **audio** never reaches the PC (Phase 0 §7: Windows reserves the HFP phone-line API for Microsoft); calls are controlled from the PC and spoken on the phone.

## 2. Repository layout

```
Palwyn/
  apps/
    windows/
      Palwyn.sln
      src/Palwyn.Core/          net10.0 library — protocol, link, pairing, feature logic. No UI, no WinRT.
      src/Palwyn.App/           WinUI 3 packaged app — tray, windows, Windows integrations
      tests/Palwyn.Core.Tests/  xUnit
    android/
      app/                           single module: link/, features/, ui/
  shared/
    protocol/test-vectors.json       frames both platforms must accept/reject identically
  docs/
  spikes/                            throwaway Phase 0 prototypes, never shipped
```

Why this split, and no more:
- **Core vs App on Windows** — the link engine, pairing and protocol are the riskiest logic; keeping them free of WinUI lets them run under plain `dotnet test`, including real TLS over loopback.
- **One Android module** — package boundaries (`link`, `features.*`, `ui`) are enough until build times or reuse say otherwise.
- **No shared code across platforms** — C# and Kotlin can't share a library without a heavy toolchain. The protocol is shared as a spec ([protocol.md](protocol.md)) plus executable test vectors.

## 3. Connection topology

**Phone = TLS server, PC = TLS client.**

- Windows Firewall prompts for, and on "Public" networks blocks, inbound listeners. Android has no inbound firewall. Letting the PC dial out avoids both.
- Phone registers `_palwyn._tcp` via `NsdManager` with TXT `id=<device id>`; PC browses with Windows DNS-SD and also tries the last known address first (fast path, and a fallback when mDNS is flaky).
- After the TLS handshake both sides are symmetric peers: either can send requests or events.
- Bulk data (photos, files, from Phase 9) uses **separate TLS connections** to the same listener, tagged with a transfer ID, so a 2 GB video never blocks a call event on the control channel.

Revisit if: the phone's server is unreachable too often on real networks (OEM process killing). The flip is contained in `link/`.

## 4. Connection state machine (both sides)

```
          ┌──────────── network change / app start / user "retry" ────────────┐
          ▼                                                                   │
  Idle ─► Discovering ─► Connecting ─► Handshaking ─► Connected ─► (lost) ─► Backoff
                              │              │              │                  │
                              └──── fail ────┴── fail ──────┘                  │
                                             │                                 │
                                  auth/trust failure ─► Unauthorized (terminal until user acts)
```

Implemented in `Palwyn.Core/Link/LinkEngine.cs` (PC) and `LinkServer.kt` (phone).

- **Backoff**: 1 s, 2 s, 4 s … capped at **30 s**, ±20 % jitter; connect timeout 6 s. Skipped immediately (`Kick`) on `NetworkInformation.NetworkStatusChanged`, Windows resume (`PowerManager.SystemSuspendStatusChanged`), and DNS-SD rediscovery of the phone (also mid-attempt).
- **No infinite loops on permanent errors**:
  - phone presents a certificate other than the pinned one → `Unauthorized` at once;
  - 3 consecutive "TLS completed, dropped before HELLO" results (how TLS 1.3 reports a rejected client certificate) → `Unauthorized`;
  - no protocol overlap → `Incompatible`.
  These stop the engine; the UI shows *Needs attention* with the fix. Timeouts never count as rejections (a frozen phone isn't a revoked one).
- **Liveness + wake**: the PC sends `PING` every *keepAlive* seconds, which the phone requests in HELLO: **3 s** on OEMs whose freezer suspends background apps (realme, OPPO, OnePlus, Xiaomi/Redmi/POCO, vivo/iQOO, Huawei/Honor, Meizu, Tecno/Infinix/itel), 15 s otherwise. The PC drops a session after 3 silent intervals; the phone closes one after 3 intervals + 5 s without a frame and re-advertises.
- **Staying awake**: pings alone weren't enough. After a few idle minutes with the screen off, Wi-Fi power save and CPU suspend made the phone miss pings, and the link dropped until the phone woke. While a PC is connected the phone now holds a partial wake lock and a high-performance Wi-Fi lock (`KeepAwake`), released as soon as no PC is connected. The battery-optimization exemption is what lets the wake lock hold in Doze. Cost: more battery drain while connected; Phase 16 measures it.
- Windows sleep: drop on "Entering", retry on resume. Phone Wi-Fi regained: re-register DNS-SD.

Measured on the reference devices (Phase 5):

| Event | Detected | Back connected |
|---|---|---|
| Phone app killed | 0.1 s | 1.8 s after the app restarts |
| Phone Wi-Fi off/on | ≤ 9 s (watchdog) | when Wi-Fi returns |
| PC sleep/wake | at suspend | 3.4 s after wake |
| Phone restart | ≤ 9 s | after boot + unlock (was up to 35 s behind; fixed by kick-on-rediscovery and the 30 s cap) |
| Phone forgot this PC | — | stops after 3 rejections (3.1 s), asks to re-pair |
| 3 min screen off on realme | no drops | — |

Heartbeat cost on the phone at 3 s: 0.2 % of one CPU core while idle (0.27 s CPU per 2 min). Radio/battery drain still to be measured unplugged (Phase 16).

## 5. Feature modules

Each feature = a message handler on each side + a capability flag. A feature only turns on when both sides advertise its capability in `HELLO` (e.g. the phone only advertises `sms.send` if `SEND_SMS` is granted). The UI reads capabilities to show a working control, a "grant permission on your phone" state, or nothing.

| Module | Android side | Windows side |
|---|---|---|
| device | Build info, battery broadcast, StatFs | Dashboard/tray status |
| calls | Phone-state receiver (+`READ_CALL_LOG` for number) → `CALL_STATE`; `TelecomManager.acceptRingingCall/endCall` | Call popup (caller, answer/decline/end), history |
| messages | SMS provider read, `SMS_RECEIVED`, `SmsManager` send | Conversation list/view/composer, SQLite cache |
| notifications | `NotificationListenerService`, dismiss, actions, `RemoteInput` | Notification center, per-app filters, Windows toasts |
| media | `MediaSessionManager` sessions (Phase 12) | Now playing + controls in the flyout and on Home |
| actions | One message per action (`RING`, …) | `QuickActions` registry rendered by the flyout and Home |
| transfer (V1) | MediaStore, share target, SAF/all-files | Gallery, explorer, Quick Drop window |
| clipboard (V1) | Write on receive; explicit "Send to PC" | Clipboard listener honouring exclusion formats |

### Calls (Phase 6)

- Phone: `calls/Calls.kt` turns the PHONE_STATE broadcast (registered by the link service) into one current call and sends `CALL_STATE`; answer/decline/end go through `TelecomManager`. History is a live `CallLog` query per request.
- PC: `CallWindow` is a small acrylic card in the bottom-right corner of the primary monitor, topmost and shown without taking focus, so it never steals typing. It hides itself 4 s after the call ends, when the link drops, or when the user hides it (for the rest of that call). `CallsPage` lists the call log grouped by day, 50 rows at a time.
- The card never offers PC audio: while a call is active it says audio stays on the phone.

Measured on the reference devices (screen on):

| Step | Time |
|---|---|
| Phone starts ringing → card on PC | ≈ 0.1 s (number follows 50 ms later) |
| Answer clicked → call active | 0.19 s |
| End clicked → call ended | 0.03 s |

With the screen off on realme the alert rides the 3 s keep-alive (Phase 0: ≈ 3 s).

### Messages (Phase 7)

- Phone: `messages/Messages.kt` watches the SMS store with a ContentObserver (only `READ_SMS`), reads conversations from `content://mms-sms/conversations` and messages from `content://sms`, and sends with `SmsManager`, waiting for the radio's sent result.
- PC: `MessagesPage` (conversations, bubbles, composer), `Toasts` (Windows App SDK app notifications with a reply box; the package manifest registers the COM activator so a click works after Palwyn closed). No SQLite cache: conversations load live, like calls. Add one if offline reading becomes a need.
- The composer shows when a message goes out as several texts (`SmsLength`, 3GPP TS 23.038): one emoji switches the whole SMS to UCS-2, 70 characters per text.
- Emoji come from the Windows emoji panel (Win+.), opened by the composer's button.

### Notifications (Phase 8)

- Phone: `notifications/Mirror.kt` with `MirrorListener` (a `NotificationListenerService` the user enables in Settings). App names and icons come from `PackageManager`, visible for launcher apps through the manifest's `<queries>`.
- PC: `NotificationsPage` mirrors the shade live (cards with actions, reply box, dismiss, right-click to turn an app off). `Toasts` shows new ones as Windows notifications with reply and action buttons, replaced when the phone updates them and removed when the phone removes them. `AppIcons` caches icons as PNG files.
- No history on the PC: the list is exactly the phone's shade and is cleared when the link drops (the phone resends it on reconnect).

### Photos (Phase 9)

- Phone: `photos/Photos.kt` lists MediaStore images newest first, makes 320 px previews with `loadThumbnail`, and streams originals on transfer connections (`LinkServer.transfer`).
- PC: `PhotosPage` (grid with previews loaded as tiles appear, at most 4 at a time; open; multi-select save; cancel). `LinkManager.DownloadPhotoAsync` opens the extra connection through `LinkEngine.OpenConnectionAsync`, so calls and messages keep flowing during a large copy.
- Measured: 0.7 to 4.4 MB originals in 94 to 250 ms (about 20 MB/s) on the reference Wi-Fi.

### Quick Drop (Phase 10)

- PC to phone: `QuickDropWindow` (drop, pick, type; also opened by the Windows Share target and the tray's "Send files to phone"). Items queue while the phone is away. Files go by `TRANSFER_PUSH` into `Download/Palwyn` (MediaStore, no permission); text and links by `DROP_TEXT` into a phone notification (`drop/Drops.kt`).
- Phone to PC: `ShareActivity` ("Send to PC" in Android's share sheet) offers the items (`DROP_OFFER`) and stays open with progress, because read access to shared files lasts only while it's open. `Receiving` on the PC pulls each file into the save folders chosen in Settings (photos and videos to Pictures\Palwyn, everything else to Downloads\Palwyn, by default) and notifies; text and links get Copy / Open.
- Measured: share to PC 0.1 s end to end for 0.9 to 1.3 MB; 120 KB to the phone in 0.16 s.

### Clipboard and links (Phase 11)

- PC to phone: `ClipboardSync` (PC) listens for `WM_CLIPBOARDUPDATE` on the tray window and sends each text copy as `CLIPBOARD_SET`, only while "Send text I copy on this PC to my phone" is on (off by default; also a quick switch in the tray flyout). Copies carrying Windows' private formats (password managers) are skipped. The phone writes it to its clipboard (allowed from the background) and shows a short "Copied from your PC" toast.
- Phone to PC: Android 10+ lets only the focused app or the keyboard read the clipboard, so there is no automatic direction. The user sends explicitly: "Send to PC" on selected text (`ACTION_PROCESS_TEXT`, no clipboard read at all), or "Send clipboard to PC" (Quick Settings tile, status notification, home screen), which reads it while a Palwyn screen has focus. The PC puts it on its clipboard and shows a silent notification with a preview (and Open for a link).
- Device control: the phone's Settings switch "Share clipboard with your PC" drops the `clipboard` capability, so the PC stops sending and the phone refuses both directions.
- Links (the master plan's "deep links"): Quick Drop already carries them both ways. PC to phone: Share from a browser to Palwyn, or paste in the Send to phone window, and the phone shows a notification that opens the link. Phone to PC: Share to "Send to PC", and the PC's notification has Open and Copy.
- Echo guard: each side remembers the last text it sent or received, so a copy never bounces back.

### Dashboard and Quick Actions (Phase 12)

- Home is the dashboard: connection (since when / last connected), battery, Wi-Fi signal, Bluetooth, storage, phone model and Android version, plus recent activity (calls, texts, files, clipboard sends, connects; memory only, last 30). Values come from `DEVICE_INFO` (once) and `DEVICE_STATUS` (on change), all event-driven on the phone: battery broadcast, network callback, Bluetooth state broadcast; free storage is re-read after a file arrives.
- Wi-Fi shows signal only: the network name needs location permission, which Palwyn doesn't ask for.
- Quick Actions: `QuickActions.All` on the PC is the only list; the tray flyout and Home render whatever the phone's capabilities allow, so a new action is one entry plus, if it needs the phone, one message. Today: Ring phone, Send clipboard (one copy, even with sync off), Send files (also offline: Quick Drop queues), Latest photo (saved to Pictures\Palwyn and opened).
- Media: `media/Media.kt` follows the phone's media sessions (needs notification access) and sends `MEDIA_STATE`; `PhoneControls` shows now playing with previous / play-pause / next and volume in the flyout and on Home.
- Not built, by platform limit: actions that open a screen on the phone (gallery, downloads, a conversation, an app). Android 10+ blocks starting activities from the background; the only exemptions are a visible window, "display over other apps", or a CompanionDeviceManager association. Revisit in V2 ("advanced device controls") if the companion-device route proves workable.

### Visual system (Phase 13)

- One product, two native languages. Windows: Fluent (Mica window, Acrylic tray popup, Segoe UI Variable, the user's Windows accent, `OverlayCornerRadius` for cards and tiles, `ControlCornerRadius` for controls). Android: Material 3 (dynamic color on Android 12+, a cobalt fallback before that, `extraLarge` hero, `large` cards). Shared: the phone glyph, the words ("Calls on your PC", "Photos and videos on your PC"), and the rule that every screen explains its empty, offline and permission states.
- `StateView` + `PageState` (PC): one look for offline / needs permission / empty / failed on Calls, Messages, Photos and Notifications. The permission state names the exact row on the phone's Home.
- `Skeleton` (PC): list rows or photo tiles shaped like the content while it loads the first time; a slow pulse, still when Windows animations are off. Refreshes keep the content and show the thin bar.
- Home (PC): now playing and quick actions, a 3-column status grid, recent photos (six, fetched once per connection), the newest three notifications, recent activity. Unpaired Home is the onboarding: what Palwyn does and Add a phone.
- Tray popup: status, media, actions and the clipboard switch; "Open Palwyn" is a footer link, and the accent button appears only when there's something to fix (no phone, blocked).
- Settings (PC): Your phone (name, ID, remove) with the security line, and a Privacy section. Android Settings mirrors it.
- Android Home: status hero (tinted when connected) with Send clipboard and Send files (a file picker feeding the Share screen), one permissions list with Allow or a check, and the phone's facts in a 2 by 2 grid.
- Icons: Segoe Fluent Icons on Windows; Material Symbols (`material-icons-extended`, unused ones removed by R8) on Android. No hand-drawn icons.

## 6. Storage

| Data | Windows | Android |
|---|---|---|
| Own identity key + cert | CNG persisted key, non-exportable, CurrentUser\My store | Android Keystore (non-exportable) |
| Paired peers (id, name, cert fingerprint, last address) | JSON file in package LocalState | SharedPreferences (a handful of rows) |
| Settings | Package LocalSettings | SharedPreferences |
| Message history | not stored: read from the phone on demand | the phone's SMS store |
| Notification history | none yet: the PC shows what's in the phone's shade now (history is a V1 feature) | none |
| App icons for notifications | PNG files in package LocalState/icons | none |
| Settings (theme, notification toggles, apps turned off, clipboard) | Package LocalSettings | SharedPreferences |
| Clipboard | never stored: only the last text in memory, for the echo guard | same |
| Call history | not stored: read from the phone on demand | the phone's call log |
| Photo thumbnails | memory only, for the current run (≤ 600) | none, MediaStore |
| Full photos/files | Only where the user saves them (Pictures\Palwyn), or the two save folders for things shared from the phone (photos and videos; other files); "Open" copies go to the package temp folder, emptied at every start | never copied; files from the PC go to Download/Palwyn |

Room is added on Android only if a feature needs relational phone-side state.

## 7. Logging

- Windows: `Microsoft.Extensions.Logging` → rolling file in local app data. Android: a thin wrapper over `android.util.Log`.
- **Never log**: message bodies, notification text, phone numbers (mask to last 2 digits), clipboard, file names from the phone, secrets, full fingerprints (first 8 hex only).
- Protocol frames are logged as `type` + `id` + size only.

## 8. Platform decisions

| Decision | Choice | Reason |
|---|---|---|
| Windows packaging | **MSIX (packaged)** | Package identity gives clean app notifications, `StartupTask`, Share target, Store distribution. Dev deploys as a loose package (Developer Mode, verified working on this PC). |
| Windows UI | WinUI 3 + Windows App SDK, .NET 10 | Native, Fluent materials (Mica/Acrylic). |
| Tray | `Shell_NotifyIcon` via P/Invoke, callbacks through a window-procedure subclass of the flyout window; left click, right click and keyboard select all open the same Acrylic flyout | WinUI has no tray API; no third-party dependency. One surface instead of a Win32 context menu, which can't follow the app's dark theme with documented APIs. |
| Main window lifetime | Created on demand, destroyed on close; the process stays alive via `DispatcherShutdownMode.OnExplicitShutdown` | Tray-first app; Quit is explicit. |
| Dropping files onto a window | XAML drop events plus classic `DragAcceptFiles`/`WM_DROPFILES` (`Win32.AcceptFileDrops`) | WinUI 3 registers no OLE drop target when the app runs elevated (verified on the dev PC, built-in Administrator account: no window had one), so XAML drops never fire there. The classic path handles files from Explorer in that case. |
| Single instance | `AppInstance.FindOrRegisterForKey` + activation redirect in a custom `Main` | A second launch opens the running copy's window. |
| Android min/target | minSdk 29, targetSdk 36 → 37 when released for Play | 29: `loadThumbnail`, TLS 1.3, scoped storage. 37 adds `ACCESS_LOCAL_NETWORK`. |
| Android background | One foreground service, type `connectedDevice` | No 6 h cap (unlike `dataSync`). |
| Caller ID | `PHONE_STATE` broadcast + `READ_CALL_LOG` | Verified on the reference phone; the screening role was granted but never invoked there. Needs a Play permission declaration. |
| Serialization | JSON (System.Text.Json / kotlinx.serialization) | Debuggable; the envelope allows a binary codec later without changing handlers. |
| DI | Constructor injection by hand | Two small apps; a container earns its place later or never. |

## 9. Future iOS

Nothing iOS-specific is built. The seam that matters is already there: Windows talks to "a peer that advertises capabilities", not to "Android". An iOS peer would simply advertise far fewer capabilities.
