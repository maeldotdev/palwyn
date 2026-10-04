# Palwyn Link Protocol — v1

Transport: TLS 1.3 over TCP (see [security.md](security.md)). Identity of the sender is the authenticated TLS peer — it is never trusted from message content.

## 1. Framing

```
┌──────────────┬──────────────────────────────┐
│ length: u32  │ UTF-8 JSON object (length B) │
│ big-endian   │                              │
└──────────────┴──────────────────────────────┘
```

- Max frame on the control channel: **1 MiB**. Larger → protocol error, close.
- Length 0 → protocol error.

## 2. Envelope

```json
{ "v": 1, "id": "7b0c…", "type": "CALL_INCOMING", "ts": 1790000000000, "replyTo": null, "payload": { } }
```

| Field | Type | Rule |
|---|---|---|
| `v` | int | Protocol major. Must equal the negotiated major. |
| `id` | string | Unique per sender per connection, 1–64 chars `[A-Za-z0-9_-]`. |
| `type` | string | `UPPER_SNAKE`, 1–64 chars. |
| `ts` | int | Sender clock, Unix ms. Informational only — never used for security or ordering. |
| `replyTo` | string? | Set on responses to the request's `id`. |
| `payload` | object | Required, may be `{}`. |

Unknown **fields** are ignored (forward compatibility). Unknown **types** get `ERROR UNSUPPORTED_TYPE`; the connection stays open.

## 3. Validation (receiver)

In order, first failure wins:
1. Frame length within limits → else close.
2. Valid UTF-8 JSON object → else close.
3. Envelope fields present with correct types → else close.
4. `v` matches negotiated version → else close.
5. `type` known **for this direction** (e.g. the phone never accepts `CALL_STATE`) → else `ERROR UNSUPPORTED_TYPE`.
6. Payload matches the type's schema (types, required fields, string lengths, enum values) → else `ERROR INVALID_PAYLOAD`.
7. Capability for this type negotiated → else `ERROR NOT_CAPABLE`.

"Close" = protocol error from an authenticated peer running a bug or hostile build; the reconnect loop handles recovery. [shared/protocol/test-vectors.json](../shared/protocol/test-vectors.json) encodes these rules; both platforms' test suites must pass it.

## 4. Session start

Both sides send `HELLO` immediately after TLS; neither sends anything else until it has received the peer's `HELLO`.

```json
"payload": {
  "protocol": { "min": 1, "max": 1 },
  "app": "0.1.0",
  "deviceId": "b3f1…",
  "name": "narzo 50",
  "platform": "android",
  "capabilities": ["device", "calls.state", "calls.control", "sms.read", "sms.send", "notifications.read"],
  "keepAlive": 3
}
```

`keepAlive` (optional, 1–300 s, phone only): how often the PC should `PING`. Phones on OEMs that freeze background apps ask for 3 s; incoming traffic is what wakes them.

- Negotiated version = highest major in both ranges. No overlap → send `ERROR INCOMPATIBLE_VERSION`, close, state `Incompatible` (no retry).
- `deviceId` must match the pinned identity for this certificate, else close.
- Capabilities change at runtime (permission granted/revoked) → `CAPABILITIES_CHANGED { capabilities: [...] }` with the full new list.

## 4b. Pairing messages

Exchanged on a pairing connection, before (and instead of) HELLO. The connection closes after `PAIR_RESULT`; the PC then reconnects as a paired peer. Maths and flows: [security.md §5](security.md), vectors: [pairing-vectors.json](../shared/protocol/pairing-vectors.json). Binary values are lowercase hex.

| Type | Dir | Payload | Flow |
|---|---|---|---|
| `PAIR_PROOF` | P→C | `{ mac }` (64 hex) | QR |
| `PAIR_COMMIT` | C→P | `{ c }` (64 hex) | code |
| `PAIR_NONCE` | P→C | `{ n }` (32 hex) | code |
| `PAIR_REVEAL` | C→P | `{ n }` (32 hex) | code |
| `PAIR_CONFIRM` | ⇄ | `{ accepted }` | code |
| `PAIR_RESULT` | C→P | `{ ok, name }` | both |

