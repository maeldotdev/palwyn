# Draft: several phones connected at once

Status: **idea, not started** (2026-09-28). Planned before or after batch 5; nothing below is built yet.

## Goal

One PC connected to one or more Android phones at the same time, with a tab per phone. Each tab shows only that phone's data, so nothing from one phone ever appears under another.

## Where things stand (batch 6)

- Several phones can be paired, but only one is connected at a time (`AppSettings.ActivePhone`, Settings > Your phones > Use this phone).
- The PC app has one `LinkManager` holding one `LinkEngine`. About 20 files and roughly 70 call sites use `App.Current.Link` as "the phone", and page state, notifications and caches assume a single phone.
- The phone side already accepts several PCs (one session per PC), so it needs no changes.
- Already kept per phone: notification history, contact pictures, photo previews and MMS attachment copies.

## Plan

1. **Split `LinkManager`.** It keeps this PC's identity, discovery, pairing and the paired-phone store. A new `PhoneLink` holds one engine, that phone's state (device info, status, media, live notifications, current call) and its request methods. There's one `PhoneLink` per paired phone, all connecting at once; discovery results go to the matching one.
2. **Phone tabs.** A switcher at the top of the navigation pane lists the paired phones with a connected dot. `App.Current.Phone` is the selected `PhoneLink`. Switching re-creates the current page, so no state from the other phone survives.
3. **Events carry their phone.** `PhoneLink` raises calls, SMS, notifications, clipboard and drops with itself as the source:
   - Windows notifications and the call card name the phone when more than one is connected.
   - Notification arguments include the device id, so opening or replying first switches to that phone and then acts through it.
   - Toast groups and "seen here" state are keyed by device id and thread id.
4. **Outgoing from the PC** (clipboard sync, PC notifications on the phone, Quick Drop) goes to the selected phone only. **Incoming** (shares, clipboard sends, camera photos) is received through the phone that sent it.
5. **Global behaviour:**
   - Keep the PC awake while any phone is connected.
   - Pause media during a call on any phone.
   - The tray shows the selected phone plus a count of the others.
6. **Removing a phone** stops only its link. Pairing a new phone adds a link instead of replacing one.

## Open decision

Should clipboard sync and PC notifications go to every connected phone rather than the selected one? The plan above uses "selected only", which gives the most separation.

## Risks and testing

- The main risk is a missed call site showing or sending through the wrong phone. Add Core tests that feed two phones' events and check they never cross.
- Real two-phone testing needs a second Android phone. Without one, only the PC-side tests can run.
- Effort: one large batch, about batches 2 and 4 combined.
