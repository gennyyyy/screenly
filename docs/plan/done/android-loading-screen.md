# Plan: Android loading screen (replace plain text status)

Goal: replace the plain "Screenly / USB mode / Connecting..." text with a proper full-screen loading experience while the app connects and waits for the first video frame.

## Current state
- `MainActivity.kt` shows a `Column` with `Text("Screenly")`, `Text("USB mode: connect phone to PC via USB.")`, and `Text("Connecting...")` whenever `!connected`.
- The video `SurfaceView` is behind this text, so the loading screen covers it.
- No progress indicator, no retry affordance, no timeout feedback.

## Options

### Option A — Minimal centered spinner (recommended)
- Centered app name, an indeterminate `CircularProgressIndicator`, and a short status line.
- Dark background, no extra artwork.
- Pros: smallest change, no new resources, matches the existing dark theme.
- Cons: less branded; still text-heavy.

### Option B — Branded splash with status steps
- App name/logo area, spinner, and a rotating status line ("Connecting to PC", "Waiting for first frame", "Negotiating codec").
- Optional retry button after a timeout.
- Pros: clearer feedback during slow connects.
- Cons: more state to manage; needs status enum.

### Option C — Full-screen placeholder with animated progress
- Full-screen dark gradient, centered logo mark, progress bar, and a "Retry" button.
- Pros: looks polished.
- Cons: needs a logo asset and more layout work; overkill for a utility second screen.

## Recommendation
Use **Option A now**, with one small addition from Option B: a status line that can show "Connecting..." and, after a timeout (e.g. 10s), a "Retry" button. Defer branded artwork until a logo asset exists (the `res/` folder is currently empty).

## Design
- Replace the `if (!connected)` `Column` with a full-screen composable:
  - Centered vertically and horizontally.
  - App name (`Text("Screenly", style = headlineMedium)`).
  - `CircularProgressIndicator` (indeterminate).
  - Status text: "Connecting to PC..." (default), "Still connecting..." after a few seconds.
  - "Retry" button shown only after timeout; calls `client.close()` and restarts the connect loop.
- Keep the background black to match video.
- Do not block the video surface; loading overlay sits above it.
- Hide system bars per the separate fullscreen plan if both are applied.

## Implementation steps
1. Add a `connecting` state in `ScreenlyApp` with a start time and a `retryToken` counter.
2. Launch a coroutine that waits for the connect timeout and flips a `showRetry` flag.
3. Replace the plain text block with the loading composable.
4. Wire "Retry" to close the client and restart the connection loop (increment retry token to re-trigger `LaunchedEffect`).
5. Reset `showRetry` and status on successful `HelloAcked`.
6. Keep the loading screen until the first frame is received, not just on handshake, to avoid a flash of black.
7. Add a small timeout test (manual): disconnect PC, observe retry button; reconnect, observe it disappear.

## Required invariants (do not break)
- Keep the connection loop and handshake timeout exactly as they are; do not replace `LaunchedEffect(retryToken)` with `LaunchedEffect(Unit)` unless the retry coroutine is removed deliberately.
- Keep `ServerEvent.Frame` handling unchanged: decode JPEG, draw into the `SurfaceView`, recycle the bitmap. Do not add overlay drawing to the JPEG branch; the cursor is composited into the frame by the Windows engine.
- `CursorPos`/`CursorShape` remain ignored (no sender); do not reintroduce Android cursor overlay state.
- Keep `ServerEvent.Disconnected` handling: set `connected = false`, close the client, stop the connection service, and reset `firstFrameReceived = false`.
- Keep `FLAG_KEEP_SCREEN_ON`.
- Fullscreen must be applied in `onCreate` and re-applied in `onWindowFocusChanged`.
- Do not block touches meant for the video; the loading overlay is only shown while `!firstFrameReceived`.
- Disable the Retry button while a connect attempt is in flight.

## Risks
- Reconnect storms if "Retry" is spammed; disable the button while connecting.
- The loading overlay must not capture touches meant for the video after connect; only show while `!connected || !firstFrameReceived`.
- Keep screen on (`FLAG_KEEP_SCREEN_ON`) so the loading screen doesn't sleep.
- Fullscreen insets must not cover the video; only the connecting UI.
- The running Windows engine must be the fixed binary (`GdiCapture` creates its memory surface from `GetDC(IntPtr.Zero)`), otherwise the cursor is invisible even though the Android code is correct. Verify the process path/hash before testing.

## Regression checklist (must pass before calling done)
- Fresh launch with PC connected: loading screen appears, then video fills the screen, cursor visible.
- Fresh launch with PC disconnected: loading screen + Retry button after timeout; clicking Retry attempts reconnect.
- Disconnect PC mid-stream: loading screen returns; reconnect restores video and cursor.
- Rotate device: no crash, still fullscreen, video resumes.
- Swipe from edge: system bars appear briefly then hide.
- No double cursor and no invisible cursor on connect or reconnect.
- Connection loop still retries every ~2s when disconnected.

## Open questions
- None blocking. Logo/branding can be added later when an asset exists.
