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

## Troubleshooting

| Problem | What to try |
|---|---|
| The PC never finds the phone | Both on the same Wi-Fi? Some routers (guest networks, "AP isolation") block devices from seeing each other: use Settings > Connect by address on the PC, or a USB cable. |
| It disconnects when the phone's screen is off | Some phones (realme, OPPO, Xiaomi, Samsung) freeze background apps. In Android's app settings for Palwyn, allow background activity and set battery use to **Unrestricted**. |
| Calls, messages or notifications don't show | Open Palwyn on the phone: the setup list shows which permission is missing. Notification mirroring needs **Notification access**. |
| Answering calls from the PC doesn't work | Allow Palwyn to manage calls on the phone. Call audio always stays on the phone (a Windows limit). |
| Something else | Open an [issue](https://github.com/maeldotdev/palwyn/issues) with your phone model, Android and Windows versions. The PC's log helps: Settings > **Open log folder**. Logs stay on your PC; they contain connection events, not your messages. |
