# Emergency screen and file rescue: design

Date: 2026-10-04. Status: approved in conversation, awaiting review of this written spec.

## Goal

If the phone's screen breaks (or the phone can't be used for another reason) while the phone still runs, the user can see and control it from the PC and copy their files off it, **without any prompt on the phone**. Palwyn's normal screen sharing can't do this: Android asks for consent on the phone for every MediaProjection session.

This is the first of three related features. The other two are separate specs:
- Auto wireless debugging: Palwyn turns wireless adb on while connected, so this feature also works without a cable.
- Auto backup to PC: for phones that won't turn on at all.

## What the user decided

- Built into Palwyn for Windows, for all users, with scrcpy's phone-side code copied into Palwyn, adapted, and credited (Apache-2.0).
- Approach 1: port scrcpy's capture and input code into a Palwyn phone helper that speaks Palwyn's existing screen protocol, and reuse Palwyn's existing Phone screen window on the PC. Smooth H.264 video is out of scope.
- Entry: a button, plus a hint when the normal link fails but the phone is visible over USB.
- Includes "Rescue files" (shared storage over adb). SMS and contacts export belong to the auto-backup feature.

## Preconditions and limits (stated in the docs and the UI)

- It works only if it was prepared **before** the accident: USB debugging on, and this PC allowed once ("Allow USB debugging from this computer"). A phone that never allowed this PC can't be reached; the UI says so plainly.
- Cable only, for now. It works over Wi-Fi as soon as wireless adb is connected (feature 2), without changes, since adb is transport-agnostic.
- After a restart, the lock screen is shown and the user types the PIN with the PC keyboard. Palwyn never stores or enters a PIN itself.
- Secure windows (FLAG_SECURE: banking apps, DRM video) stay black. Other apps' private data can't be rescued (Android limit).
- Video is JPEG frames, max 1280 px on the long side, like normal sharing: usable, less smooth than real video.

## Architecture

### Phone helper (new): `apps/android/emergency`

- A small Gradle module that produces `palwyn-emergency.jar` (a dex jar). It isn't an installed app: the PC pushes it to `/data/local/tmp/` and runs it with `app_process` as the shell user. That's why it needs no consent, and why it works even when Palwyn's phone app is frozen or stopped.
- Code ported from scrcpy's server: the hidden-API wrappers for display capture (SurfaceControl / DisplayManager virtual display) and input injection (InputManager). Each copied file keeps scrcpy's copyright header and gets a line "Modified for Palwyn: <what changed>" (Apache-2.0 §4b).
- Output: frames in Palwyn's existing stream format (big-endian u32 length, then a JPEG ≤ 1 MiB; quality 60; only changed frames; a slow reader skips to the newest), via an `ImageReader` on the virtual display.
- Input: the same messages as Palwyn's `SCREEN_TOUCH` / `SCREEN_KEY` / `SCREEN_TEXT` (docs/protocol.md), as one JSON object per line. Text is injected as key events, so it also works on the lock screen's PIN pad.
- Channel: a localabstract socket with a random name, reached from the PC through `adb forward tcp:0 localabstract:<name>`. The helper accepts exactly one client, which must send the random 32-byte token it was started with as its first bytes. It exits when that client disconnects or the token is wrong.

### PC side: Palwyn Windows

1. **Bundled adb.** `adb.exe`, `AdbWinApi.dll` and `AdbWinUsbApi.dll` from Google's platform-tools are downloaded at build time (pinned version, SHA-256 checked) into the app's assets. `UsbLink.FindAdb` prefers the bundled copy, which also makes the existing USB-cable link work without the Android SDK. adb is Apache-2.0; the SDK licence leaves open-source components to their own licence.
2. **Screen source split.** `ScreenWindow` today talks to `LinkManager` directly. It gets a small source abstraction: open a frame stream, send touch, key and text. There are two implementations: the existing link source (with Android consent) and the new emergency source. The window's look and controls stay the same; the title says "Phone screen (emergency)" in emergency mode.
3. **Emergency source.**
   - Pick the device: the authorized USB devices from `adb devices`; if there are several, ask which.
   - Push the jar, start it through `adb shell app_process` with the token and socket name (the adb process is kept and owned by Palwyn), forward, connect, send the token.
   - On close: kill the process, remove the forward, delete the jar from the phone.
