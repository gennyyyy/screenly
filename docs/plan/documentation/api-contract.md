# API Contract — Screenly Wire Protocol

Transport: single TCP connection, little-endian. Every message: `[4 bytes: payload length (uint32)] [1 byte: type] [payload]`.

Max message size: 4 MiB (video frames must fit; 1080p keyframe ~200KB typical).

## Message types

| Code | Name | Direction | Purpose |
|---|---|---|---|
| 0x01 | HELLO | C→S | Client handshake |
| 0x02 | HELLO_ACK | S→C | Server accepts/rejects |
| 0x03 | VIDEO_FRAME | S→C | H.264 frame |
| 0x04 | INPUT_TOUCH | C→S | Touch event batch |
| 0x05 | INPUT_KEY | C→S | Key event |
| 0x06 | CONFIG | both | Resolution/bitrate/fps negotiation |
| 0x07 | PING | both | Keepalive |
| 0x08 | PONG | both | Keepalive reply |
| 0x09 | DISCONNECT | both | Graceful close |
| 0x0A | ERROR | both | Error report |
| 0x0B | CURSOR_POS | S→C | Cursor position update (separate from video frames) |
| 0x0C | CURSOR_SHAPE | S→C | Cursor shape image (PNG, sent only on change) |
| 0x0D | REQUEST_KEYFRAME | C→S | Empty payload; asks the engine for an IDR |

## Ordering invariant

No `VIDEO_FRAME` may be sent before `HELLO_ACK` for that session. The session is not streaming-ready until the ACK has been written.

## HELLO (C→S)
```json
{
  "protocolVersion": 1,
  "deviceName": "Pixel 7",
  "screenWidth": 1080,
  "screenHeight": 2400,
  "densityDpi": 420,
  "pairingToken": "abc123-or-null",
  "pin": "1234-or-null",
  "supportedCodecs": ["h264", "jpeg"],
  "maxWidth": 1920,
  "maxHeight": 1080,
  "maxFps": 60
}
```

## HELLO_ACK (S→C)
```json
{ "status": "ok", "sessionId": "uuid", "pairingToken": "new-token-if-paired", "config": { "width":1920, "height":1080, "fps":30, "bitrateKbps":8000, "codec": "jpeg|h264", "keyframeIntervalSec": 2 } }
```
Status values: `ok`, `pin_required`, `pin_invalid`, `rejected`.

## VIDEO_FRAME (S→C)
Binary payload (not JSON):
```
[4 bytes: seq (uint32)]
[8 bytes: timestamp_us (uint64)]  // monotonic microseconds from Stopwatch, not wall-clock ticks
[2 bytes: width (uint16)]
[2 bytes: height (uint16)]
[1 byte: flags]   // bit0 = keyframe (IDR); bit1 = JPEG
[N bytes: H.264 Annex B NAL units or JPEG bytes]
```
Every keyframe access unit must contain SPS and PPS in-band so a decoder that joins late can start immediately. Timestamps are for ordering/latency measurement; they are not required to match wall clock.

## REQUEST_KEYFRAME (C→S)
Empty payload. The engine calls `RequestKeyframe()` on the session encoder. Requests are rate-limited to one per 500 ms per session. A client that dropped a frame must send this and discard non-key frames until the next IDR.

## CURSOR_POS (S→C)
Binary payload (not JSON):
```
[4 bytes: x (int32)]        // cursor x in stream/display coordinates (0..width)
[4 bytes: y (int32)]        // cursor y in stream/display coordinates (0..height)
[1 byte: visible (0/1)]     // 1 = cursor visible on this display, 0 = hidden/outside
[8 bytes: timestamp_us (uint64)]  // capture timestamp for sync with video frame
```
Sent at ~30 Hz (or with each frame). Client renders cursor as overlay on top of video.
Coordinates are in the same space as VIDEO_FRAME dimensions.

## CURSOR_SHAPE (S→C)
Binary payload (not JSON):
```
[4 bytes: width (int32)]     // cursor image width in pixels
[4 bytes: height (int32)]    // cursor image height in pixels
[4 bytes: hotspotX (int32)]  // hotspot x offset within cursor image
[4 bytes: hotspotY (int32)]  // hotspot y offset within cursor image
[N bytes: PNG data]          // transparent 32-bit PNG of the cursor
```
Sent only when cursor shape changes (on connect, reconnect, or cursor handle change). Client decodes and caches the PNG, then draws it as overlay at the CursorPos location (offset by hotspot). If no CursorShape received or decode fails, yellow marker fallback is used.

