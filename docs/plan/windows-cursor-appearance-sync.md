# Plan

Cursor is composited into the captured frame (simplest path: works for the existing Android APK, no protocol or overlay changes). The compositing itself was failing because the capture memory surface was created from the secondary display DC; `DrawIconEx` silently draws nothing there. Create the memory bitmap from a primary screen DC while BitBlt'ing from the target display DC.

## Root cause findings (verified)

- **Compositing surface bug:** in both `GdiCapture` and `DxgiCapture`, the memory DC/bitmap is created from the target display DC (`CreateDC(deviceName)`). Experiment: BitBlt from the secondary display into a memory bitmap created from `GetDC(0)` renders the cursor; the same BitBlt into a bitmap/DIB created from the secondary DC does not, even though `DrawIconEx` returns true.
- **Earlier Android bug (now moot):** the Android receiver had a duplicate no-op `CursorPos` branch, so the separate-overlay path could never work. The compositing path removes the need for that overlay entirely.
- **H.264 not implemented:** `H264Encoder.Encode` returns `null` for every frame, so only JPEG can be verified today.
- **Monitor geometry:** fixed; `GdiCapture`/`DxgiCapture` now require an exact device match and log when not found instead of silently keeping origin `(0,0)`.

## Scope

- In: create capture memory surfaces from the primary screen DC, keep `CursorDrawer.Draw`, verify the cursor appears at the correct position in JPEG frames on the secondary display.
- Out: separate `CursorPos`/`CursorShape` overlay, Android UI changes, H.264 encoder work, touch injection, animated cursors.

## Action items

- [x] Make monitor geometry explicit in `GdiCapture`/`DxgiCapture`; log not-found instead of silent `(0,0)`.
- [ ] In `GdiCapture.TryGetFrame`, create `hdcMem` and `hBitmap` from `GetDC(IntPtr.Zero)`; keep BitBlt source as the display DC; release the screen DC.
- [ ] In `DxgiCapture.TryGetFrame`, create `hdcMem` and `CreateDIBSection` from `GetDC(IntPtr.Zero)`; keep BitBlt source as the display DC; release the screen DC.
- [ ] Build `dotnet build Screenly.sln` and run tests.
- [ ] Verify on the actual second display: move the pointer to the streamed monitor, capture a JPEG frame, and confirm the native cursor and hotspot are present at the expected position. Keep the yellow-marker overlay only if a future fallback is still wanted; it is not part of this path.
- [ ] Revisit H.264 only after `H264Encoder.Encode` returns real frames.

## Open questions

- None blocking. JPEG is the verified path; the fix needs no Android rebuild.
