# Plan — Per-Client VDD Displays

**Goal:** Each connected Android device gets its own virtual display (VDD). The number of VDD displays always equals the number of connected devices. No sharing of a single display across clients.

**Current state:** One global VDD display, one capture, one encoder. Frames broadcast to all sessions. Resolution locked to first client.

## Design

### Windows (engine)
1. **Display registry**: `Dictionary<Guid sessionId, (DisplayManager, ICapture, H264Encoder?)>`.
2. **On HELLO**: create a new VDD display at the client's resolution, create capture + encoder, store in registry. Send HELLO_ACK with that display's dimensions.
3. **Capture loop**: iterate over all active sessions; for each, capture a frame, encode, send only to that session.
4. **On disconnect**: remove the display from registry, remove VDD, dispose capture/encoder.
5. **Input injection**: use the session's display dimensions for coordinate scaling (InputInjector needs sessionId or dimensions passed).
6. **Concurrency**: use a lock (SemaphoreSlim) for display creation/removal to avoid VDD driver races.

### Android (no changes)
- Each phone connects independently; gets its own stream dimensions via HELLO_ACK.
- No code changes needed.

## Tasks

### P1 — Refactor engine to per-session displays
- Files: `Program.cs`, `Session.cs` (maybe add DisplayHandle to Session).
- Move `display`, `capture`, `encoder`, `useH264` into a per-session struct.
- On HELLO: create display+capture+encoder, store in dict.
- Capture loop: iterate sessions.
- On disconnect: cleanup.

### P2 — Input injection per session
- Files: `InputInjector.cs`, `Program.cs`.
- Store display dims per session; route INPUT_TOUCH to the correct display.

### P3 — Remove global broadcast
- Frames sent only to the owning session.

### P4 — Tests + verification
- Connect 2 devices; verify each gets its own display (different resolutions).
- Verify no display accumulation on disconnect.

## Risks
- VDD driver may not support rapid create/remove (test).
- GPU/encoder resources: one encoder per client (may exhaust GPU). Document limit.
- Thread safety: use locks for session dict.

## Open questions
- Max number of concurrent displays? Parsec VDD supports up to 16 per adapter.
- Should resolution changes (CONFIG) recreate only that client's display? Yes.