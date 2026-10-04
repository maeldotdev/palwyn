# Emergency Screen and File Rescue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user see and control the phone from the PC, and copy its files off, with no prompt on the phone, through adb, so it still works with a broken screen.

**Architecture:**
- A small phone helper (ported from scrcpy's server, Apache-2.0) is pushed and started through adb as the shell user. It captures the screen without MediaProjection and injects input.
- It serves Palwyn's existing frame format (u32 BE length + JPEG) and existing control messages over an adb-forwarded abstract socket, guarded by a one-time token.
- The PC reuses `ScreenWindow` through a new screen-source abstraction. Bundled adb also powers Rescue files and the existing USB link.

**Tech Stack:** Java (Android helper, app_process), Kotlin/Gradle 9.6 + AGP 9.4.1, C# .NET 10 / WinUI 3, xunit, JUnit 4, adb (platform-tools r37.0.1).

**Spec:** `docs/superpowers/specs/2026-10-04-emergency-screen-design.md`

## Global Constraints

- **Android helper:** minSdk 29, targetSdk 36. Package `dev.palwyn.emergency`. Shipped as `palwyn-emergency.jar`, pushed to `/data/local/tmp/palwyn-emergency.jar`, run with `CLASSPATH=/data/local/tmp/palwyn-emergency.jar app_process / dev.palwyn.emergency.Main <socketName> <tokenHex>`.
- **Frames:** big-endian u32 length + JPEG, each ≤ 1 MiB (1,048,576 bytes); JPEG quality 60; long side ≤ 1280 px; only changed frames; a slow reader gets the newest frame, not a backlog.
- **Control:** one JSON object per line, `{"type":"SCREEN_TOUCH"|"SCREEN_KEY"|"SCREEN_TEXT","payload":{…}}`, with payloads exactly as in docs/protocol.md:
  - `SCREEN_TOUCH`: x1, y1, x2, y2 0–10000, ms 1–10000
  - `SCREEN_KEY`: key back|home|recents|backspace|enter
  - `SCREEN_TEXT`: text ≤ 1000 chars
- **Token:** 32 random bytes per session, passed as 64 lowercase hex chars. The client sends the 32 raw bytes first; a wrong or missing token (5 s timeout) closes the connection and exits the helper. One client per helper run.
- **Socket name:** `palwyn-emergency-<16 random hex>`. Forward with `adb forward tcp:0 localabstract:<name>` (adb prints the port).
- **adb:** platform-tools **r37.0.1**, `https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip`, SHA-256 `45f4d63113e895ebde0c90f194099a4676b6ac653bd28d54314a9e022bbc1a99`. Files `adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`, `NOTICE.txt` go into `Assets\adb\`. They are generated, not committed.
- **Cable only:** device selection keeps UsbLink's filter (no `:`, no `._adb-`, no `emulator-` serials).
- **Logs:**
  - "Emergency screen opened/closed (<model>)"
  - "Rescue: copied X, skipped Y, failed Z"
  - never file names, paths or screen content
- **Credits:** every ported scrcpy file keeps its original header plus `// Modified for Palwyn: <what changed>`. The feature is never named "scrcpy" in the UI.
- **UI copy** is from the spec's Errors table, verbatim.
- **Agent safety:**
  - Never type, store or send the user's PIN.
  - Never accept Android dialogs on the user's phone.
  - Never restart the PC.

## Review Focus

- The phone screen is off or locked when the session starts: the helper wakes the display (KEYCODE_WAKEUP) so the user sees the lock screen. Pinned in Task 3's on-device check.
- The phone rotates mid-session: the frame size changes and the window keeps mapping clicks correctly, because touches are 0–10000 of the frame shown. Pinned in Task 3 (the helper rebuilds the display on rotation) and Task 5's on-device check.
- Rescue paths with spaces, quotes, apostrophes or non-ASCII names (`IMG 2026'01.jpg`, `ñ.jpg`): they are listed and pulled correctly. Pinned in the Task 1 parser test and the Task 6 shell-quoting test.
- The user quits Palwyn, or closes the window, while a session or rescue runs: the helper process is killed, the forward removed and the jar deleted, with no orphaned adb children. Pinned in Task 4's dispose check.
- The normal USB link (forward 47801) and the emergency forward run at the same time: they must not disturb each other (tcp:0, separate forwards). Pinned in Task 4's on-device check.

---

### Task 1: Core: adb device list and rescue plan parsing

**Files:**
- Create: `apps/windows/src/Palwyn.Core/Adb.cs`
- Modify: `apps/windows/src/Palwyn.App/Link/UsbLink.cs:70-90` (use the parser instead of inline splitting)
- Test: `apps/windows/tests/Palwyn.Core.Tests/AdbTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AdbDevice(string Serial, string State, string? Model)` with `bool IsUsb` (serial has no `:`, no `._adb-`, doesn't start with `emulator-`) and `bool IsReady => State == "device"`
  - `public static class AdbOutput`, containing:
    - `IReadOnlyList<AdbDevice> ParseDevices(string devicesL)`: parses `adb devices -l`, CRLF or LF, skips the header and `* daemon` lines, takes `model:` from the tail
    - `IReadOnlyList<RemoteFile> ParseStat(string statOutput)`: lines `<size> <mtimeSeconds> <path>`, the path being the rest of the line
  - `public sealed record RemoteFile(string Path, long Size, long MtimeSeconds)`
  - `public static bool ShouldCopy(RemoteFile remote, long? localSize, DateTimeOffset? localMtime)`: true unless the sizes are equal and |mtime difference| ≤ 2 s

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void ParsesDevicesWithModelAndStates() {
    var d = AdbOutput.ParseDevices("List of devices attached\r\nQ8G6 device usb:1-1 product:RMX3286T2 model:RMX3286 device:RE54B4L1 transport_id:4\r\nAB12 unauthorized usb:1-2 transport_id:5\r\n192.168.1.5:5555 device product:x model:Y transport_id:6\r\n");
    Assert.Equal(3, d.Count);
    Assert.Equal(("Q8G6", true, "RMX3286", true), (d[0].Serial, d[0].IsReady, d[0].Model, d[0].IsUsb));
    Assert.False(d[1].IsReady);
    Assert.False(d[2].IsUsb);
}
[Fact] public void ParsesStatPathsWithSpacesAndQuotes() {
    var f = AdbOutput.ParseStat("1234 1790000000 /sdcard/DCIM/IMG 2026'01.jpg\n5 1790000001 /sdcard/Download/ñ.pdf\n");
    Assert.Equal(new RemoteFile("/sdcard/DCIM/IMG 2026'01.jpg", 1234, 1790000000), f[0]);
    Assert.Equal("/sdcard/Download/ñ.pdf", f[1].Path);
}
[Fact] public void SkipsSameSizeAndTimeWithinTwoSeconds() {
    var r = new RemoteFile("/sdcard/a.jpg", 10, 1790000000);
    Assert.False(AdbOutput.ShouldCopy(r, 10, DateTimeOffset.FromUnixTimeSeconds(1790000002)));
    Assert.True(AdbOutput.ShouldCopy(r, 11, DateTimeOffset.FromUnixTimeSeconds(1790000000)));
    Assert.True(AdbOutput.ShouldCopy(r, null, null));
}
```

- [ ] **Step 2: Run the tests and see them fail**

Run: `dotnet test apps/windows/tests/Palwyn.Core.Tests --filter AdbTests`
Expected: compile error, `AdbOutput` not defined.

- [ ] **Step 3: Implement the `Adb.cs` types above, then switch `UsbLink.Refresh` to `AdbOutput.ParseDevices(Adb("devices -l"))` filtered by `IsUsb`.**

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test apps/windows/tests/Palwyn.Core.Tests`
Expected: all pass (137 existing + 3 new).

- [ ] **Step 5: Commit** `Core: parse adb device lists and rescue file listings`

### Task 2: Bundled adb and build plumbing

**Files:**
- Create: `apps/windows/tools/fetch-deps.ps1`
- Modify:
  - `apps/windows/tools/run-dev.ps1` (call fetch-deps before building)
  - `apps/windows/src/Palwyn.App/Link/UsbLink.cs` `FindAdb()` (bundled first: `Path.Combine(AppContext.BaseDirectory, "Assets", "adb", "adb.exe")`)
  - `.gitignore` (add `apps/windows/src/Palwyn.App/Assets/adb/` and `apps/windows/src/Palwyn.App/Assets/Emergency/`)
  - `.github/workflows/release.yml` (Windows job: `actions/setup-java@v4` temurin 21, then `pwsh apps/windows/tools/fetch-deps.ps1` before the MSIX build)

**Interfaces:**
- Produces:
  - `fetch-deps.ps1 [-SkipEmergency]`. It downloads and verifies the pinned platform-tools zip into `Assets\adb\` (skipped when `Assets\adb\adb.exe` already exists with the right size). Unless `-SkipEmergency` is given, it runs `gradlew :emergency:assembleRelease` in `apps/android` and copies `emergency/build/outputs/apk/release/emergency-release-unsigned.apk` to `Assets\Emergency\palwyn-emergency.jar`.
  - The script throws on an SHA-256 mismatch.

- [ ] **Step 1: Write `fetch-deps.ps1` (adb part only for now), with the URL and SHA-256 from Global Constraints. Make it skip the Gradle part while `apps/android/emergency` doesn't exist yet.**
- [ ] **Step 2: Run it and check the result**

Run: `pwsh apps/windows/tools/fetch-deps.ps1`
Expected: `Assets\adb\adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`, `NOTICE.txt` exist. Running it a second time downloads nothing.

- [ ] **Step 3: Change `FindAdb()` to prefer the bundled adb and log `USB: adb from bundled|sdk|path` once at start; add the fetch-deps call to `run-dev.ps1`.**
- [ ] **Step 4: Verify the app uses the bundled adb**

Run: `apps/windows/tools/run-dev.ps1`, then plug the phone in.
Expected: the PC log shows `USB: adb from bundled` and `USB: checked, 1 on USB`, and the link switches to USB.

- [ ] **Step 5: Verify a bad checksum fails**

Edit the expected SHA-256 by one character and run the script.
Expected: it throws "SHA-256 mismatch". Revert the edit.

- [ ] **Step 6: Commit** `Bundle adb (platform-tools r37.0.1) and fetch build dependencies`

### Task 3: Phone helper: ported scrcpy capture and input, plus the handshake

**Files:**
- Create:
  - `apps/android/emergency/build.gradle.kts` (`com.android.application`, namespace and applicationId `dev.palwyn.emergency`, minSdk 29, compileSdk 37, targetSdk 36, Java 17, `isMinifyEnabled = false`, no Kotlin, test dependency `junit:junit:4.13.2`)
  - `apps/android/emergency/src/main/AndroidManifest.xml` (empty `<application/>`)
  - `apps/android/emergency/src/main/java/dev/palwyn/emergency/scrcpy/`: `ServiceManager.java`, `DisplayManager.java`, `SurfaceControl.java`, `InputManager.java`, `FakeContext.java`, `Workarounds.java`, `DisplayInfo.java`, `Size.java`, ported from scrcpy **v4.1** `server/src/main/java/com/genymobile/scrcpy/…`, trimmed to what is used, with headers kept plus the "Modified for Palwyn" line
  - `apps/android/emergency/src/main/java/dev/palwyn/emergency/Handshake.java`
  - `apps/android/emergency/src/main/java/dev/palwyn/emergency/Capture.java`
  - `apps/android/emergency/src/main/java/dev/palwyn/emergency/Input.java`
  - `apps/android/emergency/src/main/java/dev/palwyn/emergency/Main.java`
  - `apps/android/emergency/src/test/java/dev/palwyn/emergency/HandshakeTest.java`
  - `third_party/scrcpy/LICENSE` (Apache-2.0 text from scrcpy v4.1)
  - `third_party/scrcpy/README.md` (source: Genymobile/scrcpy v4.1; copyright Genymobile 2018, Romain Vimont 2018-2026; list of ported files and what changed)
- Modify: `apps/android/settings.gradle.kts` (`include(":emergency")`)

**Interfaces:**
- Produces:
  - `Handshake.check(InputStream in, byte[] token, int timeoutMs) -> boolean`: reads exactly `token.length` bytes, constant-time compare
  - `Capture(OutputStream out)`:
    - `start()` creates a virtual display (DisplayManager.createVirtualDisplay on API ≥ 34, SurfaceControl.createDisplay below, as scrcpy's ScreenCapture does) into an `ImageReader` (RGBA_8888, 2 buffers) sized to the display, scaled so the long side ≤ 1280
    - It writes changed frames as length + JPEG(60), dropping frames while a write is in progress, and rebuilds when `DisplayInfo` rotation or size changes (checked every 500 ms)
  - `Input.handle(String jsonLine)`:
    - SCREEN_TOUCH → injected MotionEvent DOWN / MOVE every 16 ms over `ms` / UP, mapped from 0–10000 to display pixels
    - SCREEN_KEY back|home|recents|backspace|enter → KEYCODE_BACK / HOME / APP_SWITCH / DEL / ENTER
    - SCREEN_TEXT → `KeyCharacterMap.load(VIRTUAL_KEYBOARD).getEvents(chars)` injected
    - unknown types ignored
  - `Main.main(String[] args)` with args `<socketName> <tokenHex>`:
    - opens `LocalServerSocket(socketName)` and accepts one client
    - `Handshake.check(…, 5000)` or exits
    - injects KEYCODE_WAKEUP, starts Capture, reads control lines into Input until EOF, then exits(0)
    - prints nothing except errors to stderr

- [ ] **Step 1: Write the failing test**

```java
@Test public void acceptsExactToken() { assertTrue(Handshake.check(new ByteArrayInputStream(T), T, 1000)); }
@Test public void rejectsWrongToken() { byte[] w = T.clone(); w[31] ^= 1; assertFalse(Handshake.check(new ByteArrayInputStream(w), T, 1000)); }
@Test public void rejectsShortToken() { assertFalse(Handshake.check(new ByteArrayInputStream(new byte[16]), T, 1000)); }
// T = 32 bytes 0..31
```

- [ ] **Step 2: Run it and see it fail**

Run (from `apps/android`, with `JAVA_HOME='D:\Android Studio\jbr'` and `TEMP=C:\pbtmp`): `./gradlew :emergency:testReleaseUnitTest`
Expected: compile error, `Handshake` not defined.

- [ ] **Step 3: Implement `Handshake`, port the scrcpy wrappers, then write `Capture`, `Input` and `Main` to the interfaces above.**
- [ ] **Step 4: Run the unit tests**

Run: `./gradlew :emergency:testReleaseUnitTest :emergency:assembleRelease`
Expected: 3 tests pass, and `emergency-release-unsigned.apk` exists.

- [ ] **Step 5: On-device smoke test** (reference phone, Palwyn's phone app force-stopped; the screen locked first)
  1. Push the APK as `/data/local/tmp/palwyn-emergency.jar`.
  2. Start it with a known token.
  3. Run `adb forward tcp:0 localabstract:<name>`.
  4. From PowerShell, open a `TcpClient`, send the 32 token bytes, and read 4 + n bytes.

  Expected:
  - The first frame's bytes start `FF D8`.
  - **No prompt appears on the phone.**
  - The display wakes to the lock screen.
  - A wrong token closes the socket.
  - Rotating the phone gives frames with swapped dimensions.
- [ ] **Step 6: Commit** `Add the emergency helper (screen capture and input via adb, ported from scrcpy v4.1)`

### Task 4: PC emergency session

**Files:**
- Create: `apps/windows/src/Palwyn.App/Emergency/EmergencySession.cs`
- Modify: `apps/windows/tools/fetch-deps.ps1` (remove the "module missing" skip)

**Interfaces:**
- Consumes: `AdbDevice` and `AdbOutput.ParseDevices` (Task 1), the bundled adb path (Task 2), the helper contract (Global Constraints).
- Produces:
  - `sealed class EmergencySession : IAsyncDisposable`
  - `static Task<EmergencySession> StartAsync(string serial, CancellationToken ct)`: push, start (kept `Process`), forward, connect, send the token. Throws `EmergencyException(string userMessage)` with the spec's error texts.
  - `IAsyncEnumerable<byte[]> FramesAsync(CancellationToken ct)`: length-prefixed reader. It rejects lengths > 1,048,576 by ending the stream.
  - `Task SendAsync(string type, JsonObject payload)`: writes one JSON line.
  - `string Model`
  - `DisposeAsync()`: kills the helper process, runs `adb forward --remove tcp:<port>` and `adb shell rm -f /data/local/tmp/palwyn-emergency.jar`. It's idempotent.
  - `static Task<IReadOnlyList<AdbDevice>> ReadyDevicesAsync()`: USB devices with `IsReady`.
  - `App` tracks open sessions and disposes them on exit.

- [ ] **Step 1: Implement `EmergencySession` to the interface above.** Use `RandomNumberGenerator.GetBytes(32)` for the token and 8 random bytes (hex) for the socket name.
- [ ] **Step 2: On-device check, through a temporary DEBUG switch `--emergency` in DevArgs** (it opens a session and logs the first frame's size, then disposes)

Expected:
- The log shows the frame size.
- Afterwards, `adb forward --list` has no emergency entry, and `adb shell ls /data/local/tmp` has no `palwyn-emergency.jar`.
- The normal USB link stays Connected the whole time.

- [ ] **Step 3: Check quitting from the tray mid-session**

Expected: no `app_process` for `dev.palwyn.emergency` remains (`adb shell ps -A | grep emergency` is empty).

- [ ] **Step 4: Commit** `Windows: emergency session over adb`

### Task 5: ScreenWindow screen sources and the emergency mode

**Files:**
- Create: `apps/windows/src/Palwyn.App/ScreenSources.cs`
- Modify: `apps/windows/src/Palwyn.App/ScreenWindow.xaml.cs` (all `App.Current.Link` screen calls go through `IScreenSource`)

**Interfaces:**
- Consumes: `EmergencySession` (Task 4), the existing `LinkManager.RequestScreenAsync`, `OpenScreenAsync`, `ScreenControlAsync` and `Capabilities`.
- Produces:
  - `interface IScreenSource`, with members:
    - `string Title`
    - `bool CanControl`
    - `event Action? Changed`
    - `Task BeginAsync()`: link asks the phone; emergency starts the session
    - `IAsyncEnumerable<byte[]> FramesAsync(CancellationToken ct)`
    - `Task SendAsync(string type, JsonObject payload)`
    - `ValueTask DisposeAsync()`
  - `sealed class LinkScreenSource : IScreenSource`: today's behaviour. Frames start after `SCREEN_STATE started`.
  - `sealed class EmergencyScreenSource(string serial) : IScreenSource`: Title "Phone screen (emergency)", CanControl always true.
  - `ScreenWindow.Open()` (unchanged, link) and `ScreenWindow.OpenEmergency(string serial)`. Only one ScreenWindow at a time: opening the other kind closes the current one.

- [ ] **Step 1: Move the link-specific code into `LinkScreenSource` and make `ScreenWindow` use `IScreenSource`.** Behaviour must stay identical.
- [ ] **Step 2: Regression check of normal sharing on the device**

Run: open "Phone screen" from the tray.
Expected: the phone shows the Palwyn notification and Android's consent as before (the user taps, not the agent), frames appear, and controls work.

- [ ] **Step 3: Add `EmergencyScreenSource` and `OpenEmergency`.**
- [ ] **Step 4: Emergency check on the device** (Palwyn's phone app force-stopped)

Run: `--emergency` switch from Task 4, changed to call `ScreenWindow.OpenEmergency`.
Expected:
- The window title is "Phone screen (emergency)", with no prompt on the phone.
- Click opens apps, drag scrolls, Back/Home/Recents work, typing fills a text field.
- Rotation keeps clicks accurate.
- Unplugging shows "Disconnected" with Retry.

- [ ] **Step 5: Commit** `Windows: phone screen window takes a screen source; emergency mode`

### Task 6: Rescue files

**Files:**
- Create:
  - `apps/windows/src/Palwyn.App/Emergency/Rescue.cs`
  - `apps/windows/src/Palwyn.App/RescueWindow.xaml(.cs)`
- Test: `apps/windows/tests/Palwyn.Core.Tests/AdbTests.cs` (shell-quoting test)
- Modify: `apps/windows/src/Palwyn.Core/Adb.cs` (add `ShellQuote`)

**Interfaces:**
- Consumes: `AdbOutput.ParseStat`, `ShouldCopy` (Task 1), the bundled adb (Task 2).
- Produces:
  - `public static string AdbOutput.ShellQuote(string path)`: single-quote and escape `'` as `'\''`
  - `sealed class Rescue`, with:
    - `static readonly string[] Folders = ["DCIM","Pictures","Movies","Music","Download","Documents","Android/media"]`
    - `Task<RescueResult> RunAsync(string serial, string targetDir, IProgress<(int done, int total)> p, CancellationToken ct)`. It lists with `adb -s <serial> shell 'find /sdcard/<folder> -type f -exec stat -c "%s %Y %n" {} +'` per folder. For each file it runs `adb pull` into `targetDir/<relative path>` and sets the local LastWriteTime to the remote mtime. A file that fails counts as failed and copying continues.
  - `record RescueResult(int Copied, int Skipped, int Failed)`
  - The window: a folder picker (default `Pictures\Palwyn rescue yyyy-MM-dd`), a progress bar, Cancel, and an end text `Copied {Copied}, skipped {Skipped}, failed {Failed}.`

- [ ] **Step 1: Write the failing test**

`ShellQuote("/sdcard/IMG 2026'01.jpg") == "'/sdcard/IMG 2026'\\''01.jpg'"`.
- [ ] **Step 2: Run it and see it fail; implement; run it and see it pass**

Run: `dotnet test apps/windows/tests/Palwyn.Core.Tests --filter AdbTests`
- [ ] **Step 3: Implement `Rescue` and `RescueWindow`.**
- [ ] **Step 4: On-device check**

Push a test folder with `adb push` to `/sdcard/Download/palwyn-rescue-test/`, holding 3 files including `a b'c.txt` and `ñ.txt`, then run Rescue into a temp folder.
Expected:
- First run: copied ≥ 3, and the test files are byte-identical (compare SHA-256).
- Second run: they're skipped.
- Cancel mid-run stops it.
- Delete the test folder from the phone and the temp folder afterwards.

- [ ] **Step 5: Commit** `Windows: rescue files over adb`

### Task 7: Entry points, hint and readiness

**Files:**
- Modify:
  - `apps/windows/src/Palwyn.App/QuickActions.cs` (actions "Emergency screen" and "Rescue files", enabled when `ReadyDevicesAsync` returns ≥ 1; with several devices, a picker showing models)
  - `apps/windows/src/Palwyn.App/HomePage.xaml(.cs)` (two tiles, the same enablement)
  - `apps/windows/src/Palwyn.App/SettingsPage.xaml(.cs)` (an "Emergency access" row with the ready / not-ready text and steps from the spec)
  - `apps/windows/src/Palwyn.App/Link/LinkManager.cs` (the hint)
  - `apps/windows/src/Palwyn.App/Toasts.cs` (`EmergencyHint()` with a button that opens `ScreenWindow.OpenEmergency`)
  - `apps/windows/src/Palwyn.App/DevArgs.cs` (remove the temporary `--emergency` switch)

**Interfaces:**
- Consumes: `EmergencySession.ReadyDevicesAsync`, `ScreenWindow.OpenEmergency`, `RescueWindow`.
- Produces:
  - Hint rule: the engine has been in `Waiting` for ≥ 20 s and an `IsUsb && IsReady` device exists. The toast "Phone not responding? Open the emergency screen." is shown once per Waiting episode, reset when Connected.
  - Readiness text:
    - ready: "Emergency access: ready (this PC is allowed)."
    - not ready: "Emergency access: not ready. Turn on USB debugging on your phone, plug it in once and tap \"Always allow from this computer\"."
    - A device in the `unauthorized` state shows the spec's unauthorized text.

- [ ] **Step 1: Implement the entry points and the readiness row.**
- [ ] **Step 2: Check them on the device**

Expected:
- Plugged in (authorized): the tray and Home items are enabled, and Settings says ready.
- Unplugged: the items are disabled, and Settings says not ready.

- [ ] **Step 3: Implement the hint. Check it by force-stopping Palwyn's phone app while it's plugged in.**

Expected: after about 20 s, one toast. Its button opens the emergency screen. Reconnecting and repeating gives one new toast.

- [ ] **Step 4: Commit** `Windows: emergency screen and rescue entry points, readiness and hint`

### Task 8: Docs, credits and the release check

**Files:**
- Modify:
  - `NOTICE`, adding:
    - "scrcpy (parts of its server, modified), Copyright (C) 2018 Genymobile, Copyright (C) 2018-2026 Romain Vimont, Apache License 2.0, see third_party/scrcpy"
    - "adb (Android SDK Platform-Tools r37.0.1), The Android Open Source Project, Apache License 2.0, see Assets/adb/NOTICE.txt"
  - `docs/feature-matrix.md` (an Emergency screen row and a Rescue files row, with what was verified)
  - `docs/security.md` (a new section: emergency access threat model, from the spec's Security section)
  - `docs/install.md` (section "Prepare for emergencies": USB debugging on, plug in once, Always allow; what works and what doesn't)
  - `docs/architecture.md` (the helper and its channel)
  - `docs/protocol.md` (a short "Emergency channel" section: token, frames, JSON lines)
  - `README.md` (one feature-table line: **Emergency screen**)

- [ ] **Step 1: Write the doc and NOTICE changes listed above.**
- [ ] **Step 2: Release-build check**

Run: the release workflow with `workflow_dispatch` on the branch.
Expected:
- Both jobs pass.
- The Windows zip's MSIX contains `Assets/adb/adb.exe` and `Assets/Emergency/palwyn-emergency.jar`. Check with `Expand-Archive` and list the MSIX contents.
- [ ] **Step 3: Final checks**

Run: `dotnet test apps/windows/tests/Palwyn.Core.Tests` and `./gradlew testDebugUnitTest :emergency:testReleaseUnitTest`.
Expected: all pass.
- [ ] **Step 4: Commit** `Docs and credits for the emergency screen and rescue files`
