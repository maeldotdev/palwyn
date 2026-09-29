# Palwyn — Reliability (Phase 14)

Run on 2026-09-29 against an **Android 16 (API 36) emulator** (Google APIs x86_64 image r07, run from a
local folder and deleted after the run), with the Windows app on the development PC. The real phone
(realme narzo 50, Android 13) was tested feature by feature in earlier phases; Phase 0 covers its
background behaviour (realme UI freezes apps, hence the 3 s keep-alive there).

## Setup

The emulator sits behind its own NAT, so discovery can't see it. It was paired with the debug-only
`--pair-dev=127.0.0.1:47802` switch (normal QR pairing, the invite opened with `adb shell am start -d`),
then reached through the emulator's port redirect (`adb emu redir add tcp:47803:47800`), so the link runs
over the emulated network card like Wi-Fi, not over adb. Runtime permissions were granted with
`pm grant` on the emulator, as a user would in the app's setup.

## Results

| Test | How | Result |
|---|---|---|
| App crash (phone) | `am crash` | **Pass**: Android restarts the service (sticky); PC reconnected 2.8 s after the crash |
| Force stop (phone) | `am force-stop` | Comes back by itself within ~1 s: Android re-binds the notification-access service, which restarts the app. Without notification access it would stay stopped until opened (Android's rule for force stop) |
| Phone restart | `adb reboot` | **Pass**: service started from `BOOT_COMPLETED` on Android 16; PC reconnected 48 s after boot (its retry backoff, up to 30 s; on Wi-Fi, rediscovery retries at once) |
| Phone sleep / Doze | screen off, `deviceidle force-idle`, 90–150 s | **Pass**, with and without the battery-optimisation exemption: link stayed up, an SMS reached the PC 0.3–0.7 s after arriving |
| Battery saver + restricted bucket | `low_power 1`, standby bucket `restricted` (45) | **Pass**: link stayed up; SMS and a notification reached the PC |
| "Restricted" battery setting | appop `RUN_ANY_IN_BACKGROUND ignore` | **Pass**: SMS in 0.9 s; after a crash the app restarted and reconnected in 3.4 s |
| Network loss | Wi-Fi and data off for 60 s | **Pass**: loss noticed after 53 s (three missed keep-alives), reconnected 1.5 s after the network returned |
| Many notifications | 200 posted in 3 s | **Pass**: Android kept 49 (its limit is 50 per app; the rest are rejected by Android) and every one reached the PC |
| Large message history | 385 SMS in 20 conversations | **Pass**: conversation list under 2 s, a conversation opens, search finds a message by its text |
| Large photo transfer | 100 photos, 422 MB, Select all > Save to Pictures | **Pass**: all 100 saved, byte-identical (SHA-256), 86 s (4.9 MB/s: the emulator's network; the real phone measured ~20 MB/s in Phase 10) |
| Long call | incoming call answered and held 10 min | **Pass**: ringing, active and ended shown on the PC; no link drop during the call |
| Removing a phone | Settings > Remove | **Pass**: the other phone took over and connected; the removed phone's notification history was deleted at once |

PC memory during the run: 217–252 MB (for Phase 16).

## Not tested

- **PC restart and PC sleep**: not run, on the owner's instruction (the PC is not to be restarted). The
  code path exists (resume from sleep reconnects on `SystemSuspendStatus` resume; start with Windows is a
  setting).
- **Bluetooth disconnect**: the emulator has no Bluetooth, and Palwyn's link doesn't use Bluetooth.
- **Other Android versions** (10–15) and other manufacturers' background limits: only Android 16 was run,
  by choice. realme (Android 13) is covered by earlier phases.
- **PC app crash**: the PC app is not restarted automatically after a crash; it starts again at sign-in
  (if "Start with Windows" is on) or when opened, then connects within a second.

## Known limits found

- A dead network is noticed only after three missed keep-alives (about 50 s at the default 15 s interval,
  about 14 s on realme and other freezing OEMs at 3 s). Faster detection costs battery.
- With a phone reached by address (VPN, emulator), a restart can take up to the 30 s retry cap to
  reconnect, since discovery can't prompt an immediate retry.

## Test infrastructure notes

- Headless emulator (`-no-window`) with `-gpu auto` froze after `screencap` on this PC; `-gpu
  swiftshader_indirect` was stable.
- The emulator's modem drops SMS sent faster than about 3 per second (`adb emu sms send`).
