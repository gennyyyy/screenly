# Screenly — Display Resolution Match & VDD Accumulation: Fix Report

## Problem Summary

Two critical issues:
1. **Multiple virtual display drivers accumulating** — every client connect / resolution change created a new Parsec VDD display; old ones were never removed.
2. **Wrong resolution** — the virtual display was always 1920×1080 (or 1920×1080 default) regardless of the Android device's resolution, causing black bars / aspect mismatch.

## Root Causes (real, verified against parsec-vdd source + runtime logs)

### 1. ADD ioctl does NOT set resolution
- `VddAddDisplay` calls `VddIoControl(VDD_IOCTL_ADD, NULL, 0)` — **no input data**. The width/height in the buffer are ignored.
- The driver always creates a display at its default resolution (1920×1080 from the fixed EDID), unless a preset is registered in `HKLM\SOFTWARE\Parsec\vdd`.

### 2. Resolution is controlled via the Windows Registry, not the ioctl
- Parsec VDD reads `HKLM\SOFTWARE\Parsec\vdd\{0..4}` (up to 5 keys), each `{width, height, hz}` (also `w`, `h`).
- After ADD, use `ChangeDisplaySettingsEx` to select a mode from the registered presets.
- If the mode isn't registered, `ChangeDisplaySettingsEx` returns `-2` (DISP_CHANGE_BADMODE).

### 3. Registry writes require admin (ACL)
- `BUILTIN\Users` has `ReadKey` only on `HKLM\SOFTWARE\Parsec\vdd`.
- The engine runs as standard user (asInvoker), so it cannot register presets without an ACL grant.
- Fix: installer (admin) grants `Users` write access (SetValue, CreateSubKey, Delete) on that key.

### 4. REMOVE ioctl requires overlapped I/O + big-endian index
- Official parsec-vdd source: `VddRemoveDisplay` writes the index as **16-bit big-endian** (`((index & 0xFF) << 8) | ((index >> 8) & 0xFF)`).
- Uses **overlapped I/O** with a 5000ms timeout.
- Synchronous I/O returns `Win32Error=31` (ERROR_GEN_FAILURE) — always fails.

### 5. Byte-order mismatch (our bug)
- We wrote the REMOVE index **little-endian** (inconsistent with ADD). Reverted to big-endian per official source.

### 6. Android reported app-window size, not full screen
- `activity.resources.displayMetrics.widthPixels/heightPixels` returned 1280×739 (window area) instead of 1280×800 (physical screen).
- Fixed: use `windowManager.defaultDisplay.getRealSize(size)`.

### 7. Concurrency race
- Multiple simultaneous HELLO handlers all saw `displayCreated=False` and each called `CreateAsync` → multiple displays.
- Fixed: `SemaphoreSlim` serializes `EnsureDisplayForClient`.

### 8. `displayCreated` set true even on failure
- `CreateAsync` failing left `displayCreated=true`, so retries never happened.
- Fixed: gate `displayCreated = true` on `capture != null`.

### 9. Installed engine was missing its DLL
- The installer only copied `Screenly.Engine.exe` (apphost), not the DLL. The app couldn't start.
- Fixed: `dotnet publish` with `PublishSingleFile=true` to produce a true single-file exe; installer copies that.

### 10. Engine ran as admin (UAC blocked silent launch)
- Manifest was `requireAdministrator`, blocking the tray app from launching the engine silently.
- Fixed: reverted to `asInvoker`; ACL grant in installer handles registry writes.

## What We Did (Fixes Applied)

### Windows Engine (`windows/Screenly.Engine/Program.cs`)
- Single-instance `Mutex` guard (`Global\ScreenlyEngine`) — prevents duplicate processes.
- `EnsureDisplayForClient` serialized with `SemaphoreSlim`.
- First-connect creates display directly at capped client resolution (no initial+recreate).
- `CreateDisplayAt`: registers registry preset → ADD → ChangeDisplaySettingsEx → read-back diagnostic.
- `RecreateTo`: correct state handling; proceeds even if REMOVE fails (orphan persisted).
- H.264 encoder initialized from actual capture dims (`dc.Width`, `dc.Height`).
- `displayCreated` gated on `capture != null`.

