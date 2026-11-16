# Plan: Android true fullscreen (immersive mode)

Goal: use the entire Android display for the streamed video by hiding the status bar and navigation bar, while keeping touch, connect/reconnect, and rotation working.

## Scope
- In: Android app (`android/`) only.
- Out: Windows host, protocol changes, video codec work, Android TV/other form factors.

## Design
- Make the activity edge-to-edge and immersive-sticky:
  - `WindowCompat.setDecorFitsSystemWindows(window, false)` so content can extend under system bars.
  - `WindowInsetsControllerCompat` hides `WindowInsetsCompat.Type.systemBars()` and uses
    `BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE` so bars only appear temporarily on swipe and then hide again.
  - Keep `FLAG_KEEP_SCREEN_ON`.
- Apply the same flags on `onCreate` and re-apply on `onWindowFocusChanged` (bars can reappear on focus loss, rotations, and app switches).
- Theme already uses `Theme.Material.NoActionBar`; keep it.
- The video `SurfaceView` already fills the computed video box. With edge-to-edge, ensure the Compose box fills the window and the letterbox math in `DisplayGeometry` uses the real window size.
- Keep the "connecting" UI visible but not under the (hidden) bars; use window-inset padding for that overlay.
- Do not change touch mapping: `TouchCapture` maps view coordinates to stream coordinates via `DisplayGeometry`; the view dimensions already reflect the fullscreen window.

## Implementation steps
1. Add `androidx.core:core-ktx` (or `core`) dependency if `WindowInsetsControllerCompat`/`WindowCompat` are not already available transitively.
2. Add a `FullscreenController(activity)` helper that:
   - enables edge-to-edge,
   - hides system bars with sticky behavior,
   - re-applies on focus change.
3. Call it from `MainActivity.onCreate` and `onWindowFocusChanged`.
4. Update Compose layout so the video box and the connecting overlay fill the window and handle insets.
5. Handle `configChanges` already present (`orientation|screenSize|smallestScreenSize`) so rotation does not recreate the activity; reapply fullscreen on focus change.
6. Restore defaults on `onDestroy` (hide calls are no-ops after destroy, but keep cleanup explicit).

## Risks
- Gesture navigation: sticky behavior is required so the nav bar can be swiped and then re-hides.
- System bars may reappear during reconnect or permission dialogs; reapply on focus change.
- Edge-to-edge padding must not cover the video; only the connecting UI.
- Some OEMs ignore immersive flags; test on the target device.

## Validation
- Launch app: no status/nav bars visible; video fills the screen.
- Swipe from edge: bars appear, then hide again.
- Rotate: no crash, still fullscreen.
- Connect/disconnect/reconnect: remains fullscreen and cursor still renders.
- Screenshot confirms full-screen video with no system UI.

## Open questions
- None blocking. If you want a "tap to show controls" overlay, that can be added later without changing this fullscreen baseline.
