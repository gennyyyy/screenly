# Plan: Make the cursor scale with display scaling

Goal: ensure the Windows cursor is drawn at the correct physical size for the streamed monitor's DPI, so it matches the host at 100/125/150/200% display scaling. The cursor is composited into the JPEG frame by `CursorDrawer.Draw`; Android just displays the frame, so all scaling happens on the Windows side.

## Current behavior
- `CursorDrawer.Draw` calls `DrawIconEx(hdc, cx, cy, hCursor, 0, 0, ...)` with 0,0 size, so Windows picks the size based on the calling thread's DPI awareness. Since the process is per-monitor DPI aware, this uses the **primary monitor's** DPI, not the virtual display's DPI. If the primary is 100% and the virtual is 150%, the cursor is drawn too small.
- The memory DC is created from `GetDC(IntPtr.Zero)` (primary screen) for DrawIconEx compatibility, but the cursor size should reflect the virtual display's DPI.

## Design
- In `CursorDrawer.Draw`, accept the target monitor's DPI (or monitor handle).
- Compute cursor size = `32 * (targetDpi / 96.0)` (logical 32px base at 96 DPI).
- Call `DrawIconEx(hdc, cx, cy, hCursor, size, size, 0, IntPtr.Zero, DI_NORMAL)` with explicit size.
- In `GdiCapture`/`DxgiCapture`, store the monitor handle from `MonitorCallback` and pass it (or the resolved DPI) to `CursorDrawer.Draw`.
- Use `GetDpiForMonitor` (shcore.dll) to get the virtual display's DPI. Fall back to 96 if the call fails.
- Re-draw every frame (already done) so DPI changes take effect immediately without a cursor handle change.

## Implementation steps
1. Add `GetDpiForMonitor` P/Invoke to `CursorDrawer`.
2. Change `CursorDrawer.Draw` signature to accept `int monitorDpi` (or `IntPtr hMonitor`).
3. Compute `int cursorSize = (int)Math.Round(32.0 * monitorDpi / 96.0)` and pass it to `DrawIconEx`.
4. In `GdiCapture` and `DxgiCapture`, store `_monitorHandle` in `MonitorCallback` and resolve DPI in `Start()` or pass the handle to `CursorDrawer.Draw`.
5. Update both `TryGetFrame` call sites to pass the DPI.
6. Test matrix:
   - Windows display scale: 100%, 125%, 150%, 200% on the virtual display.
   - Verify the cursor in the captured JPEG is the correct physical size for each scale.
   - Verify hotspot alignment (the cursor's tip stays at the received position).
   - Verify no regression at 100% (cursor should be identical to current output).

## Risks
- `GetDpiForMonitor` requires `shcore.dll` (Windows 8.1+). Fall back to 96 DPI if unavailable.
- If the monitor handle is null (fallback path), use 96 DPI.
- Drawing at an explicit size may slightly stretch the cursor if the base size assumption (32px) is wrong for some cursor types. Mitigate by reading the base size from `GetIconInfo` and scaling that instead of assuming 32.

## Open questions
- None blocking. No Android changes needed — the cursor is composited into the frame.
- If the virtual display is configured with a non-standard DPI, the cursor scales automatically.