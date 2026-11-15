# Plan

Restore the last known-working cursor-position path, then send the Windows cursor image separately and draw it in one Android overlay above either video codec. The commit history identifies the regression: `def9edf` sent `CursorPos` and drew the Android marker; later changes removed the sender during USB work and removed the Android overlay when switching to frame compositing. The replacement relies on unchecked `DrawIconEx`, and the latest `GetIconInfo` change still discards the hotspot values. The cursor disappeared because both previously working display paths were removed before the replacement was validated.

## Scope
- In: Restore position updates, transmit native cursor shape and hotspot only when the shape changes, display it over JPEG and H.264, and retain the yellow marker as fallback.
- Out: Drawing the cursor into desktop video frames, touch injection, latency tuning, and custom cursor editing. Do not depend on GDI frame compositing for cursor visibility.

## Action items
[ ] Restore the known-good `CursorPos` sender and Android position/visibility overlay from commit `def9edf` (adapt it to the current connection code without reverting unrelated changes). Verify the yellow marker moves and hides correctly before adding cursor shapes.
[ ] Capture the active `HCURSOR` shape on Windows into a small transparent 32-bit DIB when the cursor handle changes; read dimensions and hotspot from `GetIconInfo`, rasterize with `DrawIconEx` at the DIB origin, and encode the result as PNG. Check every API result and release the `GetIconInfo` bitmaps. Test this rasterization locally before sending it over the wire.
[ ] Add one `CursorShape` wire message containing image dimensions, hotspot coordinates, and PNG bytes. Send it only when the shape changes, immediately after connection and after reconnect; retain the existing `CursorPos` message for movement and visibility. Avoid changing the video-frame payload or codec paths.
[ ] Decode and cache the latest cursor image in Android, then draw it in one overlay above the video `SurfaceView` using the existing stream-to-view geometry. Offset its top-left by the scaled hotspot so the Windows hot spot stays at the received cursor position; use the same overlay for JPEG and H.264.
[ ] Keep the yellow marker visible until a valid native cursor image arrives. If shape capture, transfer, or decoding fails, keep the marker and report the failure; never leave the cursor invisible. Handle hidden/out-of-display state from the existing position message.
[ ] Validate shape transitions for arrow, I-beam, hand, resize, wait, custom, monochrome, high-contrast and large cursors; verify hotspots, all four display edges, hide/show, reconnect, and both codecs. Require the native image to match the host design and the yellow marker to appear on any shape-path failure.
[ ] Install matching Windows and Android builds together and verify behavior with their version and codec recorded. Keep fallback behavior in the release and confirm cursor overlay does not reduce the established 30 FPS minimum.

## Open questions
- None blocking. Animated cursor frame-by-frame playback is deferred; static cursor shapes must match exactly, and the fallback remains available if the shape cannot be rasterized.
