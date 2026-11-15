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

## HELLO (C→S)
```json
{
  "protocolVersion": 1,
  "deviceName": "Pixel 7",
  "screenWidth": 1080,
  "screenHeight": 2400,
  "densityDpi": 420,
  "pairingToken": "abc123-or-null",
  "pin": "1234-or-null"
}
```

## HELLO_ACK (S→C)
```json
{ "status": "ok", "sessionId": "uuid", "pairingToken": "new-token-if-paired", "config": { "width":1920, "height":1080, "fps":60, "bitrateKbps":8000 } }
```
Status values: `ok`, `pin_required`, `pin_invalid`, `rejected`.

## VIDEO_FRAME (S→C)
Binary payload (not JSON):
```
[4 bytes: seq (uint32)]
[8 bytes: timestamp_us (uint64)]
[2 bytes: width (uint16)]
[2 bytes: height (uint16)]
[1 byte: flags]   // bit0 = keyframe (IDR)
[N bytes: H.264 Annex B NAL units]
```

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
Empty payload. Sent every 5s by both sides; if no PONG within 10s, connection considered dead → reconnect.

## DISCONNECT
```json
{ "reason": "user_closed|error", "message": "optional" }
```

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
