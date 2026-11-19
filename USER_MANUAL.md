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
   - Install the **virtual display driver** (bundled — no separate Parsec install needed)
   - Add a Windows Firewall rule for port **7543**
3. When finished, launch **Screenly** from the Start Menu (or run `Screenly.Host.exe`).

> **First run:** Windows may ask you to allow the app through the firewall. Click **Allow**.

The host window opens and shows the engine status, active codec, resolution/FPS, and connection type. The engine runs in the background (system tray). You can close the host window — the engine keeps running and the second screen stays active.

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
2. Run the Screenly desktop app (`Screenly.Host.exe` or `Screenly.Engine.exe`). The installer places `adb.exe` in `{app}\adb`; the engine uses it automatically, with PATH and the Android SDK as fallbacks.
3. Open the Screenly app on the phone — it auto-connects to the PC.

If it does not connect, check that `adb devices` shows the phone as `device`, and run `adb reverse tcp:7543 tcp:7543` once on the PC. The Android app has no manual host/IP entry.

---

## 5. Using the second screen

- Once connected, the phone shows the PC's virtual display **fullscreen**.
- The PC cursor is rendered as an overlay and follows the virtual display.
- Touch input is sent by the Android app but the engine does not inject it yet; touch does not currently move the PC cursor.
- To stop: close the app on the phone, or stop the engine from the PC tray icon.

---

## 6. Settings (Windows host)

Open `Screenly.Host.exe` (tray icon → Open) to change the **Encoder**:

| Setting | Description |
|---|---|
| Encoder | JPEG or H.264; switching disconnects the phone and reconnects with the new codec |
| Frame rate | 30 FPS (stable) or 60 FPS; switching disconnects and reconnects. 60 FPS is not sustained by the current software/GDI path; 30 is recommended |

Resolution, bitrate, FPS, and virtual-display settings are read from `%APPDATA%\Screenly\config.json`. The Host does not currently expose those fields.

---

## 7. Troubleshooting

| Problem | Solution |
|---|---|
| Phone won't connect | Ensure USB debugging is enabled. Use `"%ProgramFiles%\Screenly\adb\adb.exe" devices` if `adb` is not on PATH; the engine recreates the reverse tunnel every 10 s. |
| Connection refused / ENETUNREACH | Check that the engine is listening on `127.0.0.1:7543` and `adb reverse tcp:7543 tcp:7543` succeeded. |
| Black screen on phone | The engine may still be starting, or H.264 initialization failed. Check the Host error line and `%APPDATA%\Screenly\status.json`. |
| Cursor not visible | Ensure the cursor is inside the virtual display; the overlay is hidden when it is outside. |
| Virtual display not created | Run `Screenly.Engine.exe` as Administrator once, or install the Parsec VDD driver manually from `C:\Program Files\Parsec Virtual Display Driver`. |
| Ghost displays in Settings | Reboot the PC to clear stale Parsec VDD displays left by a crashed run. |
| Second screen mirrors primary | Set the virtual display to "Extend" in Windows Display Settings. |
| Engine won't start | Check that port 7543 isn't in use by another app. |
| H.264 shows an encoder error | Check `status.json.encoderError`; the engine falls back to JPEG and reports the reason. |

---

## 8. Uninstall

- **Windows:** Settings → Apps → Screenly → Uninstall (or run the uninstaller from the install folder).
- **Android:** Settings → Apps → Screenly → Uninstall.

---

## 9. Known limitations

- **USB only:** Wi-Fi/network discovery has been removed; connection is over USB via `adb reverse`.
- **No touch input yet:** the Android app sends touch events, but the engine does not inject them.
- **Software H.264:** H.264 uses Cisco OpenH264 on the CPU; 800×1280 measured ~29.8 FPS over 60 s. Media Foundation hardware encoding is not available.
- **No audio:** only video is streamed; PC audio stays on the PC.
- **No authentication/TLS:** the TCP server binds to loopback and is reached through the USB tunnel; pairing/PIN is not implemented.

---

*Screenly — Windows host + Android second screen.*