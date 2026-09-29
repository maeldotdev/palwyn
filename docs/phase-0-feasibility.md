# Palwyn — Phase 0: Technical Feasibility

Date: 2026-09-27 · Status: **COMPLETE** (desk research, Windows HFP spike packaged + unpackaged, Android spike on real hardware)

Evidence sources: current AOSP `main` source (Telecom `TelecomServiceImpl`, `InCallController`, `RoleManagerAdapterImpl`; `frameworks/base` `AndroidManifest.xml`, `CallScreeningService`, `TelephonyManager`; PermissionController `roles.xml`), developer.android.com, learn.microsoft.com, and a Windows prototype run on this PC.

---

## 1. Verdict

Palwyn is feasible as a **third-party** product, with one hard architectural constraint:

> **Call control can travel over Wi-Fi. Call audio cannot. Call audio must use Bluetooth Hands-Free Profile (HFP) with the PC acting as the hands-free unit.**

Any Android app that is not a system/privileged app cannot capture cellular downlink audio (`CAPTURE_AUDIO_OUTPUT` is `signature|privileged|role`) and has no API to inject uplink audio. Relaying call audio over Wi-Fi is **not possible**; this is identified now so no architecture is built around it.

The second major finding: the Android privileges that make Microsoft Phone Link work well on some phones (`COMPANION_DEVICE_COMPUTER` role → sensitive notifications unredacted, etc.) are **`systemOnly`** and unavailable to Palwyn.

Everything else in the MVP is achievable with standard, user-granted permissions — but several need Google Play policy declarations.

---

## 2. Feature classification

Legend: **SUPPORTED** · **PARTIAL** · **DEVICE DEP.** · **VERSION DEP.** · **RESTRICTED** · **NOT PRACTICAL**

### Calls
| Feature | Class | Mechanism / limit |
|---|---|---|
| Detect ringing / call state | SUPPORTED | `READ_PHONE_STATE`, `TelephonyCallback` (API 31+) |
| Caller number | SUPPORTED (permission) | A) `CallScreeningService` + `ROLE_CALL_SCREENING` (+`READ_CONTACTS` so contacts are included). B) `ACTION_PHONE_STATE_CHANGED` + `READ_CALL_LOG` (deprecated but documented). Screening role is exclusive — would displace e.g. a spam-blocker app. |
| Caller name | SUPPORTED | `READ_CONTACTS` lookup |
| Answer | SUPPORTED | `TelecomManager.acceptRingingCall()` + runtime `ANSWER_PHONE_CALLS`. Deprecated since API 29 but **verified in Telecom `main`: no targetSdk gate**. Not for self-managed (VoIP) calls. |
| Decline / end | SUPPORTED | `TelecomManager.endCall()` (API 28+), same permission. Refuses emergency calls (correct). |
| Hold, DTMF, merge, per-call mute, audio route | RESTRICTED | Needs `InCallService`. Telecom only binds a non-dialer InCallService if it holds `MANAGE_ONGOING_CALLS` (`signature|appop`), granted only via the **watch** companion role. Registering a PC as a "watch" = misrepresentation → rejected. Becoming default dialer = replaces user's phone app → not practical. |
| Mute | DEVICE DEP. | Reliable path: mute the PC mic on the HFP link (PC side). Phone-side `AudioManager.setMicrophoneMute` is unreliable for non-dialer apps. |
| Call audio over Wi-Fi | **NOT PRACTICAL** | Platform-blocked (see §1). |
| Call audio via Bluetooth HFP → PC mic/speakers | **NOT PRACTICAL** | Hardware OK (`CanRouteToLocalDevice`) but Windows returns `DeniedBySystem` even packaged with `phoneLineTransportManagement` (§7). |
| Call history | PARTIAL | `READ_CALL_LOG` — Play-restricted permission (needs declaration). |
| Windows incoming-call UI | SUPPORTED | Windows App SDK app notification with incoming-call scenario, or own WinUI popup window. |