4. **Rescue files.**
   - A window opened from the same places: pick a folder (default `Pictures\Palwyn rescue <date>`), then copy `/sdcard` shared folders (DCIM, Pictures, Movies, Music, Download, Documents, Android/media) with `adb pull`, file by file, with progress and Cancel.
   - Files already present with the same size and date are skipped, so it can run again.
   - Errors (disk full, unreadable file) are counted, and copying continues; the end shows "copied X, skipped Y, failed Z".
5. **Hint.** When the link has been waiting for the paired phone for 20 s or more and adb sees an authorized USB device, Palwyn shows a notification once per episode: "Phone not responding? Open the emergency screen." It has a button that opens the window.
6. **Readiness in Settings.** "Emergency access: ready (this PC is allowed)" or "not ready", with the steps: turn on USB debugging, plug in once, tap "Always allow from this computer". It's checked when a USB device appears and when Settings opens.
7. **Entry points.** "Emergency screen" and "Rescue files" in the tray menu and on Home, enabled when adb sees an authorized device.

## Security

- The trust boundary is adb authorization, which the user granted on the phone. Palwyn opens no new access: any program on an allowed PC can already do this with adb.
- The per-session random token and the random socket name stop other PC programs (the forwarded port is on 127.0.0.1) and other phone apps (abstract sockets are world-connectable) from attaching.
- Nothing is recorded. Logs say "Emergency screen opened/closed" and rescue counts only, never file names or screen content.
- docs/security.md gets a section on emergency access and its threat model.

## Errors

| Situation | Shown |
|---|---|
| No authorized USB device | "Plug in your phone with a USB cable. It needs USB debugging on and must have allowed this PC before." |
| Device listed as `unauthorized` | "This phone hasn't allowed this PC. With a broken screen that can't be done anymore." |
| Helper fails to start | "Couldn't start the emergency screen on this phone", with details in the log |
| Cable pulled or adb lost | "Disconnected" with Retry |
| Several devices | A picker with model names |

## Build and release

- The Gradle module builds `palwyn-emergency.jar`. The Windows build copies it into the app's assets; `run-dev.ps1` builds it when missing or older than its sources.
- The release workflow's Windows job builds the jar (the runner has the Android SDK and Java) and downloads the pinned platform-tools before `dotnet build`.
- Credits:
  - `third_party/scrcpy/LICENSE` and a README naming the source version (v4.1) and the copied files
  - NOTICE entries for scrcpy (Copyright Genymobile, Copyright Romain Vimont, Apache-2.0) and adb (The Android Open Source Project, Apache-2.0)
  - The feature is called "Emergency screen", never by scrcpy's name

## Testing

- Unit tests:
  - parsing `adb devices -l` with authorized, unauthorized and several devices
  - the rescue skip rule (same size and date)
  - the helper's token check (correct, wrong, missing)
- On the reference phone (realme narzo 50, Android 13), with Palwyn's phone app force-stopped:
  - the emergency screen opens with no prompt on the phone
  - taps, swipes, Back/Home/Recents and typing work
  - closing removes the jar
  - Rescue copies a test folder, and a second run skips it
- The lock screen shows on the PC. The user types the PIN; the agent never does.
- Docs: feature-matrix, security.md, install.md (a "Prepare for emergencies" section), NOTICE.

## Out of scope

Smooth H.264 video, audio, emergency access over Wi-Fi without feature 2, SMS and contacts export, and unlocking or bypassing the lock screen.
