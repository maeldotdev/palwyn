# Palwyn — Security Model

Approved by the product owner on 2026-09-27 (Phase 0). Built in Phase 4.

## 1. Assets

Call metadata, SMS content, notification content, photos/files, clipboard, contacts, device info, and each device's identity key.

## 2. Threat model

| Adversary | In scope | Mitigation |
|---|---|---|
| Passive LAN sniffer (same Wi-Fi) | ✅ | TLS 1.3 everywhere; no plaintext mode, not even for discovery payloads beyond device id/name |
| Active LAN attacker: spoofed mDNS, ARP/MITM, fake phone/PC | ✅ | Mutual TLS with pinned certificate fingerprints; pairing secret bound to both certificates |
| Replay of captured traffic | ✅ | TLS 1.3 record protection (no 0-RTT) |
| Unknown device connecting to the phone's listener | ✅ | Handshake rejected unless the client cert is pinned (or matches the QR during pairing) |
| Brute-forcing the pairing | ✅ | 128-bit QR secret; commit-then-reveal code; pairing window 5 min and **one** connection attempt (the phone claims the window for the first PC that connects) |
| Lost/stolen paired device | ✅ | Revoke from the other side; next connect fails |
| Malware already running as the user on either device | ❌ | Out of scope — it can read the same data directly |
| Compromised OS / root | ❌ | Out of scope |

## 3. Identity

- Each install creates one **EC P-256** key pair and a self-signed X.509 certificate (20-year validity — trust comes from pinning, not from a CA or dates).
  - Android: generated inside **Android Keystore**, non-exportable; the keystore issues the self-signed cert.
  - Windows: persisted **CNG** key, non-exportable, in the CurrentUser\My store (SChannel needs a persisted key).
- **Fingerprint** = SHA-256 of the DER certificate. This is the device's identity everywhere.
- `deviceId` = first 16 bytes of the fingerprint, hex. Names are labels only and never used for trust.
- Reinstalling the app = new identity = must re-pair (intended).

## 4. Transport

- TLS **1.3 only**, mutual authentication, no session resumption / 0-RTT.
- Custom trust check on both sides: accept the peer certificate **iff** its fingerprint is in the pinned set (or equals the expected fingerprint during pairing). No hostname or chain validation — it would add nothing here.

## 5. Pairing

### 5a. QR (default)

1. User opens *Add phone* on Windows. PC creates `secret` (16 random bytes) and shows a QR:
   `palwyn://pair?v=1&fp=<PC fingerprint, b64url>&s=<secret, b64url>&n=<PC name>` — valid 5 minutes, single use.
2. Phone scans, advertises `_palwyn._tcp` with TXT `pair=<first 8 hex of PC fp>` and accepts TLS **only** from a cert whose fingerprint equals `fp`.
3. PC connects (mutual TLS). PC now knows the phone's cert; phone has verified the PC.
4. Phone sends `PAIR_PROOF { mac: HMAC-SHA256(secret, "PB-PAIR-1" ‖ fp_PC ‖ fp_phone) }`.
5. PC verifies in constant time. Success → both pin each other's fingerprint, show "Connected", then send `HELLO`. Failure → close, count an attempt.

Why it holds: only someone who saw the QR knows `secret`; the MAC covers both certificate fingerprints, so a MITM terminating two TLS sessions (different fingerprints) cannot reuse a proof.

### 5b. Code comparison (no camera)

1. Phone lists PCs in pairing mode via DNS-SD; user taps one. PC connects with mutual TLS, both accepting any cert for this one session.
2. Commit-then-reveal: PC sends `PAIR_COMMIT { c: SHA-256(nPC) }`; phone sends `PAIR_NONCE { n: nPhone }`; PC reveals `PAIR_REVEAL { n: nPC }`; phone checks the commitment.
3. Both show `code = HMAC-SHA256(nPC ‖ nPhone, "PB-SAS-1" ‖ fp_PC ‖ fp_phone)` → 6 decimal digits.
4. User confirms the codes match **on both devices** → both pin. Mismatch → abort.

The commitment stops a MITM from picking nonces after seeing the other side's, so its chance of forcing matching codes is 1 in 10⁶ per attempt, and each attempt needs a visible user confirmation.

## 5c. Several phones and addresses