### Messages
| Feature | Class | Notes |
|---|---|---|
| Receive SMS | SUPPORTED | `RECEIVE_SMS`. Play-restricted (declaration; cross-device sync is a candidate exception — confirm at submission). |
| SMS history / conversations | SUPPORTED | `READ_SMS`, `content://sms`, `content://mms-sms/conversations` |
| Send SMS | SUPPORTED | `SEND_SMS`, `SmsManager`. System persists sent message for non-default apps. |
| MMS read | PARTIAL | `content://mms` parts readable; parsing is messy. |
| MMS send | PARTIAL / DEVICE DEP. | `sendMultimediaMessage` needs hand-built PDU; carrier-dependent. Defer. |
| RCS / chat features | **RESTRICTED** | No public third-party RCS API. Closest legitimate: mirror Google Messages notifications and reply via their `RemoteInput` action. No RCS history. Never advertise "RCS support". |
| Quick reply (any messaging app) | SUPPORTED | Notification `RemoteInput` actions, when the app exposes them. |

### Notifications
| Feature | Class | Notes |
|---|---|---|
| Mirror notifications | SUPPORTED | `NotificationListenerService`, user grants in Settings. |
| Sideloaded APK | VERSION DEP. | Android 13+ "Restricted settings" blocks enabling notification access for sideloaded apps until user taps *Allow restricted settings*; stricter in 15. Play-installed apps unaffected. Onboarding must handle this. |
| OTP / sensitive content | **RESTRICTED** (Android 15+) | Redacted for listeners without `RECEIVE_SENSITIVE_NOTIFICATIONS` (`signature|role`, only system roles). Palwyn will show "sensitive content hidden". |
| Dismiss | SUPPORTED | `cancelNotification(key)` |
| Actions / reply | SUPPORTED | Fire the notification's actions / `RemoteInput`. |
| Open app on phone from PC | RESTRICTED | Background activity starts blocked; companion association is **not** a documented exemption. Closest: post a Palwyn notification on the phone the user taps. Firing another app's `contentIntent` from background is version-dependent — test. |

### Photos / files / transfer
| Feature | Class | Notes |
|---|---|---|
| Browse photos/videos, albums, thumbnails | SUPPORTED | MediaStore, `READ_MEDIA_IMAGES`/`VIDEO` (13+), `loadThumbnail` (29+). Android 14 partial "selected photos" access must be handled. |
| PC → phone upload | SUPPORTED | MediaStore insert (no permission for own files). |
| Delete from PC | PARTIAL | `createDeleteRequest` (30+) forces a confirmation on the phone per batch. |
| Photo permission on Play | POLICY RISK | Play photo/video policy limits broad media access to core use cases. |
| Full storage explorer | PARTIAL / POLICY RISK | `MANAGE_EXTERNAL_STORAGE` ("All files access", user toggle) works; Play allows it only for qualifying apps. No access to `Android/data`, `Android/obb` (11+). Fallback: SAF folder grants (root and Download cannot be granted on 11+). |
| Quick Drop / share | SUPPORTED | Android `ACTION_SEND` share target; Windows drop window. Windows Share-sheet target needs a packaged (MSIX) app. |

### Clipboard, media, device
| Feature | Class | Notes |
|---|---|---|
| PC → phone clipboard | SUPPORTED (verify) | Writes allowed; Android 13+ shows a system toast/preview. |
| Phone → PC automatic clipboard | **RESTRICTED** | Android 10+: only the focused app or default IME may read. Closest: "Send to PC" share target + Quick Settings tile/notification action that briefly opens a transparent activity on user tap. |
| Windows clipboard watch | SUPPORTED | Must honour `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInCloudClipboard=0` set by password managers. |
| Media control + now playing | SUPPORTED | `MediaSessionManager.getActiveSessions` (needs notification-listener grant). |
| Battery, charging, model, Android version, storage | SUPPORTED | Sticky battery broadcast, `Build`, `StatFs`. |
| Wi-Fi SSID | PARTIAL | Requires location permission — skip or make opt-in. |
| Ring my phone | SUPPORTED | Alarm stream; overriding DND needs user-granted `ACCESS_NOTIFICATION_POLICY`. |

