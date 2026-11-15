# Plan

Restore a visible cursor in every video mode, then replace the yellow marker with the actual Windows cursor wherever native drawing succeeds. The current JPEG path attempts `DrawIconEx`, but the H.264 capture path does not; Android now ignores cursor-position events and has no fallback, so H.264 can show no cursor at all. The JPEG path also passes the cursor hotspot as `DrawIconEx`’s top-left coordinate, which misplaces the image.

## Scope
- In: Native Windows cursor rendering for JPEG and H.264 frames; correct position, hotspot and visibility; a small frame flag and Android fallback; verification before retiring the yellow marker.
- Out: Touch injection, cursor theme editing, new cursor-image transfer protocols, and latency work. Cursor appearance will update at the video frame rate.

## Action items
[ ] Confirm the active codec on the affected setup and log whether `GetCursorInfo` succeeds, whether the cursor is visible, whether native drawing succeeds, and whether the captured frame contains the cursor. This distinguishes the missing H.264 path from a failed JPEG draw.
[ ] Composite the native cursor into every active capture path before encoding. Reuse the Windows cursor APIs already declared in `GdiCapture.cs`; add the same behavior to `DxgiCapture.cs` (or its replacement) so JPEG and H.264 cannot diverge. Check the `DrawIconEx` return value and preserve the desktop image if it fails.
[ ] Correct placement using the captured monitor origin and `GetIconInfo` hotspot: draw at `(cursorScreenX - monitorOriginX - hotspotX, cursorScreenY - monitorOriginY - hotspotY)`. `DrawIconEx` positions the cursor’s upper-left corner, not its hotspot. Preserve the native cursor dimensions, alpha/color or monochrome mask, and system-selected cursor size; release bitmap handles returned by `GetIconInfo`.
[ ] Mark each video frame as having authoritative native cursor state when Windows successfully determines the cursor state and draws it when visible. Use an unused frame-header flag bit rather than adding another message type; distinguish a successfully hidden cursor from a failed cursor draw.
[ ] Restore Android’s yellow cursor marker as a fallback above both JPEG and H.264 video. Show it only when the frame flag says native cursor state is unavailable and the existing `CursorPos` event says the cursor is visible; hide it when the native cursor is present or Windows reports it hidden. This prevents the current no-cursor failure while avoiding a double cursor during normal operation.
[ ] Keep the existing position messages until the fallback path is no longer needed. Update Android’s frame decoding and use one overlay layer shared by both codecs; do not remove cursor handling from either app before the matching replacement is built and verified.
[ ] Verify arrow, I-beam, hand, resize, wait, custom, monochrome, high-contrast and large cursors; confirm hotspot alignment and clipping at all four display edges. Test JPEG and H.264 independently, plus hidden cursor and forced `DrawIconEx` failure; require either the native cursor or visible fallback in every case and no double cursor when native drawing succeeds.
[ ] Install matching Windows and Android builds together, then compare phone captures with the Windows source and confirm the 30 FPS minimum remains stable. Check animated cursors and document any frame-animation limitation if Windows does not expose the active animation frame through the chosen drawing path.

## Open questions
- None blocking. The plan assumes the current yellow marker is an acceptable temporary fallback when native cursor rendering fails.