## 5. Control messages

| Type | Dir | Payload |
|---|---|---|
| `HELLO` | ⇄ | above |
| `PING` / `PONG` | ⇄ | `{}`; `PONG.replyTo` = ping id |
| `ERROR` | ⇄ | `{ code, message? }`, `replyTo` when caused by a frame |
| `CAPABILITIES_CHANGED` | ⇄ | `{ capabilities }` |
| `UNPAIR` | ⇄ | `{}` — sender has revoked the peer; receiver deletes its pin and stops reconnecting |

Error codes: `UNSUPPORTED_TYPE`, `INVALID_PAYLOAD`, `NOT_CAPABLE`, `PERMISSION_DENIED` (phone permission missing), `FAILED` (platform call failed), `INCOMPATIBLE_VERSION`, `BUSY`.

## 6. MVP feature messages

Phone → PC events carry state; PC → phone commands get exactly one response (the result or an `ERROR`) with `replyTo`. The PC matches replies by `replyTo`, so events may arrive in between; a command unanswered after 10 s (8 s for calls) fails.

### device
| Type | Dir | Payload |
|---|---|---|
| `DEVICE_INFO` | P→C | `{ manufacturer, model, androidVersion, sdk, storageTotal, storageFree }` — after HELLO and on change |
| `BATTERY_CHANGED` | P→C | `{ level: 0–100, charging: bool }` — on ≥1 % change |

### calls (capabilities `calls.state`, `calls.control`, `calls.log`)
| Type | Dir | Payload |
|---|---|---|
| `CALL_STATE` | P→C | `{ callId, state: "RINGING"\|"ACTIVE"\|"ENDED", number?, name?, direction: "IN"\|"OUT", since }` |
| `CALL_ANSWER` | C→P | `{ callId }` → `CALL_RESULT` |
| `CALL_DECLINE` | C→P | `{ callId }` → `CALL_RESULT` |
| `CALL_END` | C→P | `{ callId }` → `CALL_RESULT` |
| `CALL_RESULT` | P→C | `{ ok: bool }` |
| `CALL_LOG_GET` | C→P | `{ limit 1–100, before?, query? }` (Unix ms; query: see Search) → `CALL_LOG` |
| `CALL_LOG` | P→C | `{ calls: [≤ 100 × { id, type: "IN"\|"OUT"\|"MISSED"\|"REJECTED"\|"BLOCKED"\|"VOICEMAIL", date, duration (s), number?, name? }] }`, newest first |

- `callId` is phone-generated per call. `since` is when the current `state` began (phone clock).
- Answer and decline need `state` RINGING, end needs ACTIVE, and the `callId` must be current; otherwise `ERROR FAILED`. A missing permission gives `ERROR PERMISSION_DENIED`.
- `CALL_RESULT { ok: true }` means Telecom accepted the command. The following `CALL_STATE` confirms the outcome.
- The number usually arrives in a second `CALL_STATE` with the same `callId` a few ms after the first (Android sends it in a second broadcast). It is absent for private numbers.
- The phone sends a call already in progress when the PC connects. An old ENDED call is not replayed.
- One call at a time: PHONE_STATE is a single state for the whole phone, so a waiting second call replaces the first. No mute/hold/audio messages: a non-dialer app cannot control those (Phase 0 §2).
- Capabilities follow Android runtime permissions: `calls.state` = `READ_PHONE_STATE`, `calls.control` = `ANSWER_PHONE_CALLS`, `calls.log` = `READ_CALL_LOG`. The number in `CALL_STATE` also needs `READ_CALL_LOG`; `name` needs `READ_CONTACTS`. Granting one sends `CAPABILITIES_CHANGED`.