### Windows Display Driver (`windows/Screenly.Core/Display/DisplayManager.cs`)
- `EnsureRegistryPreset(width, height, refreshHz)`: writes to `HKLM\SOFTWARE\Parsec\vdd` (slot 0–4), both `width/height/hz` and `w/h`.
- REMOVE: official overlapped I/O, big-endian index, 5000ms timeout, ping after.
- Stale cleanup: overlapped REMOVE; keeps failed indices persisted (doesn't delete file on failure).
- Logging: every REMOVE result + Win32Error, every CreateAsync result.

### Manifest (`windows/Screenly.Engine/app.manifest`)
- `asInvoker` (no UAC per launch).

### Installer (`installer/screenly.iss`)
- Installs Parsec VDD driver (`/S`, silent, waits for completion).
- Grants `Users` write access to `HKLM\SOFTWARE\Parsec\vdd` (ACL).
- Installs single-file `Screenly.Engine.exe`.
- Launches Screenly after install.

### Android (`android/app/src/main/kotlin/com/screenly/MainActivity.kt`)
- Hello message uses `windowManager.defaultDisplay.getRealSize(size)` for full screen resolution instead of `displayMetrics` (which reported app-window size).

### Build & Deploy
- `dotnet publish` with `PublishSingleFile=true` → true single-file exe in `app/`.
- Installer rebuilt via ISCC.
- Android APK rebuilt via Gradle 8.11.1 and installed via adb.

## What TO Do

1. **Run `app\Screenly-Setup.exe` as admin** — installs Parsec VDD + sets ACL + installs single-file engine.
2. **Reboot** after first install (Parsec VDD driver requires it).
3. **Launch Screenly normally** — engine runs as standard user, registers presets at runtime.
4. **Verify** via `%APPDATA%\Screenly\engine.log`:
   - `EnsureRegistryPreset: slot 0 set to {W}x{H}@{Hz}Hz success`
   - `CreateDisplayAt diagnostic: requested={W}x{H} actual={W}x{H} match=True`
   - `VDD removed index 0` on reconnect (no `FAILED`)
5. **Build engine**: `dotnet build windows\Screenly.sln -c Release` then `dotnet publish windows\Screenly.Engine\Screenly.Engine.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o app`
6. **Build Android**: `gradle :app:assembleDebug` in `android/` with Gradle 8.11.1.
7. **Install Android**: `adb install -r app\build\outputs\apk\debug\app-debug.apk`.

## What NOT To Do (Mistakes We Made)

1. **Don't assume ADD sets resolution.** It doesn't — the ioctl takes no input. Must use registry presets + ChangeDisplaySettingsEx.
2. **Don't use synchronous I/O for REMOVE.** The driver requires overlapped I/O with 5000ms timeout. Sync returns Win32Error=31 (always fails).
3. **Don't write the REMOVE index little-endian.** Official source uses big-endian. Inconsistent endianness = silent failure.
4. **Don't delete `vdd-indices.txt` on failure.** Keep failed indices for next-run cleanup; deleting them orphans displays permanently.
5. **Don't set `displayCreated=true` unconditionally.** Gate it on successful creation (`capture != null`), otherwise failed creates loop forever.
6. **Don't run multiple engine instances.** Use a single-instance mutex; concurrent instances cause port conflicts and duplicate displays.
7. **Don't report `displayMetrics` as screen size on Android.** It returns app-window size (excluding system bars). Use `getRealSize()`.
8. **Don't install only the .exe without the DLL.** Use `dotnet publish` with `PublishSingleFile=true` for a true self-contained binary.
9. **Don't require admin for the engine.** Run as `asInvoker`; grant ACL via the installer instead (one-time UAC).
10. **Don't test with the old installed binary.** Always run from the build output path or reinstall via the installer.
11. **Don't skip the reboot** after first Parsec VDD install — the driver isn't available until reboot.

## Key Files Changed

| File | Change |
|---|---|
| `windows/Screenly.Core/Display/DisplayManager.cs` | REMOVE overlapped+big-endian, EnsureRegistryPreset, stale-cleanup persistence, logging |
| `windows/Screenly.Core/Display/DisplayManagerClass.cs` | RemoveAsync state handling + logging |
| `windows/Screenly.Engine/Program.cs` | Single-instance mutex, serialized EnsureDisplayForClient, direct first-connect create, ChangeDisplaySettingsEx, diagnostics, H.264 actual dims |
| `windows/Screenly.Engine/app.manifest` | `asInvoker` |
| `windows/Screenly.Tests/CapResolutionTests.cs` | Portrait + cap edge cases |
| `android/.../MainActivity.kt` | `getRealSize()` for full screen resolution |
| `installer/screenly.iss` | Parsec VDD install step + ACL grant |
| `docs/plan/display-resolution-and-accumulation-fix.md` | Implementation plan |

## Verification Checklist

- [ ] Engine log shows `EnsureRegistryPreset: slot 0 set to {W}x{H}@{Hz}Hz success`
- [ ] Engine log shows `CreateDisplayAt diagnostic: requested={W}x{H} actual={W}x{H} match=True`
- [ ] Engine log shows `VDD removed index 0` on reconnect (no `FAILED`)
- [ ] Only one `Parsec Virtual Display Adapter` in Device Manager
- [ ] Android screen has no black bars (fullscreen)
- [ ] `dotnet build` + `dotnet test` pass (8/8)
- [ ] `Screenly-Setup.exe` installs and launches without UAC on subsequent runs