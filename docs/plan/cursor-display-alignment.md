# Plan

Fix the Windows cursor marker stopping short of the displayed desktop by tracing its coordinates from the selected Windows monitor through capture and protocol messages to Android’s rendered video rectangle. First measure the active path to distinguish Windows DPI/origin/capture errors from Android scaling or stale geometry, then make the coordinate contract shared and verify marker alignment at the screen edges.

## Scope
- In: Windows DPI awareness, exact monitor identity and physical bounds, capture/frame dimensions, cursor coordinate generation and protocol, Android video/cursor rendering for JPEG and H.264, geometry tests, and real-device verification.
- Out: Touch-to-mouse injection (not the reported symptom), video performance work, VDD redesign, networking/security changes, and unrelated UI changes.

## Action items
[ ] Reproduce the reported edge shortfall and capture the active codec; selected monitor identity, physical bounds and origin; current display mode; capture and encoded frame dimensions; cursor coordinates at each edge; and Android `SurfaceView` pixel bounds and content rectangle. Add a temporary known edge grid/marker if necessary to determine whether the captured image itself is cropped or only the cursor overlay is mispositioned.
[ ] Establish Windows physical pixel coordinates before any display APIs run by declaring process DPI awareness; select the attached Parsec monitor exactly once; and obtain its bounds, origin, and current mode from that same monitor identity. Remove guessed dimensions and primary-screen fallback from the virtual-display path or surface a clear startup failure.
[ ] Align Windows capture and cursor sources: verify the capture DC dimensions and origin against the selected monitor rectangle; convert `GetCursorInfo` screen position to capture-local coordinates in the same DPI space; handle non-zero and negative desktop origins; and recalculate the geometry after resolution or topology changes.
[ ] Define a versioned geometry contract for frame and cursor messages, including dimensions, coordinate origin, pixel-edge convention, visibility, and geometry generation. Encode/decode with explicit byte order, validate cursor bounds against the matching frame geometry, and discard stale updates after a mode change.
[ ] Use a single Android content rectangle derived from the actual frame-buffer dimensions and rendered view pixel bounds. Render the cursor as an overlay above both JPEG and H.264 video, use that same rectangle for video and cursor scaling, and clip marker drawing to the image content.
[ ] Define full-range edge mapping and lifecycle behavior: map first and last display pixels correctly, handle cursor hotspot/marker appearance separately from its position, and clear or hide cursor state on disconnect, surface recreation, or geometry-generation changes.
[ ] Add unit tests for monitor-screen-to-local transforms, DPI scaling, non-zero and negative origins, inclusive/exclusive edge boundaries, Android aspect-fit transforms and letterboxing, four corners and center, and protocol geometry changes or stale messages.
[ ] Validate on the affected PC and phone using a known grid at all four edges and corners. Repeat with Windows scaling at 100%, 125%, 150%, and 200%; multiple virtual-display resolutions/aspect ratios; Android portrait and landscape; reconnects and mode changes; and both JPEG and H.264. Require the marker position to match the same image point at all edges within a documented pixel tolerance and retain diagnostic measurements.

## Open questions
- What Windows display scaling, virtual-display resolution, Android model/orientation, and codec are active on the affected setup? If unavailable, use the defaults and exercise the full validation matrix above.