### messages (capabilities `sms.read`, `sms.send`)
| Type | Dir | Payload |
|---|---|---|
| `SMS_RECEIVED` | P→C | `{ threadId, messageId, address, name?, body ≤ 10000, date, outgoing?, parts? }`: a new message in the phone's SMS or MMS store, received or sent (from the phone or the PC) |
| `SMS_THREADS_GET` | C→P | `{ limit ≤ 100, before?, query? }` (see Search) → `SMS_THREADS` |
| `SMS_THREADS` | P→C | `{ threads: [{ threadId, addresses, names, snippet ≤ 200, date, unread }] }`, newest first; `names[i]` is `""` without a contact |
| `SMS_MESSAGES_GET` | C→P | `{ threadId, limit ≤ 200, before? }` → `SMS_MESSAGES` |
| `SMS_MESSAGES` | P→C | `{ messages: [{ messageId, address, body, date, outgoing, read, status?: "sending"\|"failed", parts? }] }`, newest first, SMS and MMS together |
| `SMS_SEND` | C→P | `{ address, body ≤ 5000 }` → `SMS_SEND_RESULT` once the radio reports the SMS sent (phone waits ≤ 60 s) |
| `MMS_SEND` | C→P | `{ addresses: [1–20], body ≤ 5000 }` → `SMS_SEND_RESULT` once the carrier accepts it (phone waits ≤ 120 s): a group text, one MMS to all |
| `SMS_SEND_RESULT` | P→C | `{ ok }` |

- `sms.read` = `READ_SMS`, `sms.send` = `SEND_SMS`. New messages come from ContentObservers on the SMS and MMS stores, so `RECEIVE_SMS` isn't needed and messages sent from the phone itself also reach the PC.
- The phone keeps an `SMS_MESSAGES` page under 800 KB (bodies counted at 4 bytes per character), so it can return fewer than `limit`.
- MMS: `messageId` is `mms-<id>`; `body` is its text parts joined; `parts` lists up to 10 other parts `{ id, mime, name? }` (pictures, video, audio, contact cards), fetched with `TRANSFER_PULL` kind `mms`. The address is the sender, or the first recipient of one sent from the phone. An MMS still downloading isn't listed until the SMS app has it. A thread whose provider snippet is empty (MMS subjects) gets the newest MMS's text, or "Attachment".
- `MMS_SEND` builds a text-only m-send-req PDU on the phone and hands it to `SmsManager.sendMultimediaMessage` through a FileProvider URI; Android's MMS service uses the carrier's MMS settings and mobile data, and stores the sent MMS for a non-default SMS app. The carrier's MMS rate applies.
- RCS chat messages are not in the store and are not shown. A non-default SMS app can't mark messages read.

### notifications (capabilities `notifications.read`, `notifications.act`)
| Type | Dir | Payload |
|---|---|---|
| `NOTIFICATION_POSTED` | P→C | `{ key ≤ 512, package, appName, title? ≤ 500, text? ≤ 4000, postedAt, clearable, actions: [≤ 5 × { index, title ≤ 64, reply }], existing? }` |
| `NOTIFICATION_REMOVED` | P→C | `{ key }` |
| `NOTIFICATION_DISMISS` | C→P | `{ key }` → `NOTIFICATION_RESULT` |
| `NOTIFICATION_ACTION` | C→P | `{ key, index, replyText? ≤ 5000 }` → `NOTIFICATION_RESULT` |
| `NOTIFICATION_RESULT` | P→C | `{ ok }` |
| `APP_ICON_GET` | C→P | `{ package }` → `APP_ICON` |
| `APP_ICON` | P→C | `{ package, png }`: 64 px PNG, base64 ≤ 90 000 chars; the PC keeps it on disk |

