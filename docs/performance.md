# Palwyn — Performance (Phase 16)

Measured on 2026-09-29: the development PC (Windows 11, 12 logical CPUs) running the **Debug** build (the dev
package is registered at the Debug output; moving it to Release would mean uninstalling it, which deletes its
data), and the reference phone (realme narzo 50, Android 13, debug build) on battery, connected over Wi-Fi.
Idle means connected, no window open, nothing happening on either side.

## Windows

| Measure | Result |
|---|---|
| Startup | process start to "Connected": 0.8 s (phone already discovered on the LAN) |
| Memory | working set 207–216 MB, private 118–126 MB while idle |
| Disk while idle | 0 KB written in 5 min |
| Idle CPU, defaults | **0.44 % of one core** (0.04 % of the whole CPU) after the fix below; 1.09 % before |
| Idle CPU, "Show this PC's notifications on my phone" on | about +1.6 % of one core (off by default) |
| Idle CPU, nothing polling | 0.14 % of one core (keep-alive every 3 s for realme) |

### What was found

Using systematic measurement (turning one thing off at a time):

- **"Music, volume and lock"** (on by default) polls every 2 s for what's playing and the speaker volume, and
  created a new Core Audio device object on every poll. It now reuses it for 30 s: idle CPU 1.09 % → 0.44 %
  of one core. Trade-off: after plugging in headphones, the volume shown on the phone may follow the old
  device for up to 30 s (setting the volume from the phone always uses the current device).
- **"Show this PC's notifications on my phone"** (off by default) asks Windows for all its notifications every
  2 s, because the change event isn't available to desktop apps: about 1.6 % of one core. Left as is; a
  longer interval would cut it proportionally at the cost of slower delivery.

## Android

| Measure | Result |
|---|---|
| Idle CPU | **0.29 % of one core** (86 ticks in 5 min), keep-alive every 3 s |
| Memory | 94.5 MB PSS (Java heap 15.8 MB, native heap 4.5 MB) |
| Battery attribution (Android's own estimate, 4 h 56 m on battery, excluding screen time) | about 6 mAh, almost all the link's partial wake lock (57 min held in total) |
| Network while idle | estimate from the protocol: a `PING`/`PONG` pair every 3 s, roughly 220 bytes plus TCP/IP headers, about 10 MB a day on the local network (no internet data). Android 13's per-app counters don't give a reliable figure for this |
| Photo indexing | none: Palwyn reads MediaStore on request (a page of 100 at a time), it keeps no index |

### The part not measured

Android's estimate doesn't charge the **Wi-Fi high-performance lock** Palwyn holds while connected (3 h 10 m
of the 4 h 56 m here). It's the Phase 0 fix for realme freezing the connection, and on most phones it costs
more battery than the app's own CPU. The honest number needs an unplugged A/B test: the same phone, screen
off, for a few hours each, once with Palwyn connected and once with the PC app closed. Not run yet: it
needs the phone left alone for about 6 hours.
