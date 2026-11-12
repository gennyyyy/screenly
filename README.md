# Screenly — Windows host + Android second screen

## Prerequisites
- Windows 10 1903+ (for Parsec VDD / IddCx 1.4)
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Android Studio (for Android client) — minSdk 26
- Parsec VDD driver (bundled under driver/parsec-vdd)

## Build (Windows)
```
cd windows
dotnet build Screenly.sln
```
Run engine: `dotnet run --project Screenly.Engine`
Run host UI: `dotnet run --project Screenly.Host`

## Build (Android)
Open `android/` in Android Studio, sync Gradle, run on device.

## Output
Final installer + APK go to `app/`.

## Status
Native interop (DXGI Desktop Duplication capture, Media Foundation H.264 encode, Parsec VDD control, mDNS) is scaffolded as documented stubs pending wiring against proven reference implementations (deskstream, ParsecDisplay). TCP protocol, config, and UI scaffolding are implemented.