### Platform plumbing
| Feature | Class | Notes |
|---|---|---|
| Android always-on connection | SUPPORTED / DEVICE DEP. | Foreground service type `connectedDevice` (persistent notification, no 6 h cap unlike `dataSync`). Aggressive OEM battery managers (Xiaomi, Huawei, some Samsung) are device-dependent — onboarding must guide battery-optimisation exemption. |
| Companion association (PC's Bluetooth identity) | SUPPORTED (verify) | Plain CDM association (no profile, normal permission). Enables `REQUEST_COMPANION_RUN_IN_BACKGROUND` / `…START_FOREGROUND_SERVICES_FROM_BACKGROUND` (normal) and presence callbacks when the PC is in Bluetooth range. Legitimate because the user really pairs the PC over Bluetooth for HFP. |
| Local Wi-Fi transport | SUPPORTED / VERSION DEP. | Android 17 (targetSdk 37) requires runtime `ACCESS_LOCAL_NETWORK` (Nearby devices group) for **all** LAN traffic incl. mDNS, NSD, inbound/outbound TCP, UDP. Mandatory once we target 37. |
| Discovery | SUPPORTED | DNS-SD/mDNS both sides; QR code carries address as fallback. Guest/hotspot networks with client isolation block LAN → Bluetooth fallback. |
| Bluetooth RFCOMM fallback | SUPPORTED | Low bandwidth: control channel only, not photos/files. |
| Windows tray | SUPPORTED | WinUI 3 has no tray API: `Shell_NotifyIcon` via P/Invoke or H.NotifyIcon.WinUI (MIT). |
| Windows startup | SUPPORTED | `StartupTask` (packaged) or HKCU Run key (unpackaged). |
| Windows firewall | RISK | Inbound listener on PC triggers firewall prompt; blocked on "Public" profile. Recommendation: phone listens, PC connects outbound. |

---

## 3. Call pipeline — the feasible design

```
Phone rings
  ├─ Wi-Fi (TLS): CALL_INCOMING {number, contact name}  ──► Windows popup
  │                    ◄── CALL_ANSWER / CALL_DECLINE ─┘
  │   phone: TelecomManager.acceptRingingCall() / endCall()
  └─ Bluetooth HFP (system-managed): voice audio ◄──► PC mic + speakers/headset
      Windows: PhoneLineTransportDevice.Connect() when user answers on PC
```

Degraded modes (must be first-class, not errors):
1. HFP unavailable → answer/decline/end from PC, talk on the phone.
2. PC Bluetooth off → notification + decline only.

Open questions only a real device can answer: does Windows accept the phone as a phone-line transport on this adapter; audio quality; conflict with Microsoft Phone Link (preinstalled on Windows 11) claiming the same transport; behaviour when the UGREEN headset is also connected.

---

## 4. Architecture recommendations

- **Android**: Kotlin + Compose, `minSdk 29` (Android 10: `loadThumbnail`, TLS 1.3, scoped-storage baseline), `targetSdk 36`, plan for 37 (`ACCESS_LOCAL_NETWORK`).
- **Windows**: C#/.NET 10, WinUI 3 + Windows App SDK, SQLite. Packaging decision deferred to Phase 1 (MSIX gives identity for share target/startup/toasts; unpackaged is simpler for dev).
- **Transport**: TCP over LAN, TLS 1.3 both ends; phone advertises via DNS-SD and listens; PC connects out. Bluetooth RFCOMM control fallback later.
- **Protocol**: length-prefixed JSON frames `{v, id, type, ts, payload}`, capability negotiation on connect, heartbeat.
- **Security (proposed, for approval — no custom crypto)**:
  - Each device generates a long-lived EC P-256 identity key (Android Keystore, non-exportable; Windows CNG key, user-scoped) and a self-signed certificate.
  - Pairing: PC shows QR = {address, PC cert SHA-256 fingerprint, one-time 128-bit secret, expiry}. Phone connects with mutual TLS, checks PC fingerprint, then proves knowledge of the secret with HMAC over both certificate fingerprints (binds the secret to this TLS session → MITM fails). Both store the peer fingerprint.
  - No-camera fallback: 6-digit code shown on both screens, derived with a commit-then-reveal exchange (Bluetooth numeric-comparison style) so an attacker cannot brute-force it.
  - Every later connection = mutual TLS with pinned fingerprints; unknown certs rejected before any app data. TLS handles confidentiality, integrity and replay; app layer adds message IDs + strict schema validation.
  - Revoke = delete pinned fingerprint on either side; the other side's next connect fails.
  - No message/notification/clipboard content in logs; SQLite caches hold only what the UI needs.

---

## 5. Permissions (Android)

| Permission | Type | For | Play policy |
|---|---|---|---|
| `READ_PHONE_STATE` | runtime | call state | — |
| `ANSWER_PHONE_CALLS` | runtime | answer/end | — |
| `READ_CONTACTS` | runtime | names, screening | — |
| `ROLE_CALL_SCREENING` or `READ_CALL_LOG` | role / runtime | caller number | call-log is restricted |
| `RECEIVE_SMS`, `READ_SMS`, `SEND_SMS` | runtime | messages | restricted (declaration) |
| Notification access | special (Settings) | notifications, media | — |
| `READ_MEDIA_IMAGES`, `READ_MEDIA_VIDEO` | runtime | photos | photo/video policy |
| `MANAGE_EXTERNAL_STORAGE` | special | file explorer | restricted |
| `BLUETOOTH_CONNECT`, `BLUETOOTH_SCAN` | runtime | HFP, CDM, RFCOMM | — |
| `ACCESS_LOCAL_NETWORK` | runtime (API 37) | LAN | — |
| `POST_NOTIFICATIONS` | runtime | FGS + phone-side prompts | — |
| `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_CONNECTED_DEVICE` | normal | background link | FGS type declaration |
| `REQUEST_COMPANION_RUN_IN_BACKGROUND`, `…START_FOREGROUND_SERVICES_FROM_BACKGROUND` | normal | reconnect | — |
| `ACCESS_NOTIFICATION_POLICY` | special | ring through DND | — |

---

## 6. Risks (ranked)

1. **HFP audio on the user's PC adapter** — unverified; product's headline feature depends on it. Mitigation: degraded modes in §3.
2. **Google Play policy** — SMS, call log, all-files, photo access each need approval. Mitigation: `ROLE_CALL_SCREENING` over `READ_CALL_LOG`; ship sideload/beta first; keep explorer SAF-based for a Play build if refused.
3. **Deprecated answer/end APIs** could be removed in a future Android. Mitigation: isolate behind one `CallController`; monitor releases.
4. **OEM background killing** — device-dependent reconnect reliability.
5. **Android 15+ sensitive notification redaction** — cannot be fixed; communicate in UI.
6. **Phone Link coexistence** on Windows 11 (same HFP transport).
7. **Client-isolated Wi-Fi** — LAN blocked; Bluetooth fallback is low bandwidth.

---

## 7. Prototype results

Reference device: **realme narzo 50 (RMX3286), realme UI 4.0, Android 13**, paired to this PC over Bluetooth.

**Windows HFP spike** — `spikes/windows-hfp` (.NET 10), run 2 (phone paired, unpackaged):

```
OS: Microsoft Windows NT 10.0.26200.0  packaged=False
Paired Bluetooth devices: 2  (UGREEN Studio Max2, narzo 50)
HFP phone-line transports: 1
- narzo 50: audio=CanRouteToLocalDevice inBandRing=False
  access=DeniedBySystem
  RegisterApp: UnauthorizedAccessException 0x80070005
  PhoneLines: TimeoutException
```

Proves: the PC's Bluetooth stack exposes the narzo as a hands-free phone-line transport and reports **CanRouteToLocalDevice** → the hardware/driver side of call audio works on this PC.

Blocks: Windows denies the API to apps without package identity + the **restricted** capability `phoneLineTransportManagement`. Public reports (Microsoft Q&A, the MyPhone open-source project) say it is still denied on Windows 11 22H2+ even with the capability, while Microsoft's Phone Link keeps working → likely Microsoft-gated. Microsoft's docs also say restricted phone capabilities are rarely approved for Store apps.

Run 3 — **packaged** (Developer Mode, loose registration via `register-packaged.ps1`, manifest declares `runFullTrust`, `phoneCall`, `phoneLineTransportManagement`; Windows privacy switches for phone calls/Bluetooth all *Allow*):

```
packaged=True
- narzo 50: audio=CanRouteToLocalDevice
  access=DeniedBySystem
  RegisterApp:  UnauthorizedAccessException 0x80070005
  ConnectAsync: UnauthorizedAccessException 0x80070005
  PhoneLines:   TimeoutException
```

**Decision: call audio on the PC is NOT PRACTICAL for Palwyn.** Windows gates the HFP phone-line API to Microsoft-trusted apps (Phone Link). Calls ship as **control-only**: caller ID, answer, decline, end from the PC; the conversation happens on the phone or on a Bluetooth headset paired to the phone. Writing our own HFP stack would mean replacing the Windows Bluetooth driver per adapter — rejected.

Microsoft Phone Link is not installed on this PC, so it isn't competing for the phone's HFP connection.

realme UI note: ColorOS-derived background management is aggressive — onboarding must cover "Allow background activity" / auto-launch. Android 13 → restricted-settings flow applies to sideloaded builds; OTP redaction (15+) and `ACCESS_LOCAL_NETWORK` (17+) do not apply on this device.

**Android spike** — `spikes/android-spike` (Kotlin, framework APIs only, minSdk 29 / target 36) + `pc-client.ps1`. Foreground service (`connectedDevice`) listens on TCP 47800 behind a random token; the PC connects over Wi-Fi, sends commands, receives events. Counts/states only — no content leaves the phone. Tested on the narzo 50 (Android 13, realme UI 4.0), app in background, screen locked.

| Test | Result |
|---|---|
| LAN connect PC → phone | ✅ 5–10 ms; wrong token → `DENIED`, closed |
| Device info / battery | ✅ |
| SMS provider read | ✅ 3051 messages / 301 threads readable |
| MediaStore | ✅ 38 images, 5 albums, 2 videos; 256 px thumbnail in 86 ms |
| Notification listener | ✅ bound, active notifications listed (0 with reply actions at test time) |
| Media sessions | ✅ API works (0 sessions at test time) |
| Clipboard write from background | ✅ |
| Forced deep Doze (screen off, unplugged) | ✅ commands still answered in ms |
| Caller number via `PHONE_STATE` + `READ_CALL_LOG` | ✅ (two broadcasts: first without number, second with) |
| Caller number via `CallScreeningService` role | ❌ role granted, but Telecom never invoked the service on this device |
| **Answer from PC** (`acceptRingingCall`) | ✅ call connected, 270 ms command round trip |
| **End from PC** (`endCall`) | ✅ `true`, 200 ms |

**realme freezer (ColorOS "Hans") — critical finding.** With the app in the background, realme suspends it despite: a running foreground service, Android battery-optimisation exemption, standby bucket EXEMPTED, and realme's own *Allow background / foreground activity* + *Auto launch* toggles. Effect: call events were generated on time but reached the PC **2+ minutes late** or only when something else woke the app — missed calls every time.

What works: **incoming network traffic wakes the app.** With the PC sending a tiny keep-alive every 3 s, ring → PC took ≈3 s and the PC answered and ended the call successfully.

Implications:
- The link needs a **PC → phone keep-alive**, interval tunable per device (3 s on realme; the normal 15 s where the OEM doesn't freeze). Battery cost must be measured (Phase 5/17).
- Caller ID primary path = `PHONE_STATE` + `READ_CALL_LOG` (Play declaration needed); screening role is not reliable across OEMs.
- Also learned: changing Developer options → *Select debug app* force-stops the app (test-setup pitfall, not a product issue).

---

## 8. Needs real-device testing

HFP transport + audio (per PC adapter); answer/end on the target phone's Android version/OEM skin; caller ID via screening role for contacts vs unknown numbers; background survival on the OEM; notification-access flow when sideloaded; clipboard write toast; firing other apps' `contentIntent` from background.

---

## 9. Costs

Phase 0: **FREE** (.NET SDK, Android SDK, public docs).

| Service | Purpose | When | Est. cost | Free alternative / trade-off |
|---|---|---|---|---|
| Google Play developer account | Android distribution | Release | USD 25 one-time | Sideload APK — restricted-settings friction, no auto-update |
| Microsoft Store | Windows distribution + Store signing | Release | Individual registration currently free (verify at signup) | Direct download — needs own code signing |
| Windows code signing | Avoid SmartScreen warnings for direct download | Release | Azure Artifact Signing ~USD 10/mo (verify) | Store-only distribution |
| Subscription billing | Pro plan | Phase 14+ | Store/Play take ~15% on subscriptions (verify) | Own processor ~3–5% + tax handling burden |

No cloud service is required by any MVP or V1 feature.
