# Screenly System Improvement Tasks

Track implementation of [the system improvement plan](../docs/plan/plan.md). Tasks are ordered by dependency; check items off only after their acceptance criteria are verified.

## Phase 1: Baseline and contracts

### Task 1: Establish streaming baseline
- [ ] Record capture, encode, decode, render, FPS, frame drops, and end-to-end latency for USB and Wi-Fi on named devices.
- [ ] Record resolution, bitrate/quality, environment, and exact reproduction steps.
- [ ] Save results in a dated performance report under `docs/plan/`.
- **Verify:** A second run on the same hardware produces comparable measurements.
- **Dependencies:** None.

### Task 2: Validate and centralize configuration
- [ ] Inventory runtime defaults and duplicate hardcoded port/resolution/quality/FPS values.
- [ ] Validate values on load and report invalid settings clearly.
- [ ] Add focused checks for defaults, bounds, and malformed config.
- **Verify:** Invalid config cannot start a partially configured listener or stream.
- **Dependencies:** Task 1.

### Task 3: Add structured diagnostics
- [ ] Replace production debug prints and silent exception catches in the streaming, display, and connection paths.
- [ ] Include timestamps and component/session context; redact PINs and pairing tokens.
- [ ] Document log location and retention behavior.
- **Verify:** Simulated connection and display failures leave actionable log entries without secrets.
- **Dependencies:** Task 1.

### Task 4: Specify protocol compatibility and cursor message
- [ ] Update `docs/plan/api-contract.md` with version negotiation, `CursorPos` payload, coordinate space, visibility, timestamp, and old-client behavior.
- [ ] Confirm message identifiers do not collide and define behavior for unsupported optional messages.
- [ ] Add protocol round-trip and compatibility cases to the implementation work.
- **Verify:** Windows and Android payload definitions agree; old/new compatibility cases are explicit.
- **Dependencies:** None.

## Checkpoint: Foundation
- [ ] Baseline report exists and can be reproduced.
- [ ] Config errors and protocol incompatibilities are actionable.
- [ ] Diagnostics avoid logging credentials.

## Phase 2: Current pipeline correctness

### Task 5: Stream cursor state independently
- [ ] Send cursor position/visibility separately from video frames.
- [ ] Render cursor overlay in Android using the agreed coordinate space.
- [ ] Remove cursor compositing from captured frames after overlay path is verified.
- **Verify:** Cursor tracks accurately with delayed/low-FPS video and across aspect ratios.
- **Dependencies:** Task 4.

### Task 6: Bound per-client frame sending
- [ ] Add capacity-one latest-frame delivery per session.
- [ ] Isolate each client's send loop and cancellation from capture and other sessions.
- [ ] Ensure keyframe recovery after dropped encoded frames where required.
- **Verify:** Throttling one client does not reduce capture or another client's delivery rate.
- **Dependencies:** Task 1, Task 4.

### Task 7: Decouple Android receive, decode, and render
- [ ] Keep socket reads independent from JPEG decode and surface drawing.
- [ ] Replace `available()` based skipping with bounded latest-frame behavior.
- [ ] Own and cancel all jobs; release decoder/surface resources on disconnect and lifecycle changes.
- **Verify:** Slow decode does not build stale-frame latency or leave duplicate receive loops after reconnect.
- **Dependencies:** Task 1.

### Task 8: Unify content, cursor, and touch geometry
- [ ] Compute an aspect-preserving content rectangle and use it for video, cursor, and touch mapping.
- [ ] Handle letterboxing and view/source dimension changes.
- **Verify:** Cursor and touch align at landscape and portrait ratios, including 1280x720 and 1920x1080 streams.
- **Dependencies:** Task 5.

## Checkpoint: Current pipeline
- [ ] Slow-client isolation and bounded queues verified.
- [ ] Android rendering remains current under load and reconnect.
- [ ] Cursor and touch alignment verified on reference ratios.

## Phase 3: Hardware media path

### Task 9: Implement DXGI capture with recovery
- [ ] Capture selected virtual display through Desktop Duplication and timestamp frames.
- [ ] Handle duplication access loss and device/display reset.
- [ ] Retain GDI fallback until compatibility is verified.
- **Verify:** Sustained capture benchmark and recovery from display reset.
- **Dependencies:** Tasks 1, 3.

