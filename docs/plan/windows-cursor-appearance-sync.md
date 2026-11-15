# Plan

Restore the cursor that was visible before the native-cursor change, then make the real Windows cursor replace it only when native rendering is confirmed in the displayed frame. The current Android client has no visible cursor path: it ignores `CursorPos`, and the Windows engine contains cursor API declarations but no longer sends position updates. Both Windows capture paths call `DrawIconEx`, but ignore its result and cannot tell Android whether the cursor made it into the frame.

## Scope
- In: Restore the proven position-marker path; diagnose and fix native drawing in JPEG and H.264 capture; conditionally show a fallback marker; correct hotspot placement and verify both apps together.
- Out: Touch input, cursor theme editing, a new cursor-image transport, and latency work. Keep a visible cursor even if native rendering fails.

## Action items
[ ] Restore the working fallback first: send `CursorPos` updates from the Windows engine using the existing message format, restore Android cursor position/visibility state, and draw the yellow marker in a shared overlay above both JPEG and H.264 surfaces. Confirm it is visible before changing native compositing again.
[ ] Instrument native rendering separately in `GdiCapture.TryGetFrame` and `DxgiCapture.TryGetFrame`: record whether `GetCursorInfo` succeeds, cursor visibility/handle, whether the cursor is within captured bounds, the `DrawIconEx` return value, active codec, and whether the resulting frame visibly contains the cursor. Do not report native cursor state as successful merely because the API was called.
[ ] Fix Windows cursor placement using `GetIconInfo` hotspot and the selected monitor origin: draw at `(cursorScreenX - monitorOriginX - hotspotX, cursorScreenY - monitorOriginY - hotspotY)`. `DrawIconEx` takes the image’s upper-left position; the current calls pass the hotspot position directly. Release the bitmap handles created by `GetIconInfo`.
[ ] Composite the native cursor exactly once before encoding on every active capture path. Preserve native dimensions, color/alpha or monochrome mask, and system-selected cursor size; treat hidden cursor state as a successful no-cursor frame, and treat API failure as a request to keep the Android marker fallback.
[ ] Add one cursor-present/authoritative bit to the existing video-frame flags and pass it through `FrameCodec` and Android `ServerEvent.Frame`. Set it only when Windows has successfully determined the cursor state and, if visible, drawn it into that same frame. Android should hide the yellow marker only for authoritative frames; if native drawing fails or is unsupported, continue showing the marker using `CursorPos`.
[ ] Keep `CursorPos` sending and parsing for fallback and preserve the frame-flag fallback across both codecs. Do not remove the marker or position path after a single successful screenshot; only hide it on frames confirmed to contain the native cursor.
[ ] Validate arrow, I-beam, hand, resize, wait, custom, monochrome, high-contrast and large cursors; verify hotspot alignment and all four display edges. Exercise JPEG and H.264 independently, hidden cursor, forced `DrawIconEx` failure, capture restarts and mode changes. Require a visible native cursor or yellow fallback in every visible-cursor case, with no double cursor when native rendering succeeds.
[ ] Install matching Windows and Android builds together and confirm cursor design against the Windows source while maintaining the 30 FPS minimum. Check animated cursors and document any active-frame limitation if the native Windows path cannot reproduce the current animation frame.

## Open questions
- None blocking. Keep the yellow marker as the permanent failure fallback; it is hidden only when the corresponding video frame confirms native cursor rendering.