## INPUT_TOUCH (C→S)
```json
{
  "events": [
    { "type": "down|move|up|cancel", "pointerId": 0, "x": 120.5, "y": 340.0, "pressure": 1.0, "tUs": 123456789 }
  ]
}
```
Coordinates are in stream-resolution space (0..width, 0..height). Server scales to virtual-display resolution if they differ.

## INPUT_KEY (C→S)
```json
{ "events": [ { "type": "down|up", "keyCode": 27, "tUs": 123456789 } ] }
```
Android keycodes mapped to Windows VK codes server-side.

## CONFIG (both)
```json
{ "width": 1920, "height": 1080, "fps": 60, "bitrateKbps": 8000, "keyframeIntervalSec": 2 }
```
Server sends to propose; client may reply with adjusted prefs. Final config applied server-side (display mode + encoder). Client reconfigures decoder on dimension change.

## PING / PONG
Empty payload. Windows sends PING every 5s; Android answers with PONG. The current engine records `LastPong` but does not yet enforce a missed-PONG timeout; until it does, the TCP connection itself is the liveness boundary.

## DISCONNECT
```json
{ "reason": "user_closed|codec_change|fps_change|error", "message": "optional" }
```
Terminal. The sender writes the message and then closes the connection. The receiver treats it as terminal, closes its side, and reconnects. Android must handle `DISCONNECT` explicitly; it must not rely only on a read error.

## ERROR
```json
{ "code": "ENCODER_FAIL|DISPLAY_LOST|PROTOCOL", "message": "..." }
```

## Error semantics
- Unknown message type → send ERROR{PROTOCOL}, close connection.
- Oversized message (>4MiB) → close connection (no ERROR, assume malicious).
- HELLO with unsupported protocolVersion → HELLO_ACK{rejected}.
- Frame decode failure on client → request keyframe via CONFIG with `requestKeyframe: true` (extension; server sends IDR next frame).

## Idempotency
Connection is stateful; reconnect creates new session. Server treats each TCP connection as a fresh session. No idempotency keys needed (transport-level retry is the client's job).

## Engine status file (engine → Host)

`%APPDATA%\Screenly\status.json`, written atomically. Current fields plus H.264 diagnostics:

```json
{
  "codec": "h264|jpeg",
  "width": 1920,
  "height": 1080,
  "fps": 30,
  "connectionType": "USB",
  "connectedDevices": 1,
  "encoderError": null,
  "framesEncoded": 1234,
  "framesDropped": 12,
  "backpressureSkips": 9,
  "slotReplacements": 3,
  "pacerOverruns": 4,
  "bytesOut": 5678901,
  "encodeMsMin": 3.1,
  "encodeMsAvg": 8.4,
  "encodeMsMax": 22.7,
  "captureMsMin": 4.8,
  "captureMsAvg": 11.6,
  "captureMsMax": 19.2,
  "sendMsAvg": 1.2,
  "sendMsMax": 9.5
}
```

`encoderError` is null when healthy and contains the specific exception type/message when initialization or encoding fails. `framesEncoded`, `bytesOut`, and encode-time fields reset per engine session or per encoder reinitialization.

`fps` is measured from frames actually written to the client over the last second (0 when nothing is being sent); it is not derived from encoded or queued frames. `framesDropped` counts frames lost to backpressure since the session started: capture/encode skipped because a frame was still waiting for the sender, plus encoded frames replaced in the one-slot holder. The new counters split that total and add latency/pacing evidence:

- `backpressureSkips`: frames where capture/encode was skipped because `Frames.HasPending` was true.
- `slotReplacements`: encoded frames published into a slot that still held an unsent frame (`Publish` returned true). With one producer per session this stays 0; a non-zero value means a second producer is publishing into the same session slot.
- `pacerOverruns`: frames where the loop returned at or after the deadline (frame work plus scheduling), forcing the next deadline to rebase at now (cumulative since engine start, not per session).
- `sendMsAvg` / `sendMsMax`: `VIDEO_FRAME` write time in the session sender task (including the session write-lock wait), cumulative since the session started. Min/avg/max are tracked; only avg/max are written.
- `captureMsMin` / `captureMsAvg` / `captureMsMax`: wall time spent inside `ICapture.TryGetFrame` in the engine frame loop, measured per successful frame and cumulative since the session started (0 before the first frame). Splits capture cost out of the frame budget so a missed frame period can be attributed to capture versus encode versus send; failed `TryGetFrame` calls are not sampled.

`framesDropped = backpressureSkips + slotReplacements` for the same session. When several sessions are connected, the fields reflect the first active session: `skipped` growing while `pacerOverruns` stays flat points at the sender/socket; `pacerOverruns` tracking `skipped` points at capture/encode exceeding the frame budget.