### Task 10: Implement Media Foundation H.264 encoding
- [ ] Configure low-latency encoder, dimensions, bitrate, frame rate, keyframe interval, and no B-frames.
- [ ] Detect hardware capability and document software fallback behavior.
- [ ] Support keyframe requests after connection/recovery.
- **Verify:** Output decodes on the target Android device and keyframe recovery succeeds.
- **Dependencies:** Task 9.

### Task 11: Integrate Windows capture-to-stream pipeline
- [ ] Connect DXGI frames, encoder output, and per-client latest-frame slots.
- [ ] Bound buffers and account for codec dependencies when dropping frames.
- **Verify:** Compare five-minute FPS, latency, drops, and memory against Task 1 baseline.
- **Dependencies:** Tasks 6, 9, 10.

### Task 12: Implement Android MediaCodec rendering
- [ ] Decode H.264 to Surface with low-latency mode when available.
- [ ] Handle IDR, resolution changes, recovery requests, and surface lifecycle.
- [ ] Retain a compatibility fallback during rollout if needed.
- **Verify:** Sustained playback and recovery on the supported Android device set.
- **Dependencies:** Task 4, Task 10.

### Task 13: Negotiate codecs and stream settings
- [ ] Advertise supported codecs and capabilities in handshake/configuration.
- [ ] Return actual applied dimensions/FPS/bitrate and clear rejection reasons.
- **Verify:** Compatible path is selected for supported and unsupported capability combinations.
- **Dependencies:** Tasks 10, 12.

## Checkpoint: Media path
- [ ] Reference PC/phone streams 1080p for five minutes at the negotiated target.
- [ ] FPS, drops, latency, and hardware are recorded.
- [ ] Capture/decoder reset recovers without app restart.

## Phase 4: Lifecycle, UX, installer

### Task 14: Harden virtual display lifecycle
- [ ] Check VDD presence/version and supported modes.
- [ ] Track owned display handles and make create/remove idempotent.
- [ ] Report mirror fallback and verify applied topology/resolution.
- **Verify:** Repeated start/stop and simulated failure do not accumulate Screenly-owned ghost displays.
- **Dependencies:** Tasks 3, 9.

### Task 15: Make reconnect and worker teardown deterministic
- [ ] Implement bounded exponential reconnect with cancellation.
- [ ] Prevent duplicate receive loops and define foreground service/engine restart behavior.
- [ ] Expose connection states.
- **Verify:** Network loss/recovery and process restart recover once without leaked workers.
- **Dependencies:** Tasks 3, 7.

### Task 16: Add stream status and supported settings
- [ ] Show connection state, active display/resolution, FPS/bitrate, and useful errors.
- [ ] Ensure displayed settings match negotiated engine values.
- [ ] Keep manual host entry available when discovery fails.
- **Verify:** User can distinguish discovery, pairing, display, and stream failures.
- **Dependencies:** Tasks 13, 15.

### Task 17: Verify installer and recovery paths
- [ ] Detect or bundle .NET 8 according to publish mode.
- [ ] Check driver compatibility/reboot requirement and firewall rule result.
- [ ] Document clean install, upgrade, uninstall, and preservation of user config.
- **Verify:** Fresh-machine install and upgrade checklist passes; each prerequisite failure names the failed step.
- **Dependencies:** Task 14.

## Phase 5: Security and later decisions

### Task 18: Complete pairing security baseline
- [ ] Implement optional PIN/pairing-token behavior in line with ADR-010.
- [ ] Add constant-time token checks, rate limits, rotation/revocation, and secret redaction.
- [ ] Document that pairing does not encrypt traffic and decide TLS release scope.
- **Verify:** Invalid/replayed/revoked credentials are rejected and intended LAN behavior is documented.
- **Dependencies:** Tasks 3, 4.

### Task 19: Validate multi-client capacity
- [ ] Measure concurrent client resource usage and per-client FPS/latency.
- [ ] Set a tested connection limit or define per-client adaptation criteria.
- **Verify:** One slow client does not impair another; resource limits are explicit.
- **Dependencies:** Tasks 6, 11.

### Task 20: Scope deferred capabilities individually
- [ ] Decide user value and acceptance criteria separately for audio, WebRTC, other desktop platforms, enterprise management, and auto-start.
- **Verify:** No deferred feature enters implementation without a scoped decision and compatibility/security review.
- **Dependencies:** Checkpoint: Media path.

## Final checkpoint
- [ ] All applicable phase checks are complete on documented hardware.
- [ ] Wire protocol and known limitations are documented.
- [ ] Install, upgrade, failure recovery, and performance results are reviewable.
