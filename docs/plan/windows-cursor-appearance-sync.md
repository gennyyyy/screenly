# Plan

Restore a guaranteed visible fallback first, then fix native cursor rendering using a capture made while the pointer is deliberately on the streamed display. The current code has no fallback (`Program.cs` no longer sends `CursorPos`, and Android ignores the event); both capture paths depend on unchecked `DrawIconEx`. The latest commit also claims a hotspot fix but only calls `GetIconInfo` and frees its bitmaps—the returned hotspot values are never used. The existing debug JPEG is written only for the first frame, which may be captured before the pointer reaches the virtual display, so it cannot identify the native draw failure.

## Scope
- In: Restore the proven marker path; observe the active codec and actual capture geometry; fix the failing native-cursor condition; retain fallback until each frame confirms the native cursor is present.
- Out: Touch injection, cursor theme editing, new cursor-image transport, and latency work. No further cursor-removal attempt until the validation gates pass.

## Action items
[ ] Restore the last known visible marker: have Windows send the existing `CursorPos` message again, restore Android cursor position/visibility state, and draw the yellow marker in a shared layer visible over both JPEG and H.264. Confirm it appears before changing capture rendering.
[ ] For one diagnostic run, record the actual engine binary/version and selected codec, monitor device name, capture width/height and monitor origin, plus `GetCursorInfo` success, flags, handle, screen position, monitor-local position, bounds result, and `DrawIconEx` return value. Capture a frame only after moving the pointer to the center of the streamed virtual display; the current one-time first-frame dump is not a valid cursor test.
[ ] Decide the failure from those values instead of guessing: if the pointer is outside bounds, correct monitor selection/origin/DPI space; if `GetCursorInfo` fails or reports hidden, correct the calling desktop/session/visibility path; if `DrawIconEx` fails, preserve its error and test drawing to the active capture DC; if the API reports success but the frame has no cursor pixels, verify that the drawn bitmap/DIB is the same buffer encoded and sent.
[ ] Implement the hotspot correction the latest commit omitted: use `ICONINFO.xHotspot/yHotspot` and draw at `(cursorScreenX - monitorOriginX - xHotspot, cursorScreenY - monitorOriginY - yHotspot)`. `DrawIconEx` takes the cursor image’s upper-left position. Keep the bounds check based on actual cursor dimensions instead of a fixed 32-pixel allowance, and release `GetIconInfo` bitmap handles after reading the hotspot.
[ ] Apply the verified compositor to both `GdiCapture` and `DxgiCapture`, and pass a `cursorAuthoritative` bit in an unused video-frame flag. Set it only when the cursor state is known and a visible native cursor was drawn into that exact frame, or a hidden state was confirmed.
[ ] Keep the yellow marker as fallback in Android for both codecs: hide it only when the corresponding frame says native cursor state is authoritative; otherwise use the latest valid `CursorPos`. A failed/unknown native draw must never result in no cursor.
[ ] Validate arrow, I-beam, hand, resize, custom, monochrome, high-contrast and large cursors with the pointer at the center and all four edges. Test JPEG/H.264 separately, plus hidden cursor and forced native-draw failure. Require a native cursor or fallback in every visible-cursor case, correct hotspot placement, and no double cursor.
[ ] Install matching Windows and Android builds together and verify against the actual binaries and codec in use. Remove temporary diagnostics only after the exact failure condition is recorded and the 30 FPS minimum remains stable.

## Open questions
- None blocking. The yellow marker remains the fallback whenever a frame cannot prove that Windows cursor rendering succeeded.
