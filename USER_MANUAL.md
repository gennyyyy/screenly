# Screenly User Manual

Screenly turns an Android device into a **second (extended) display** for a Windows PC. The PC creates a real virtual monitor, captures it, and streams the video to the Android device over your local network or USB. Touch input on the Android device moves the cursor on the PC.

---

## 1. What's in the box

| File | Purpose |
|---|---|
| `Screenly-Setup.exe` | Windows installer — installs the engine, host UI, and the virtual display driver |
| `Screenly.Host.exe` | Windows control app (tray icon + window) — auto-starts the engine |
| `Screenly.Engine.exe` | Windows engine (run standalone if you prefer) |
| `Screenly.apk` | Android app (sideload onto your phone/tablet) |

---

## 2. Install on Windows

1. Run **`Screenly-Setup.exe`** as Administrator.
2. Follow the installer. It will:
   - Install `Screenly.Engine.exe` and `Screenly.Host.exe`
   - Install the **Parsec Virtual Display Driver** (required for the extended display)
   - Add a Windows Firewall rule for port **7543**
3. When finished, launch **Screenly** from the Start Menu (or run `Screenly.Host.exe`).

> **First run:** Windows may ask you to allow the app through the firewall. Click **Allow**.

The host window opens and shows:
- **Host IP** (e.g. `192.168.0.146`)
- **Port** (`7543`)
- Status: engine running

The engine runs in the background (system tray). You can close the host window — the engine keeps running and the second screen stays active.

---

## 3. Install on Android

1. Copy `Screenly.apk` to your Android device (email, cloud drive, USB, etc.).
2. On the device, open the APK file to install it.
   - If prompted, enable **Install from unknown sources** for your browser/file manager.
3. Open the **Screenly** app.

> **Requirements:** Android 8.0 (API 26) or newer.

---

## 4. Connect

### USB connection (only mode)
1. Plug the phone into the PC via USB with **USB debugging** enabled.
2. On the PC, open a terminal and run:
   ```
   adb reverse tcp:7543 tcp:7543
   ```
3. On the phone, enter `127.0.0.1` in **Manual host IP** and tap Connect.

> **Tip:** If the Connect button is hidden by the keyboard, press **Back** first to dismiss the keyboard, then tap Connect.

---

## 5. Using the second screen

- Once connected, the phone shows the PC's virtual display **fullscreen**.
- **Touch** the phone screen to move the mouse cursor on the PC.
- **Drag** to drag windows.
- **Multi-touch** is supported for gestures.
- The PC's cursor is visible on the extended display.

To stop: close the app on the phone, or stop the engine from the PC tray icon.

---

## 6. Settings (Windows host)

Open `Screenly.Host.exe` (tray icon → Open) to configure:

| Setting | Description |
|---|---|
| Resolution | Width × height of the virtual display (default 1920×1080) |
| Refresh rate | Hz (default 60) |
| Bitrate | JPEG quality (higher = sharper, more bandwidth) |
| PIN | Optional 4–6 digit PIN to restrict connections |
| Auto-start | Start engine automatically at Windows login |

Config is stored in `%APPDATA%\Screenly\config.json`.

---

## 7. Troubleshooting

| Problem | Solution |
|---|---|
| Phone stuck on "Searching for hosts on LAN" | Use **Manual IP** (Option B) or USB (Option C). mDNS may be blocked by your router. |
| "Connect" button doesn't respond | Dismiss the keyboard (Back button) first, then tap Connect. |
| Connection refused / ENETUNREACH | Ensure phone and PC are on the same network. Check firewall (port 7543). |
| Black screen on phone | The engine may still be starting. Wait 5 seconds and reconnect. |
| Cursor not visible | Ensure you're running the latest engine build. |
| Virtual display not created | Run `Screenly.Engine.exe` as Administrator once, or install the Parsec VDD driver manually from `C:\Program Files\Parsec Virtual Display Driver`. |
| Engine won't start | Check that port 7543 isn't in use by another app. |
| App crashes on connect | Ensure Android 8.0+ and sufficient free RAM. |

---

## 8. Uninstall

- **Windows:** Settings → Apps → Screenly → Uninstall (or run the uninstaller from the install folder).
- **Android:** Settings → Apps → Screenly → Uninstall.

---

## 9. Known limitations

- **Mirror vs. extend:** Screenly creates a real extended display, but capture is via GDI+JPEG (~5–6 fps at 1080p). DXGI + H.264 hardware encoding is planned for 60 fps.
- **USB only:** Wi-Fi/network discovery has been removed; connection is over USB via `adb reverse`.
- **No audio:** Only video is streamed; PC audio stays on the PC.

---

*Screenly — Windows host + Android second screen.*