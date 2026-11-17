# Plan

Cursor is composited into the captured frame (simplest path: works for the existing Android APK, no protocol or overlay changes). The compositing itself was failing because the capture memory surface was created from the secondary display DC; `DrawIconEx` silently draws nothing there. Create the memory bitmap from a primary screen DC while BitBlt'ing from the target display DC.

## Root cause findings (verified)

- **Compositing surface bug:** in both `GdiCapture` and `DxgiCapture`, the memory DC/bitmap is created from the target display DC (`CreateDC(deviceName)`). Experiment: BitBlt from the secondary display into a memory bitmap created from `GetDC(0)` renders the cursor; the same BitBlt into a bitmap/DIB created from the secondary DC does not, even though `DrawIconEx` returns true.
- **Regression (found again):** the GdiCapture fix was lost from the working tree after the fullscreen/loading-screen changes; `GdiCapture` reverted to `CreateCompatibleDC(hdcSrc)`. The DxgiCapture fix survived. Because the default codec is JPEG, this made the cursor invisible again. The running Program Files engine was also stale, compounding the issue.
- **Earlier Android bug (now moot):** the Android receiver had a duplicate no-op `CursorPos` branch, so the separate-overlay path could never work. The compositing path removes the need for that overlay entirely.
- **H.264 not implemented:** `H264Encoder.Encode` returns `null` for every frame, so only JPEG can be verified today.
- **Monitor geometry:** fixed; `GdiCapture`/`DxgiCapture` now require an exact device match and log when not found instead of silently keeping origin `(0,0)`.

## Scope

- In: create capture memory surfaces from the primary screen DC, keep `CursorDrawer.Draw`, verify the cursor appears at the correct position in JPEG frames on the secondary display.
- Preserve the GdiCapture memory-surface fix in all future edits: memory DC/bitmap from `GetDC(IntPtr.Zero)`, BitBlt from the display DC. Reverting this silently breaks the cursor.
- Out: separate `CursorPos`/`CursorShape` overlay, Android UI changes, H.264 encoder work, touch injection, animated cursors.

## Action items

- [x] Make monitor geometry explicit in `GdiCapture`/`DxgiCapture`; log not-found instead of silent `(0,0)`.
- [x] In `GdiCapture.TryGetFrame`, create `hdcMem` and `hBitmap` from `GetDC(IntPtr.Zero)`; keep BitBlt source as the display DC; release the screen DC.
- [x] In `DxgiCapture.TryGetFrame`, create `hdcMem` and `CreateDIBSection` from `GetDC(IntPtr.Zero)`; keep BitBlt source as the display DC; release the screen DC.
- [x] Build `dotnet build Screenly.sln` and run tests.
- [ ] Add a debug log in `CursorDrawer.Draw` (or `GdiCapture.TryGetFrame`) recording `GetCursorInfo` success/flags/handle, monitor origin, local cursor position, bounds decision, and `DrawIconEx` result. Without this, a silent failure is invisible.
- [ ] Add an automated check: capture a JPEG frame with the cursor at a known position and assert non-transparent pixels in the expected region (or at least non-zero alpha in the cursor bounding box).
- [ ] Rebuild the self-contained Engine (`dotnet publish ... -p:PublishSingleFile=true`) and replace the installed binary. Verify the running process path/hash matches the publish output before any cursor test.
- [ ] Verify on the actual second display: move the pointer to the streamed monitor, capture a JPEG frame, and confirm the native cursor and hotspot are present at the expected position.
- [ ] Revisit H.264 only after `H264Encoder.Encode` returns real frames.

## Open questions

- None blocking. JPEG is the verified path; the fix needs no Android rebuild.
- Deployment check: confirm the running `Screenly.Engine.exe` path/hash before testing. A stale binary (or a binary built from source without the GdiCapture fix) reproduces the missing cursor on connect and reconnect.