- A PC can keep several phones paired, each pinned separately; only the one in use is connected. Switching drops the link to the old phone first, and its live data (calls, notifications) leaves the PC with it.
- A phone can be given an address (a VPN such as Tailscale, or a network without discovery). The PC then connects only there, with the same pinned TLS, so a wrong or hostile address can at worst fail to connect: nothing else will pass the certificate check. The address is never logged. Pairing itself still needs discovery on one network.
- Over a USB cable the PC uses adb's port forward (127.0.0.1:47801 to the phone's link port). Any program on the PC can open that port, but it only reaches the phone's TLS server, which still demands a paired PC's certificate; the PC checks the phone's pinned certificate before moving the link there. It relies on USB debugging, which the user turns on and which Android guards with its own "Allow USB debugging?" prompt; Palwyn never turns it on or answers that prompt.

## 6. Revocation

- *Remove device* on either side: delete the pin, send `UNPAIR` if connected, close. The other side deletes its pin when it receives `UNPAIR`, or when its next connection is rejected (state `Unauthorized`, no retries).
- Revoking never needs the other device to be online.

## 7. Data handling

- Nothing sensitive in logs (see [architecture.md §7](architecture.md)).
- Call history and text messages are never stored on the PC: the Calls and Messages pages ask the phone each time. Numbers, names and message text travel only to the paired PC over the TLS link; logs on both sides mask numbers to the last 2 digits and never contain message text.
- New-message and mirrored phone notifications show their text in Windows notifications, so Windows' own lock-screen and Notification Center settings decide who can glimpse them. Both can be turned off: all phone notifications in Settings, or one app at a time.
- Quick Drop never acts on its own: the phone offers only what the user shared, and the PC pushes only what the user dropped. Files from the phone are saved without a prompt to the save folders chosen in Settings: photos and videos with the saved photos (Pictures\Palwyn by default), everything else to Downloads\Palwyn (the user picked this PC on the phone); files from the PC stay hidden on the phone until complete. Links are opened only when the user taps or clicks them.
- Photos are copied only when the user opens or saves them. "Open" copies live in the package temp folder and are deleted at the next start. File names from the phone are reduced to a plain name before touching the disk, and a failed copy leaves no partial file.
- Live notification contents are held in memory only while connected. The notification history (on by default) is the one exception: the paired phone's last 500 notifications (app, title, text, time) in a JSON file in the app's LocalState. Turning the setting off, clearing it, or removing or replacing the phone deletes it; muted apps are never recorded.
- "Show this PC's notifications on my phone" (off by default) needs Windows' notification-access consent. It sends the app name and the toast's text lines of new notifications only (not those already there), never Palwyn's own, and nothing is logged but on/off. The phone shows them in its own channel and removes them all when the session ends. A paired PC can fire a notification's actions (reply, mark as read) only when the user does it on the PC.
- A paired PC can send SMS through the phone, and group texts as MMS (user-initiated only). Android's premium-SMS guard still asks on the phone before texting premium short codes. The group MMS file is written to the app's cache, handed to Android's MMS service through a non-exported FileProvider (read-only, `cache/mms` only), and deleted after the send.
- MMS pictures and attachments are copied to the PC only when shown or opened, into the same temp folder as photo "Open" copies (deleted at the next start). A transfer can pull only attachment parts of MMS, never SMIL or text parts, and needs `sms.read`.
- Windows history caches (messages/notifications) are bounded, user-clearable, and deleted with the device. They sit in the user profile, protected by Windows account access (at-rest encryption beyond that is out of scope for v1: an attacker with the user's session can read them anyway).
- Phone data is sent only in response to a paired PC's request or as events the user enabled.
- "Keep this PC awake while my phone is connected" (off by default) also stops Windows locking itself on idle, since it holds a display power request. The setting says so and suggests Windows+L; it's released as soon as the phone disconnects or the setting is turned off.
- A paired PC can ring the phone (at most a minute, stoppable on the phone) and control its media playback and volume. It can't read the Wi-Fi network name or anything about Bluetooth beyond on/off. The PC's recent-activity list (names, file names) lives in memory only.
- Contacts: with `READ_CONTACTS` a paired PC can list the phone's contacts; with `WRITE_CONTACTS` it can also add, edit and delete them (each delete confirmed on the PC). Both are Android permissions the user grants on the phone and can revoke there. The PC keeps the list and contact pictures in memory only; logs record "Contact added/edited/deleted", never names or numbers. A delete also reaches accounts the contact syncs to (e.g. Google).
- Copied images follow the same rules as text (off by default on the PC, only on a tap on the phone, private and sensitive clips skipped). On the phone the latest image from the PC sits in Palwyn's cache, readable by other apps only through the clipboard's own paste grant; on the PC an image from the phone is copied from the temp folder (deleted at the next start). Sizes are logged, never contents.
- "Take a photo" from the PC only shows a notification; nothing happens until the user taps it and takes the photo in the phone's own camera app. Palwyn holds no camera permission and never starts the camera itself. The photo stays in Palwyn's cache (not the gallery) until the next request.
- The phone as a remote: a paired phone can move the mouse, click and type on the PC only while the PC's "Mouse and keyboard" setting is on (off by default, since it's full control of the PC), and play/pause, set the volume and lock the PC while "Music, volume and lock" is on (on by default: nothing it can't undo). The PC checks its settings on every message, whatever it told the phone. Windows keeps synthesized input away from the sign-in screen, UAC prompts and apps with higher rights than Palwyn. The phone sends only while its Remote screen is open. Logs note once per connection that the phone took control, never what was typed.
- Phone screen on the PC: a PC can only ask. Sharing starts when the user taps the phone's notification and then Start in Android's own consent dialog, every time, and Android shows that the screen is being shared. Frames go only to the PC that asked, on their own TLS connection; nothing is recorded or stored on either side. Sharing stops when the PC window closes, from the phone, when the link drops, or after 30 s unwatched. Exception, off by default: with "Keep screen sharing ready" on the phone, those stops become a pause (no frames, remote control off) and the same PC can resume without a new consent until the user taps Stop in the ongoing notification, turns the switch off, or restarts the phone. Any other PC still needs its own consent, which ends the paused one. Secure windows (banking apps, DRM video) stay black because Android blocks them.
- Controlling the phone from the PC needs two things on the phone, both off by default: Palwyn's "Let my PC control this phone" switch and its accessibility service in Android's settings. Even then it works only while the screen is shared, and only for the PC it's shared with. The service taps, swipes, presses Back/Home/Recents and sets the text of the focused field; it reads only that field's text, to add the typing, and keeps nothing. Google Play restricts accessibility services to accessibility tools, so a Play release may need this left out.
- Commands the phone can run exist only if the user adds them in the PC's Settings. The phone sees their names, never the command lines, and can't send a command of its own; they run as the PC's user (with administrator rights if Palwyn itself runs elevated), and the log records only the name. What's playing on the PC and its volume go to the phone only while "Music, volume and lock" is on.
- Folders sent from the PC land under Download/Palwyn only: the phone rebuilds each path part as a plain name, so `..` or absolute paths can't escape it.
- Clipboard sync is off by default on the PC and can be turned off on the phone (which then refuses both directions); content marked sensitive by the OS (Android `ClipDescription.EXTRA_IS_SENSITIVE`, Windows exclusion formats) is never sent. The phone's clipboard leaves it only when the user taps a send action. Clipboard text travels only over the TLS link, is never written to disk or logs (lengths only), and the PC's notification about it expires after a minute.

## 8. Implementation notes (Phase 4)

- **QR scanning** uses the phone's own camera app: the QR holds the `palwyn://pair?...` URI, Android opens it in Palwyn via a deep link, and the user must tap *Pair* before the phone accepts anything. No camera permission or scanning library.
- **Trust check placement**: the phone's TLS trust manager rejects any client that isn't pinned or expected for pairing, so strangers never reach the protocol. Verified on device with a random certificate (rejected during the handshake).
- **Keys**: phone key in Android Keystore; PC key is a named, non-exportable CNG key (`Palwyn.Identity`). Both survive app updates and reboots (verified).
- **Discovery privacy**: the phone advertises only while pairing (with its name) or while paired but not connected (id only, no name). Accepted residual risk (Phase 15): the `id` attribute is a stable identifier visible on the network while advertising. A rotating tag would not remove it, because the phone's TLS certificate (equally stable) is shown to anyone on the network who starts a handshake, before client authentication. Unlinkable identities would need a different handshake design; not planned for v1.
- **Removal**: removing on one side sends `UNPAIR` when connected; otherwise the other side learns on its next connection attempt (TLS rejection).

## 8b. Emergency screen and Rescue files

For when the phone's screen is broken but the phone still runs: the PC shows and controls it, and copies its shared storage, with no prompt on the phone. Spec: [superpowers/specs/2026-10-04-emergency-screen-design.md](superpowers/specs/2026-10-04-emergency-screen-design.md).

- **Trust boundary: adb authorization.** Everything goes through adb on a USB cable, and the phone accepts adb only from PCs the user allowed ("Allow USB debugging from this computer", Android's RSA key prompt). Palwyn opens no new access: any program on an allowed PC can already capture the screen, inject input and read shared storage through adb. Palwyn makes it a button. A phone that never allowed this PC can't be reached, and Palwyn says so.
- **The helper** (`apps/android/emergency`, partly ported from scrcpy v4.1, Apache-2.0) is pushed to `/data/local/tmp/` and run by adb as the shell user. That's why it needs no MediaProjection consent and works while Palwyn's phone app is frozen. It isn't installed, keeps nothing, and is deleted from the phone when the session ends; Palwyn also stops it when it quits.
- **Its channel**: an abstract socket with a random name per session, reached through `adb forward tcp:0` (127.0.0.1 only on the PC). The helper serves exactly one client, which must first send the 32-byte random token Palwyn passed on the helper's command line. A wrong or missing token (5 s) ends the helper. This keeps other PC programs (the forwarded port) and other phone apps (abstract sockets are world-connectable) out.
- **Nothing recorded**: no screen images are saved. Logs say "Emergency screen opened/closed" with the model, and Rescue's counts, never file names or screen content.
- **Android's protections still apply**: secure windows (FLAG_SECURE: banking apps, DRM video) stay black, and other apps' private data (`Android/data`, app databases) can't be rescued. After a restart the lock screen shows; the user types the PIN from the PC keyboard. Palwyn never stores or enters a PIN.
- **adb** is bundled (Android SDK Platform-Tools r37.0.1, Apache-2.0), downloaded at build time and checked against a pinned SHA-256.

## 9. Phase 15 audit (2026-09-29)

Checked: network and TLS setup, pairing, local storage, logs, tokens and secrets, permissions and exported components, device removal, message and photo privacy, file names from the other side, frame limits, dependencies.

Found and fixed:

- **TLS resumption**: the PC now refuses session resumption (`AllowTlsResume = false`), so every connection is a full handshake with the pin checked; it also re-checks the certificate in use after the handshake. The phone re-checks at the app level that a connection opening the pairing window is one the trust manager would accept, since a resumed session would skip the trust manager.
- **Phone-to-phone transfer**: `allowBackup="false"` doesn't stop Android 12+ device-to-device transfer; `dataExtractionRules` now excludes everything from both cloud backup and transfer.
- **Share as a confused deputy**: another app could share one of Palwyn's own cache files (clipboard image, camera photo, MMS) to make Palwyn send it; shares of Palwyn's own FileProvider are now ignored. Anything else shared still goes only to the user's own paired PC, after the share sheet.
- **Reserved Windows names**: a phone file named `CON`, `NUL`, `COM1.jpg` etc. is saved as `_CON`… instead of opening a device.
- **Removal**: removing a phone that isn't the one in use now deletes its notification history at once (it used to wait for the next phone switch).

Checked and fine: no secrets in the repository or its history (the only match is a published test vector); the PC listens on no port; TLS 1.3 only with mutual pinning on both sides; frames capped at 1 MiB before allocation, pairing handshakes time out after 20 s; logs mask numbers and hold no message, notification or clipboard content (PC logs kept 7 days); exported Android components are either system-guarded (boot, tile, notification listener, accessibility) or user-started (launcher, share, text selection, pairing deep link that still needs a tap); `dotnet list package --vulnerable`: none. Not done: a third-party penetration test, and a dependency scan for the Android libraries (AndroidX and Compose only, from Google Maven).

## 10. Libraries

Platform crypto only: .NET `SslStream`/`ECDsa`/`HMACSHA256`/`RandomNumberGenerator`, Android Conscrypt/`KeyStore`/`Mac`/`SecureRandom`. QRCoder (MIT) renders the QR image on Windows and touches only the pairing URI.
