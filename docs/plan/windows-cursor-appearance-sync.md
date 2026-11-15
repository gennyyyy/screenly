# Plan

Show the actual Windows cursor design on Android by drawing the current native cursor into each captured frame before JPEG or H.264 encoding. This uses Win32 cursor rendering already declared in the capture code and lets Android display the same pixels as the host, avoiding a new cursor-image protocol, a custom Android cursor renderer, or per-frame cursor network messages.

## Scope
- In: Windows cursor shape, position and hotspot capture; compositing in each active capture path; removal of the synthetic Android marker and redundant position updates; validation across shapes, codecs and display edges.
- Out: Touch injection, cursor theme editing, cursor animation reconstruction where Windows does not expose the active frame, and latency work. Cursor updates will follow the video frame cadence.

## Action items
[ ] Confirm which capture path is active for JPEG and H.264 and whether it already includes the pointer. Use one Windows-side composition point per path before encoding, so the cursor is included exactly once and the two codecs produce the same appearance.
[ ] Use `GetCursorInfo` and the existing `GetIconInfo`/`DrawIconEx` declarations in `GdiCapture.cs` to draw the current host cursor into the captured frame. Position the drawing from the cursor’s screen position minus the captured monitor origin and its native hotspot; preserve native dimensions, alpha/color or monochrome masks, and system cursor scaling. Free any bitmap handles returned by `GetIconInfo`.
[ ] Apply the same composition to the H.264 input frame in `DxgiCapture.cs` (or its replacement capture path) before encoding. Reuse one small Windows cursor-drawing helper if both paths need it; avoid separate per-codec cursor implementations and do not composite twice if the capture API already supplies the cursor.
[ ] Remove the yellow circle/cross and cursor-position overlay handling from `MainActivity.kt`, and stop the Windows `CursorPos` send loop in `Program.cs`. Keep the existing wire message identifier reserved for compatibility; video frames remain the sole source of visible cursor pixels.
[ ] Handle hidden cursors and capture failures without blocking or failing the whole frame: omit the cursor when Windows reports it hidden or the handle is invalid, and preserve the captured desktop if cursor drawing fails.
[ ] Verify common host cursors (arrow, I-beam, hand, resize, wait), a custom cursor, high-contrast/large system cursor settings, hotspot placement, and the cursor at all four display edges. Test both JPEG and H.264, check for missing or double cursors, and confirm Android requires no shape-specific scaling logic.
[ ] Measure frame-capture/encode time before and after cursor compositing and confirm the established 30 FPS minimum remains stable. Inspect Android screenshots against the Windows source to verify cursor design and hotspot match; document any animated-cursor frame limitation if the current Win32 path cannot expose the active animation frame.

## Open questions
- None blocking. The plan assumes the cursor should be part of the displayed desktop image and update at the video frame rate.
