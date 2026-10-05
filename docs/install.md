# Installing Palwyn

Palwyn has two parts: the **Windows app** on your PC and the **Android app** on your phone. Download both from the [Releases](https://github.com/maeldotdev/palwyn/releases) page. Each release lists SHA-256 checksums in `SHA256SUMS.txt`.

Requirements: Windows 10 (version 2004) or Windows 11, 64-bit; Android 10 or later; phone and PC on the same Wi-Fi to pair.

## Windows

1. Download `Palwyn-<version>-windows-x64.zip`.
2. Right-click the zip, choose **Properties**, tick **Unblock** if it's shown, then **OK**. Extract the zip.
3. In the extracted folder, right-click **Install.ps1** and choose **Run with PowerShell**.
4. The installer asks once to trust Palwyn's signing certificate (Windows asks for administrator permission). Accept, and it installs Palwyn and the Windows App SDK runtime it needs.
5. Open **Palwyn** from the Start menu. It lives in the notification area (system tray).

Why the certificate step: Windows only installs signed apps. Palwyn is signed with its own certificate instead of one bought from a certificate authority, which your PC has to trust once. See [code signing](code-signing.md).

### Uninstall

- **Settings > Apps > Installed apps > Palwyn > Uninstall.** This removes the app and everything it stored (paired phones, settings, history). Files you received stay in your Downloads and Pictures folders.
- Optional: remove the certificate. Press Win+R, type `certlm.msc`, open **Trusted People > Certificates**, and delete the one issued to **Mael**.

## Linux (early)

The Linux app is new: it pairs, keeps the phone connected, shows its status and shows its notifications on your desktop. Calls, messages, photos, files and the remote come later. It needs Palwyn 0.15 or later on the phone.

1. Download `Palwyn-<version>-linux-x86_64.AppImage`.
2. Make it executable: right-click > Properties > "Allow executing file as program", or `chmod +x Palwyn-*.AppImage`.
3. Open it. Palwyn uses two common tools: `avahi-browse` (package `avahi-utils`) to find your phone, and `notify-send` (package `libnotify-bin` on Ubuntu, `libnotify` on Fedora) for notifications. Most desktops have both; Palwyn tells you if one is missing.
4. To start it when you sign in: Settings > **Start Palwyn when you sign in**.

The tray icon works on KDE Plasma, XFCE, Cinnamon and others. Stock GNOME has no tray: install the AppIndicator extension, or use the window, which Palwyn opens at start there. Closing the window keeps Palwyn running; Settings > **Quit Palwyn** stops it.

Prefer no AppImage? `Palwyn-<version>-linux-x64.tar.gz` holds the same app: extract it and run `./palwyn-linux`.

To uninstall, delete the AppImage (and `~/.config/autostart/dev.palwyn.Palwyn.desktop` if you turned on starting at sign-in). Its data is in `~/.local/share/palwyn` (identity and paired phones), `~/.config/palwyn` and `~/.local/state/palwyn` (logs).

## Android

1. On your phone, download `Palwyn-<version>-android.apk`.
2. Open it. If Android asks, allow your browser or file manager to **install unknown apps**.
3. Google Play Protect may warn that the app comes from outside the Play Store. This is expected for apps from GitHub; you can check the file against `SHA256SUMS.txt`.
4. Open Palwyn and follow the setup screens. Each permission explains what it's for; you can skip features you don't want.

Updates are installed the same way, over the existing app. Every Android release is signed with the same key, so your data and pairing are kept.

### Uninstall

Long-press Palwyn > **App info > Uninstall**. Files the PC sent you stay in **Download/Palwyn**.

## Pair your phone and PC

1. Put both on the same Wi-Fi.
2. On the PC, open Palwyn and choose **Add a phone**. A QR code appears.
3. Scan it with the phone's camera and tap the Palwyn link. The two devices exchange certificates and connect.

After pairing, they reconnect by themselves whenever both are on the same network. You can also connect over a USB cable (with USB debugging on) or by address over a VPN; see Settings on the PC.

## Prepare for emergencies

If your phone's screen breaks but the phone still works, Palwyn on your PC can show and control it (**Emergency screen**) and copy its photos, videos and files (**Rescue files**) over a USB cable, without touching the phone. This only works if you prepare it **now**, while the screen works:

1. On the phone, turn on Developer options: Settings > About phone, then tap **Build number** (or Version) 7 times.
2. In Developer options, turn on **USB debugging**.
3. Plug the phone into this PC with a USB cable and, on the phone, tick **Always allow from this computer** and tap **Allow**.
4. In Palwyn on the PC, Settings > **Emergency access** should say **Ready**.

Then, in an emergency, plug in the cable and choose **Emergency screen** or **Rescue files** in the tray or on Home. If the phone restarted, you'll see its lock screen: type your PIN with the PC keyboard.

What it can't do: reach a phone that is switched off or never allowed this PC; show banking apps or protected video (they stay black); copy other apps' private data, such as chat databases. For phones that won't turn on at all, only a backup made beforehand helps.

## Troubleshooting

| Problem | What to try |
|---|---|
| The PC never finds the phone | Both on the same Wi-Fi? Some routers (guest networks, "AP isolation") block devices from seeing each other: use Settings > Connect by address on the PC, or a USB cable. |
| It disconnects when the phone's screen is off | Some phones (realme, OPPO, Xiaomi, Samsung) freeze background apps. In Android's app settings for Palwyn, allow background activity and set battery use to **Unrestricted**. |
| Calls, messages or notifications don't show | Open Palwyn on the phone: the setup list shows which permission is missing. Notification mirroring needs **Notification access**. |
| Answering calls from the PC doesn't work | Allow Palwyn to manage calls on the phone. Call audio always stays on the phone (a Windows limit). |
| Something else | Open an [issue](https://github.com/maeldotdev/palwyn/issues) with your phone model, Android and Windows versions. The PC's log helps: Settings > **Open log folder**. Logs stay on your PC; they contain connection events, not your messages. |
