# Screenly — Windows host + Android second screen

Screenly turns an Android device into an extended display for a Windows PC. The Windows engine creates a Parsec virtual display, captures it, encodes H.264 or JPEG, and streams over TCP. The Android app decodes fullscreen. Connection is USB-only through `adb reverse`; mDNS/Wi-Fi discovery is not present.

## Status

- Windows engine, WPF Host, and Android Compose client are implemented.
- JPEG is the default codec; H.264 (H264Sharp/OpenH264, software) is selectable in the Host and switches live.
- The Host has a FRAME RATE selector: 30 FPS (stable default) or 60 FPS. 60 switches correctly but the current GDI capture path does not sustain it on the test tablet (~23–28 FPS); 30 is recommended.
- Cursor position/shape is streamed separately and rendered as an Android overlay.
- Touch events are sent by Android but the engine still ignores them (`InputInjector` is a stub).
- H.264 800×1280 measured ~29.8 FPS over 60 s after the 1 ms timer-resolution fix; see `docs/plan/performance-baseline.md`.
- Media Foundation hardware encoding remains blocked; see ADR-011.

## Prerequisites

- Windows 10 1903+ (Parsec VDD / IddCx 1.4)
- .NET 8 SDK for building (the published app is self-contained)
- Android Studio for the Android client (`minSdk 26`)
- Parsec VDD driver (bundled under `driver/parsec-vdd`)
- Android Platform Tools are bundled by the installer under `{app}\adb`; PATH or the standard Android SDK location is used only as a fallback

## Build

```powershell
cd windows
dotnet build Screenly.sln
dotnet run --project Screenly.Engine
dotnet run --project Screenly.Host
```

Android: open `android/` in Android Studio and build. There is no checked-in Gradle wrapper.

## Run

1. Install/start the Host. It starts the Engine.
2. Enable USB debugging and connect the tablet.
3. The Engine runs `adb reverse tcp:7543 tcp:7543`.
4. Open the Android app; it connects to `127.0.0.1:7543` and sends its native resolution.
5. Choose JPEG or H.264 in the Host. A codec change disconnects the client and it reconnects with the new stream.

## Output

- `dist/win-x64/` — self-contained Engine + Host publish output
- `app/Screenly-Setup.exe` — Inno Setup installer
- `app/Screenly.apk` — Android APK

## Config

`%APPDATA%\Screenly\config.json` (port, virtual display, encoding, pairing). Engine diagnostics are in `status.json` and `engine.log` in the same directory.
