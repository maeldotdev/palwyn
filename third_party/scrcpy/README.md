# scrcpy (parts of its server, modified)

Palwyn's emergency-screen helper (`apps/android/emergency`) includes code from **scrcpy v4.1**:
https://github.com/Genymobile/scrcpy (tag `v4.1`, `server/src/main/java/com/genymobile/scrcpy/`).

    Copyright (C) 2018 Genymobile
    Copyright (C) 2018-2026 Romain Vimont

    Licensed under the Apache License, Version 2.0 (the "License"); see LICENSE in this folder.

## Files taken from scrcpy

| Palwyn file (`apps/android/emergency/src/main/java/dev/palwyn/emergency/scrcpy/`) | scrcpy source | Changes |
|---|---|---|
| `Workarounds.java` | `Workarounds.java` | package renamed; only the ActivityThread setup and the system context kept; logging through System.err |
| `FakeContext.java` | `FakeContext.java` | package renamed; content-provider resolver removed |
| `DisplayManager.java` | `wrappers/DisplayManager.java` | package renamed; only `getDisplayInfo()` and `createVirtualDisplay()` kept; DisplayInfo trimmed |
| `SurfaceControl.java` | `wrappers/SurfaceControl.java` | package renamed; only the display-mirroring calls kept; `createDisplay()` chooses the secure flag as scrcpy's ScreenCapture does |
| `InputManager.java` | `wrappers/InputManager.java` | package renamed; only `injectInputEvent()` kept |

Each file says so in its header. The rest of the helper (`Main`, `Capture`, `Input`, `Handshake`) is Palwyn's own code, following scrcpy's approach (a mirrored display and injected input from an `app_process` started by adb). Palwyn's feature is called "Emergency screen"; it isn't scrcpy and isn't endorsed by its authors.
