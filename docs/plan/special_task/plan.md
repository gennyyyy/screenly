# Screenly System Improvement Plan

## Overview

This plan turns the findings in `issues-and-inefficiencies.md` into an ordered path from the current working Windows-to-Android stream to a reliable, low-latency second-screen system. The issue report describes the current GDI+/JPEG path and Android bitmap rendering; the architecture and ADRs already select DXGI Desktop Duplication, Media Foundation H.264, MediaCodec, latest-frame delivery, a single TCP connection, and Parsec VDD. Treat those documents as the intended direction, and validate current behavior before implementing changes. The existing `implementation-plan.md` remains the original end-to-end build checklist; this plan addresses the reported gaps and sequencing.

## Outcomes and success measures

- Cursor position is independent of video frame timing and aligns with displayed content across aspect ratios.
- A slow client cannot stall capture or other sessions; the client renders recent frames without accumulating latency.
- The primary Windows-to-Android path reaches a sustained 60 fps at 1080p where hardware supports it, with measured latency and a documented fallback when it does not.
- Connection, display, and streaming failures are visible, recoverable where possible, and diagnosable from structured logs.
- Installer prerequisites and driver/display failures are reported clearly; a clean install and upgrade are repeatable.
- Existing wire clients either remain compatible or fail with a clear protocol-version error.

Performance targets are goals to validate on named test hardware, not guarantees for every device. Record the PC, Android model, connection type, resolution, bitrate, FPS, dropped frames, and latency with each benchmark.

## Guiding decisions

- Follow ADR-008/ADR-007 for DXGI capture and H.264 hardware encode/decode. Keep GDI/JPEG as a tested fallback until the new path is proven on supported systems.
- Keep ADR-004's single TCP transport for this roadmap. Use bounded latest-frame queues per client; revisit UDP/WebRTC only if measurements show TCP is the remaining bottleneck.
- Extend the wire protocol deliberately for cursor metadata. Define compatibility/version behavior in `api-contract.md` before sending the new message.
- Keep JSON configuration. Validate and centralize defaults instead of introducing a database.
- Do not silently expand the product scope to audio, enterprise management, or other desktop platforms before the core stream is reliable.

## Dependency order

```text
Baseline measurements and config/protocol contracts
  ├── cursor metadata + display-coordinate mapping
  ├── bounded per-client delivery + Android latest-frame pipeline
  └── structured logging + explicit recovery
          ↓
DXGI capture → H.264 encode → protocol integration → Android decode
          ↓
display lifecycle + connection UX + installer verification
          ↓
security hardening and optional product capabilities
```

## Task list

### Phase 1: Make the current path measurable and predictable

1. **Establish a reproducible baseline.** Capture engine FPS, Android rendered FPS, capture/encode/decode/draw durations, queue depth or dropped frames, and end-to-end latency for USB and Wi-Fi. Include current resolution and device details in the results. Use this to verify each subsequent performance change.
2. **Validate and centralize runtime configuration.** Check port, resolution, frame rate, JPEG quality/bitrate, and retry values at startup; reject or clamp invalid values with a user-visible explanation. Remove duplicated hardcoded defaults where the config already owns them.
3. **Add structured file logging.** Replace production debug `Console.WriteLine` and silent catches with levels, timestamps, component/session context, and exception details. Keep console output useful for development and ensure secrets such as PINs/tokens are redacted.
4. **Define a wire-protocol compatibility policy.** Specify protocol version negotiation, unknown message behavior, and optional extensions in `api-contract.md`. Add a `CursorPos` server-to-client message carrying stream/display dimensions, coordinates, visibility, and timestamp; define whether older clients may connect without cursor overlay.

**Checkpoint:** Baseline can be reproduced; invalid configuration and protocol-version mismatch produce actionable errors; logs show connection and pipeline failures without credentials.

### Phase 2: Fix visible correctness and latency issues on the current pipeline

5. **Separate cursor updates from image frames.** Stop drawing the cursor into GDI frames; publish cursor state independently and render it in an Android overlay. Normalize cursor coordinates against the actual content viewport, accounting for letterboxing and aspect-preserving scaling. Define behavior when the cursor is hidden or outside the display.
6. **Bound frame delivery per session.** Give each session a capacity-one latest-frame slot, with independent send loops so one slow socket cannot block capture or another client. Keep control/input messages ordered and ensure disconnect cancels the session worker.
7. **Separate Android receive, decode, and render work.** Keep socket reads on an I/O coroutine, feed a bounded latest-frame pipeline, and perform decode/render off the receive loop using lifecycle-owned jobs. Remove reliance on `InputStream.available()` as a correctness mechanism. Ensure teardown cancels every worker and releases decoder/surface resources.
8. **Correct viewport and touch mapping.** Use a single computed content rectangle for video fit, cursor overlay, and touch coordinate conversion. Test landscape/portrait and differing source/view aspect ratios so coordinates remain aligned.

**Checkpoint:** Under a deliberately slow client, capture and other clients continue; Android shows recent frames without growing delay; cursor and touch align at all tested aspect ratios; reconnect does not accumulate workers.

### Phase 3: Replace the performance bottleneck with the accepted media path