- Both capabilities = notification access granted in Android settings. When it's granted mid-session the phone also sends what's in the shade (`existing: true`).
- Posted again under the same `key` = an update. The PC alerts only when the title or text changed, never for `existing`, and not for 10 s after it acted on that key (the app's own update after a reply).
- Not mirrored: ongoing notifications (media, navigation, downloads, Palwyn's own), group summaries, incoming-call notifications (the call card covers them) and the default SMS app's (the Messages page covers them). Android 15 redacts one-time codes for apps like Palwyn.
- `index` refers to the app's own action list. Reply actions fill every free-form `RemoteInput`. Actions that open a screen on the phone may be blocked by Android's background-start limits.

### photos and videos (capability `photos.read`)
| Type | Dir | Payload |
|---|---|---|
| `PHOTOS_GET` | C→P | `{ limit ≤ 200, beforeId?, album? }` → `PHOTOS`; with `album`, only that album |
| `ALBUMS_GET` | C→P | `{}` → `ALBUMS` |
| `ALBUMS` | P→C | `{ albums: [≤ 200 × { id ≤ 32, name ≤ 255, count }] }`: MediaStore buckets (the folders photos are in), biggest first, counting only readable items |
| `PHOTOS` | P→C | `{ photos: [{ id, name?, date, width, height, size, mime, video?, duration? (ms) }] }`, newest first (MediaStore id order; images and videos share ids) |
| `PHOTO_THUMB_GET` | C→P | `{ id, video? }` → `PHOTO_THUMB` |
| `PHOTO_THUMB` | P→C | `{ id, jpeg }`: about 320 px (a frame for videos), base64 |

`photos.read` = `READ_MEDIA_IMAGES`/`READ_MEDIA_VIDEO` (Android 13+), or `READ_MEDIA_VISUAL_USER_SELECTED` on 14+ (then only the items the user picked), or `READ_EXTERNAL_STORAGE` (≤ 12).

### Quick Drop (capability `drop`, always on: files go to Palwyn's own folder)
| Type | Dir | Payload |
|---|---|---|
| `DROP_TEXT` | C→P | `{ text ≤ 50 000 }` → `DROP_DONE`; shown as a phone notification (a link opens on tap; text has Copy) |
| `DROP_DONE` | P→C | `{ ok }` |
| `DROP_OFFER` | P→C | `{ dropId, files: [≤ 50 × { index, name, size, mime }], text?, purpose? }`: the user shared these to Palwyn. `purpose`: `"clipboard"` (an image copied on the phone: the PC puts it on its clipboard) or `"camera"` (a photo the PC asked for: saved with the PC's photos) |
| `DROP_RESULT` | C→P | `{ dropId, ok }`: the PC has pulled everything (or gave up); the phone's share sheet closes |

### Transfer connections
Files never travel on the session. The PC opens a second TLS connection (same pinning), reads the phone's `HELLO`, and sends one of these **instead of its own HELLO**:

| First frame (C→P) | Then |
|---|---|
| `TRANSFER_PULL { kind: "photo"\|"video"\|"drop"\|"mms", id }` | phone: `TRANSFER_START { size, name, mime }` (or `ERROR`), then exactly `size` raw bytes, then close |
| `TRANSFER_PUSH { kind: "file"\|"clipboard", name, size, mime, folder? }`, then exactly `size` raw bytes | phone: `TRANSFER_DONE { ok }` (or `ERROR`) once the file is stored, then close |

- Pull authorization depends on the kind: `photo`/`video` need `photos.read`; `mms` (an MMS part id) needs `sms.read` and serves only attachment parts; `drop` ids (`dropId:index`) exist only while the phone's offer is live (the share sheet is open).
- Push kinds: `file` goes to Download/Palwyn, inside `folder` (≤ 255, "Trip/Day 1") when the PC sends a folder; the phone makes each part a safe name and drops `..` and empty parts, at most 8 deep. `clipboard` (an image copied on the PC, ≤ 20 MB) needs the `clipboard` capability; the phone keeps only the latest one in its cache and puts it on its clipboard as a FileProvider URI.
- A transfer connection is not a session: it replaces no session and gets no events.
- Both sides treat a short stream or 20 s of silence as failure. The PC writes to a `.partial` file and renames it when complete; the phone writes pushed files as `IS_PENDING` MediaStore rows and deletes them on failure.

### Clipboard (capability `clipboard`: on unless the user turns it off in the phone's Settings)
| Type | Dir | Payload |
|---|---|---|
| `CLIPBOARD_SET` | both | `{ text ≤ 50 000 }`: put this text on the receiver's clipboard. No reply. |

- The PC sends only while its clipboard setting is on, and never text carrying Windows' private clipboard formats. The phone sends only when the user taps a send action (Android 10+ allows no background clipboard reads) and never a clip marked `EXTRA_IS_SENSITIVE`.
- Text travels as `CLIPBOARD_SET`. Images (PNG from the PC, the original format from the phone, ≤ 20 MB) travel over transfer connections: PC to phone as `TRANSFER_PUSH` kind `clipboard`, phone to PC as a `DROP_OFFER` with `purpose: "clipboard"`. Other files go by Quick Drop.

### Camera (capability `camera`: the phone has a camera)
| Type | Dir | Payload |
|---|---|---|
| `CAMERA_REQUEST` | C→P | `{}` → `CAMERA_RESULT` once the phone shows its notification |
| `CAMERA_RESULT` | P→C | `{ ok }` |

- Android doesn't let a background app open the camera, so the phone shows a notification (it times out after 2 minutes). Tapping it opens the phone's own camera app with an output file in Palwyn's cache, so Palwyn needs no camera permission. The photo then goes to the PC as a `DROP_OFFER` with `purpose: "camera"`. It isn't added to the phone's gallery.

### PC notifications (capability `pc.notifications`: the phone allows Palwyn's notifications)
| Type | Dir | Payload |
|---|---|---|
| `PC_NOTIFICATION` | C→P | `{ id ≤ 32, app ≤ 128, title? ≤ 500, text? ≤ 4000 }`: a new Windows notification, shown on the phone. No reply. |
| `PC_NOTIFICATION_REMOVED` | C→P | `{ id }`: it left the PC's Action Center; the phone removes its copy. No reply. |

- Sent only while the PC's setting is on. The PC reads toasts with UserNotificationListener (user consent), polling every 2 s; the title is the toast's first text line and the text the rest.
- The phone drops all of them when the session ends, since it can't see PC changes while disconnected. It never mirrors its own notifications back.

### Dashboard (capability `device`)
| Type | Dir | Payload |
|---|---|---|
| `DEVICE_STATUS` | P→C | `{ wifi, wifiSignal? 0–4, bluetooth?, storageFree, cellSignal? 0–4, cellNetwork? ("5G"\|"LTE"\|"3G"\|"2G"), carrier? }`: sent on connect and whenever one of them changes (signal as bars, so small RSSI changes send nothing). `bluetooth` is absent on phones without it. The mobile fields come from signal-strength events (no permission; the network type needs `READ_PHONE_STATE`) and are absent without a ready SIM. |

### Ring (capability `ring`: always on, no permission)
| Type | Dir | Payload |
|---|---|---|
| `RING` | C→P | `{ on }` → `RING_RESULT`. The phone's ringtone on the alarm stream at full volume, looped; stops on `{ on: false }`, from the phone's notification, or after 60 s. |
| `RING_RESULT` | P→C | `{ ok }` |

### Media (capability `media` = notification access, which Android requires to see other apps' media sessions)
| Type | Dir | Payload |
|---|---|---|
| `MEDIA_STATE` | P→C | `{ active, app?, title?, artist?, playing? }`: the session playing (or the most recent one); `active: false` when nothing is. Sent on connect and on every change. |
| `MEDIA_CONTROL` | C→P | `{ action: play\|pause\|next\|previous\|volumeUp\|volumeDown }` → `MEDIA_RESULT`. Without a session, play/pause/next/previous go out as a media key (resumes the last player). Volume is the phone's media volume. |
| `MEDIA_RESULT` | P→C | `{ ok }` |

### Contacts (capabilities `contacts.read` = `READ_CONTACTS`, `contacts.write` = `WRITE_CONTACTS`)
| Type | Dir | Payload |
|---|---|---|
| `CONTACTS_GET` | C→P | `{ limit 1–500, offset? }` → `CONTACTS` |
| `CONTACTS` | P→C | `{ contacts: [≤ 500 × { id, name ≤ 128, numbers: [≤ 20 × { number ≤ 64, type: mobile\|home\|work\|other, label? ≤ 64 }], emails: [≤ 20 × ≤ 254], photo? }], more }`, by name; `photo: true` when it has a picture |
| `CONTACT_PHOTO_GET` | C→P | `{ id }` → `CONTACT_PHOTO` |
| `CONTACT_PHOTO` | P→C | `{ id, jpeg? }`: the contact's thumbnail (about 96 px), base64; absent when it has none |
| `CONTACT_SAVE` | C→P | `{ id?, name, numbers, emails }` (numbers as above; `label` = a custom label for `other`) → `CONTACT_RESULT` |
| `CONTACT_DELETE` | C→P | `{ id }` → `CONTACT_RESULT` |
| `CONTACT_RESULT` | P→C | `{ ok, id? }`: the saved contact's id |

- Only contacts with a number or an email are listed. A page stops early to stay under ~800 KB; `more` says whether any are left, and the PC asks again from `offset` + the count it got.
- Save without `id` creates a contact in the phone's own storage (no account). With `id` it replaces the contact's name, numbers and emails: they come off every raw contact behind it (phone, Google, SIM) and go on one, the Google one when there is one, so the edit syncs. Delete removes the contact and every raw contact behind it, so synced accounts delete it too.
- The PC keeps the list and pictures in memory only, and reads the list again after every change. Pictures show on the Contacts, Calls and Messages pages and the call card; they can't be changed from the PC.

### Remote: the phone controls the PC (capability `remote`: always on the phone; the PC's own settings decide)
| Type | Dir | Payload |
|---|---|---|
| `PC_REMOTE` | C→P | `{ input, media, commands: [≤ 50 × { id ≤ 32, name ≤ 64 }], app? ≤ 128, title? ≤ 500, artist? ≤ 500, playing?, volume? 0–100, muted? }`: what the PC allows, and (only when `media`) what's playing and its volume. Sent on connect, when a setting changes, and when media or volume change (the PC checks every 2 s). No reply. |
| `REMOTE_POINTER` | P→C | `{ dx, dy }`, each −5000–5000: relative pointer movement in PC pixels. No reply. |
| `REMOTE_BUTTON` | P→C | `{ button: left\|right\|middle, action: click\|down\|up }` (down/up for dragging). No reply. |
| `REMOTE_SCROLL` | P→C | `{ dy }` −12000–12000: wheel units, 120 = one notch, negative scrolls down. No reply. |
| `REMOTE_TEXT` | P→C | `{ text ≤ 1000 }`: typed as Unicode, so the PC's keyboard layout doesn't matter; `\n` is Enter. No reply. |
| `REMOTE_KEY` | P→C | `{ key: enter\|backspace\|delete\|tab\|escape\|left\|right\|up\|down\|home\|end\|pageUp\|pageDown\|f5 }`. No reply. |
| `PC_MEDIA` | P→C | `{ action: playPause\|next\|previous }` for the PC's current media session → `REMOTE_RESULT` (`NOTHING_PLAYING` when there's none) |
| `PC_VOLUME` | P→C | `{ level? 0–100, muted? }`: the default speakers' master volume → `REMOTE_RESULT` |
| `PC_COMMAND` | P→C | `{ id }`: runs a command the user set up on the PC → `REMOTE_RESULT` (`NOT_FOUND` if it was removed) |
| `PC_LOCK` | P→C | `{}` → `REMOTE_RESULT` |
| `REMOTE_RESULT` | C→P | `{ ok }` |

- Input messages (`REMOTE_POINTER` … `REMOTE_KEY`) need the PC's "Mouse and keyboard" setting (off by default); media, volume and lock need "Music, volume and lock" (on by default); a command needs only to be on the PC's list. The PC checks on every message, whatever the phone was told: input it doesn't allow is dropped, and requests are answered `ERROR { code: "REMOTE_OFF" }`.
- The phone sends only while its Remote screen is open, to the one PC picked there. Pointer and scroll movement is merged to at most one message per ~16 ms; everything is sent in order on one thread.
- Commands reach the phone as names only; the command line stays on the PC. They run as `cmd.exe /d /s /c "<command>"` without a window, as the PC's user.
- Synthesized input can't reach the Windows sign-in screen or administrator (UAC) prompts, and not windows of apps running with higher rights than Palwyn.

### Phone screen on the PC (capabilities `screen`: always; `screen.control`: see below)
| Type | Dir | Payload |
|---|---|---|
| `SCREEN_REQUEST` | C→P | `{}` → `SCREEN_RESULT` once the phone shows its notification |
| `SCREEN_RESULT` | P→C | `{ ok }` |
| `SCREEN_STATE` | P→C | `{ state: started\|declined\|stopped }`: the user allowed sharing (the PC then opens the stream), said no in Android's dialog, or sharing ended |
| `SCREEN_PULL` | C→P | `{}`: first frame of a stream connection instead of `HELLO`, like `TRANSFER_PULL`. The phone then writes frames until sharing stops: each a big-endian u32 length and a JPEG (≤ 1 MiB, the frame limit). `ERROR { code: "NOT_SHARING" }` instead when the screen isn't shared with this PC. |
| `SCREEN_TOUCH` | C→P | `{ x1, y1, x2, y2 0–10000, ms 1–10000 }`: across the screen as shown; a tap (same points), long press, or straight swipe. No reply. |
| `SCREEN_KEY` | C→P | `{ key: back\|home\|recents\|backspace\|enter }`. No reply. |
| `SCREEN_TEXT` | C→P | `{ text ≤ 1000 }`: added to the phone's focused text field. No reply. |

- Only the user can start sharing: `SCREEN_REQUEST` shows a notification; tapping it opens Android's own consent dialog (MediaProjection, every time). The capture runs in a `mediaProjection` foreground service, at most 1280 px on the long side, JPEG quality 60; only changed frames are sent and a slow link skips to the newest. It stops when the PC closes the stream, from the phone's notification or Android's own controls, when the link drops, and after 30 s if the PC never opens the stream. With the phone's "Keep screen sharing ready" on, those become a pause instead (SCREEN_STATE `stopped`, capture kept, no frames); a later `SCREEN_REQUEST` from the same PC answers `SCREEN_STATE started` straight away, without a notification or consent.
- Only the PC that asked may open the stream or control the phone. `screen.control` is advertised only while the screen is shared, with the phone's "Let my PC control this phone" switch on (off by default) and its accessibility service on. Taps and swipes go in through `dispatchGesture`, keys through global actions, text by setting the focused field (no real key presses, so no shortcuts).
- Details and limits (secure windows stay black, no sound): [research-screen-mirroring.md](research-screen-mirroring.md).

### Emergency channel (not part of the link)

The emergency screen doesn't use the paired TLS link: the PC starts a helper on the phone through adb (`CLASSPATH=/data/local/tmp/palwyn-emergency.jar app_process / dev.palwyn.emergency.Main <socketName> <tokenHex>`) and reaches it with `adb forward tcp:0 localabstract:<socketName>`.
- The client first sends the 32 raw token bytes (the command line carries them as 64 lowercase hex characters). Anything else closes the connection and ends the helper.
- Phone to PC: frames as on the link's screen stream (big-endian u32 length, then a JPEG of at most 1 MiB).
- PC to phone: one JSON object per line, `{"type": "SCREEN_TOUCH" | "SCREEN_KEY" | "SCREEN_TEXT", "payload": {...}}`, with the payloads above. Input is injected as the shell user, so it also reaches the lock screen.
- One client per helper run; it exits when the client disconnects. Security: [security.md §8b](security.md).

### Search
`CALL_LOG_GET` and `SMS_THREADS_GET` take an optional `query ≤ 100`; the phone filters before paging, so `limit` and `before` work as usual.
- Calls: the contact name or the number matches.
- Conversations: a contact name or number matches, or an SMS in it contains the text (the snippet is then the newest such SMS).
- A number matches when it contains the query's digits (3 or more). A leading 0 is a national prefix, so `0917` also finds `+63 917 …` in numbers written with a country code. Both apps implement the same rule.
- Call names now come from the contacts at request time, not the call log's cached name (which the dialer updates late), so an edited contact shows at once.

New transfer kinds extend `TRANSFER_PULL` / `TRANSFER_PUSH`.

## 7. Versioning rules

- Adding a type, an optional field, a capability or an error code → **same major**.
- Removing or changing the meaning of a field, or making a field required → **new major**; apps support the previous major for at least one release.
- Pairing (`PAIR_*`, see security.md) is versioned separately so a pairing-flow change never strands already-paired devices.
