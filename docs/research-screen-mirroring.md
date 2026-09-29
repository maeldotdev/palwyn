# Research: phone screen on the PC (batch 8)

Status: research, 2026-09-29. Steps 1 and 2 of the plan (view with option B, and control) were then built in batch 8; see protocol.md "Phone screen on the PC". Step 3 (H.264) is not built. Goal: see the phone's screen live in a Palwyn window on the PC, and maybe control it from there.

## 1. Capturing the screen on Android

The only way for a normal app is **MediaProjection**, and it comes with firm limits:

- **Consent every time.** Each session needs the user to tap Allow in Android's own "Start recording or casting?" dialog. There's no "don't ask again": it was removed in Android 10. From Android 14 a consent token works for one session only.
- **It must start on the phone.** Android 10+ stops background apps from opening screens, so a click on the PC can't open that dialog. It has to be a notification the user taps, the same pattern as "Take a photo" (batch 4), or the app itself.
- **A foreground service of type `mediaProjection`** runs for the whole session, with its notification.
- **What stays black or hidden:**
  - Windows marked `FLAG_SECURE`: banking apps, password fields in some apps, and DRM video such as Netflix.
  - On Android 14+, "share one app" leaves out the status bar, navigation bar and notifications.
  - Android 15 also hides notifications, and apps that just showed a one-time code, during any screen share.
- **Status bar chip.** From Android 15 QPR1 a large status-bar chip shows while the screen is shared; tapping it stops sharing, and locking the phone stops it too.
- **No sound.** Sound would need a second API (AudioPlaybackCapture, Android 10+) plus the microphone permission, and many apps opt out of it. Not planned.
- **The test phone:** realme narzo 50, Android 13. It has the consent-per-session rule, but not the Android 14/15 extras.

## 2. Getting the frames to the PC

| Option | Phone side | PC side | Latency and quality | Effort |
|---|---|---|---|---|
| **A. H.264 video** | `MediaCodec` hardware encoder fed straight from the capture surface: very little CPU or battery | Needs a decoder. **A1** Windows `MediaStreamSource` + `MediaPlayerElement`: easy, but it's reported to buffer up to about 3 s even with `RealTimePlayback`, which is unusable for control. **A2** Media Foundation H.264 decoder (built into Windows) with low-latency mode, drawn into the window: about 50–100 ms, but a lot of COM interop | Best: 30–60 fps, sharp, around 2–6 Mbit/s | A1 small but too laggy; A2 large |
| **B. JPEG frames** | Frames from `ImageReader`, compressed to JPEG on the CPU | Decode and draw each frame: a few lines with the built-in decoder | Around 10–20 fps at about 720p; blocky on motion; 100–200 ms; about 1–2 MB/s on Wi-Fi; the phone gets warm | Small |

Both options travel on a second connection, like a file transfer, so a video stream never delays calls or messages. It's the same TLS encryption and pinning as everything else. Nothing goes through a server, so there's no cost.

**Recommendation:** build it in stages.
1. Start with **B** to ship something that works and learn how the test phone behaves (frame rate, heat, the realme power manager).
2. Move to **A2** once the window, controls and protocol are settled.

A1 is ruled out by the buffering reports.

## 3. Controlling the phone from the PC (optional)

A normal app can touch the phone's screen only through an **AccessibilityService**:
- Taps and swipes go in with `dispatchGesture`.
- Back, Home and Recents are built-in actions.
- Typing works as "set the text of the focused field". There are no real key presses, so shortcuts don't work.

The costs:
- **Permission:** the user turns on Palwyn under Accessibility. On Android 13+ an app installed outside a store is first blocked as a "restricted setting", and the user has to allow it in App info. That app then has broad power over the phone.
- **Google Play:** Play allows the Accessibility API only for real accessibility tools. It fine for a GitHub or sideloaded build, but it could block a Play release.
- **Security:** a paired PC could then tap anything on the phone. That needs its own off-by-default switch on the phone, and must only work while mirroring is on.

The alternative scrcpy uses (adb over USB or wireless debugging) needs developer options on the phone. That's not reasonable for normal users, so it's not recommended.

## 4. Plan if approved

1. **View only (option B):**
   - PC: a "Phone screen" button on the Photos page or a new page, which asks the phone.
   - Phone: a notification leads to Android's consent dialog, and frames then go to a Palwyn window on the PC.
   - The window scales and handles rotation, and there's a Stop button on both sides.
2. **H.264 (option A2):** replaces the frames; same window.
3. **Control (only if wanted):** clicks and drags become taps and swipes, with Back/Home/Recents buttons and typing into the focused field. It needs the Accessibility switch on the phone.

**Risks and unknowns:**
- The realme power manager may throttle the encoder when the phone gets warm.
- The phone must stay unlocked while mirroring.
- Rotation and odd screen sizes need testing.
- It's heavy on battery (worse with option B).

## Sources
- [Android: Media projection](https://developer.android.com/media/grow/media-projection)
- [Android 15 behavior changes](https://developer.android.com/about/versions/15/behavior-changes-all)
- [Android 15 screen-sharing protections (Android Authority)](https://www.androidauthority.com/android-15-scam-fraud-protection-3443191/)
- [Android 15 QPR1 status bar chip (Android Authority)](https://www.androidauthority.com/android-15-qpr1-stop-screen-shares-3489652/)
- [MediaStreamSource H.264 delay (Microsoft forum)](https://social.msdn.microsoft.com/Forums/en-US/0b55d610-df4a-4a52-9fdf-ffee837cd17c/mediaelement-with-customized-h264-mediastreamsource-to-decode-and-display-the-raw-h264-has-a-delay)
- [Optimizing MediaElement for live streaming (Microsoft Q&A)](https://learn.microsoft.com/en-us/answers/questions/1532586/how-to-optimize-mediaelement-for-live-streaming)
- [CODECAPI_AVLowLatencyMode (Media Foundation)](https://learn.microsoft.com/en-ca/windows/win32/medfound/codecapi-avlowlatencymode)