9. **Implement DXGI Desktop Duplication behind the capture interface.** Capture the selected virtual display, handle access-lost/device-reset events, and expose timestamped latest frames. Preserve GDI as a fallback until compatibility is documented.
10. **Implement Media Foundation H.264 encoding.** Negotiate dimensions, frame rate, bitrate, keyframe interval, low-latency/no-B-frame settings, and hardware/software fallback. Ensure a newly connected or recovered client can request an IDR frame.
11. **Integrate and validate the Windows video pipeline.** Connect capture to encode and bounded per-session delivery; avoid unnecessary CPU/GPU copies and unbounded buffers. Measure encode time, FPS, memory, and dropped frames against Phase 1.
12. **Implement Android MediaCodec decode and surface rendering.** Configure AVC low-latency where supported, handle keyframes and stream reconfiguration, render to the surface, and release/recreate resources on lifecycle changes. Keep the JPEG path available during rollout if needed.
13. **Negotiate capabilities and fallback.** Extend CONFIG/HELLO behavior to identify codec and supported dimensions/rates. Select a compatible path and return the actual applied settings; unsupported hardware must fail clearly or use the documented fallback.

**Checkpoint:** On a documented reference PC/phone pair, sustain 1080p at the negotiated target (goal: 60 fps) for five minutes over USB and Wi-Fi, report FPS/drops/latency, and recover from a decoder/capture reset without restarting the app.

### Phase 4: Make display, connection, and installation failures recoverable

14. **Harden virtual-display lifecycle.** Validate VDD presence/version and supported modes; create and remove displays idempotently; track owned display handles; clean up stale owned displays after abnormal shutdown where safe. Report mirror-mode fallback explicitly and verify topology/resolution after each change.
15. **Make reconnect and worker lifecycle deterministic.** Use bounded exponential backoff with cancellation and jitter as appropriate; prevent duplicate receive loops; define foreground-service behavior and engine restart policy. Surface disconnected/reconnecting/connected states.
16. **Add useful status and settings.** Show connection state, selected display/resolution, negotiated FPS/bitrate, and actionable error text. Add only settings supported by the negotiated engine configuration; retain manual host entry as a fallback if discovery is unavailable.
17. **Verify installer prerequisites and recovery.** Bundle or detect the .NET 8 runtime as appropriate for the chosen publish mode; check driver compatibility and reboot requirements; verify firewall rule creation; make install/upgrade/uninstall behavior repeatable. Produce a clean-install checklist and preserve user config on upgrade.

**Checkpoint:** Clean install, upgrade, driver unavailable, display creation failure, network loss, and engine crash all yield an understandable state and documented recovery; manual IP and USB workflows are verified.

### Phase 5: Security and optional capabilities

18. **Complete the LAN security baseline.** Implement and verify the accepted optional PIN/pairing-token flow before exposing the listener by default on untrusted networks. Add constant-time token comparison, rate limits, rotation/revocation behavior, and clear trust-boundary documentation. Decide separately whether TLS is required for the intended release; do not describe PIN pairing as encryption.
19. **Review multi-client policy.** Validate bounded per-client queues and resource usage with concurrent clients. Decide whether to cap clients or adapt quality per client based on measured CPU/GPU/network use.
20. **Evaluate later features by user need and evidence.** Audio streaming, WebRTC/NAT traversal, macOS/Linux support, SSO/MDM, and auto-start improvements should each receive a separate scope and acceptance criteria. WebRTC is not a prerequisite for the LAN/USB product; auto-start should follow engine lifecycle and user-consent requirements.

## Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| DXGI or hardware encoder behavior differs across GPU drivers | Stream may fail on a subset of PCs | Keep a tested fallback; report capability and driver failures; validate a reference-device matrix. |
| Cursor/protocol change breaks existing Android builds | Client cannot connect or cursor is misplaced | Version the extension, define optional behavior, and verify old/new client-server combinations. |
| Latest-frame dropping breaks dependent codec frames | Decoder corruption until next IDR | Preserve control ordering; request/send an IDR after recovery or deliberate H.264 frame loss; test keyframe cadence. |
| VDD cleanup removes a display not created by Screenly | User display disruption | Track ownership/handles and only clean up displays proven to belong to the current or stale Screenly instance. |
| Installer changes require admin/reboot and may fail partially | Broken installation or unavailable virtual display | Check prerequisites before mutation, report exact step/result, and document rollback/reboot requirements. |
| Performance targets vary by hardware and network | Misleading success criteria | Publish measurements with hardware/network context and treat 60 fps as a reference target. |
| PIN is mistaken for transport encryption | Sensitive traffic exposed on shared networks | State explicitly that PIN pairing authenticates but does not encrypt; make TLS a release/security decision. |

## Decisions needed before implementation

- Which PC/GPU and Android devices define the initial supported and benchmarked hardware set?
- Should the product require pairing by default for LAN connections, or retain opt-in pairing for the first release?
- Is the intended release limited to local LAN/USB, or must it support untrusted networks (which changes the TLS priority)?

## Definition of done

- The relevant phase checkpoint passes on documented reference hardware.
- New protocol behavior is documented and backward-compatibility behavior is verified.
- User-visible failure states and structured diagnostics exist for affected paths.
- No unbounded frame, coroutine, or session queues remain in the streaming path.
- Installer and recovery behavior are documented for release-affecting changes.
