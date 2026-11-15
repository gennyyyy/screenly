# USB Connection Stability Audit and Plan

The USB path is Android `127.0.0.1:7543` → ADB reverse → Windows TCP server. The highest-risk faults are in tunnel setup and reconnect ownership: Windows configures ADB once with a timeout that can still block indefinitely, while Android can retry before a handshake completes and an old receive loop can close a newer socket. Fix those paths first, reuse the existing ADB and PING/PONG mechanisms, and remove only code that has no callers or duplicates existing work.

## Audit findings

### Connection stability (highest priority first)

- **Critical — ADB tunnel setup can hang and report false success.** `Program.cs` ignores the return from `WaitForExit(5000)`, then calls blocking `StandardError.ReadToEnd()`. A stuck ADB process can stall engine startup indefinitely; a failed or still-running process can also be reported as successfully configured because its exit code is never checked.
- **Critical — ADB reverse is attempted only once.** The engine does not retry if ADB is missing, the phone is offline/unauthorized, USB debugging is not approved, or the phone is connected after engine startup. It also does not restore the mapping after a cable/ADB-server reconnect.
- **Critical — Android reconnect attempts can overlap the handshake.** `MainActivity` sets `connected` only after `HELLO_ACK`; the retry coroutine can call `connect()` again while waiting, and `connect()` begins by closing the current socket.
- **Critical — receive loops share mutable socket state.** A previous `receiveLoop()` catches the close caused by reconnect and closes the `ScreenlyClient.socket` field, which may already refer to the new connection. Receive jobs are not tracked or cancelled per connection, and `close()` also leads the old loop to emit a disconnect event.
- **High — disconnect/control events can be lost behind video events.** `ScreenlyClient` puts all events in one `MutableSharedFlow` with capacity 64 and ignores the result of `tryEmit`. If the UI collector is busy drawing frames, a disconnect or handshake event may be dropped and the reconnect state can stall.
- **High — existing heartbeat is incomplete.** Windows sends `PING` every five seconds, Android does not answer inbound `PING`, and Windows does not enforce a missing `PONG` timeout. This provides neither a working liveness check nor dependable stale-link recovery.
- **High — reverse and client ports can disagree.** Android and ADB use device-side port 7543, while Windows listens on configurable `cfg.Port`; the current reverse maps 7543 to 7543 regardless of config. A non-default server port therefore breaks USB connection.
- **Medium — ADB device state is opaque.** The engine searches PATH and one conventional SDK path, but does not report the selected executable/version, distinguish no device from unauthorized/offline, or check command exit status. Multiple connected devices make unqualified `adb reverse` ambiguous.
- **Medium — host can mistake any same-named process for a healthy engine.** `App.TryStartEngine()` skips startup if any `Screenly.Engine` process exists, without checking that it belongs to this installation or is listening. This can leave the UI running while the USB endpoint is unavailable.
- **Medium — Android service does not own the connection.** `ConnectionService` only holds a foreground notification and a ten-minute wake lock; `ScreenlyClient` and retry logic live in the Activity. The service therefore cannot restore the socket after process/activity recreation, and its wake lock expires without renewal.
- **Medium — troubleshooting instructions conflict with the app.** The manual still describes manual IP entry and old LAN connection paths, while the current app auto-connects to localhost over USB. The host also says to run the same reverse command manually even though the engine tries it automatically.

### Ponytail audit (simplifications that preserve current behavior)

- **delete:** `MdnsBroadcaster` is unreferenced outside its own 211-line file, and LAN discovery has been removed. Delete it; the USB-only path needs no replacement.
- **delete:** `Session` exposes a bounded frame channel that has no readers or writers anywhere in the repository. Remove the channel and its API; current direct `SendAsync` behavior remains unchanged.
- **yagni:** VDD keepalive runs in `ParsecVddDisplay.StartPing()` every 100 ms and again in `DisplayManager`’s watchdog every 500 ms. Keep one timer (the driver-owned one for the sole current VDD implementation) to preserve keepalive with fewer IOCTLs and less timer/task code.
- **native:** USB reverse reaches the PC through localhost, but the server binds `IPAddress.Any` and the installer opens an inbound firewall rule. If USB-only remains the product contract, bind loopback and remove the firewall rule; this preserves ADB reverse while avoiding unused LAN exposure and setup.
- **yagni:** The Android touch sender and Windows input injector currently terminate at an ignored message handler, so their scopes, serialization and types do not deliver behavior. Decide whether touch input is required; if yes, finish the existing path with Windows `SendInput`, otherwise defer/remove the dead path rather than maintain a non-working subsystem.
- **shrink:** The manual documents LAN discovery, manual IP entry and Connect controls that are absent from the current USB-only Android UI. Rewrite the affected sections to match the single supported flow; no runtime code is needed.

Estimated removable code from confirmed dead/duplicate pieces: roughly 230 lines, with no dependency changes.

## Scope
- In: ADB discovery and reverse mapping, Android connection/retry lifecycle, socket ownership, heartbeat, configured port consistency, engine readiness, USB diagnostics, and removal of dead/duplicated code found in the audit.
- Out: Video throughput/latency changes, Wi-Fi transport, new ADB libraries or custom ADB protocol clients, background streaming, and completing touch input unless separately requested. Assume one authorized USB phone and an open Android viewer screen.

## Action items
[ ] Make the Windows ADB command bounded and truthful in `Screenly.Engine/Program.cs`: drain redirected output without deadlock, enforce and act on a timeout, check exit code, report the executable and actionable device/error state, and never block capture/server startup indefinitely. Keep the existing `adb` CLI; add no dependency or custom ADB client.
[ ] Keep the reverse mapping present while the engine runs: detect authorized USB device availability with the existing ADB CLI, apply `adb -s <serial> reverse tcp:7543 tcp:<cfg.Port>` when a device appears or mapping is missing, and retry failures with capped backoff. Do not clear all reverse mappings or poll/spawn ADB continuously when the mapping is healthy.
[ ] Replace the Android retry loop with one connection owner in `MainActivity`/`ScreenlyClient`: enter `Connecting` before dialing, wait for handshake success/failure or a bounded handshake timeout before retrying, use a small capped retry backoff, and avoid reconnecting merely because the ACK is slow.
[ ] Make each `ScreenlyClient` receive job own a captured socket/input/output instance; cancel and join that job before replacement, emit one terminal state per attempt, and only close that attempt’s socket. Keep disconnect/handshake control signals reliable even when frame delivery is busy; reuse the bounded frame handoff from the FPS work if present instead of adding a second event framework.
[ ] Complete the existing heartbeat contract: answer Windows `PING` with `PONG`, track acknowledgements and close the session after a bounded missed-heartbeat window so Android’s single reconnect loop can recover stale USB sessions. Do not add a second heartbeat protocol or third-party keepalive library.
[ ] Keep the Android side on port 7543 and map that device port to the configured Windows `cfg.Port`; validate one authorized device or report ambiguity when multiple USB devices are connected. Confirm startup readiness at the configured server port before the host reports the engine as available.
[ ] Simplify connection lifecycle for the assumed foreground-only viewer: make Activity disposal close/cancel its connection and remove the foreground service/wake lock if no background streaming is required. If background display is a real requirement, instead make the service the sole owner of the client and reconnect loop; do not leave two owners.
[ ] Remove the unused mDNS broadcaster, unused session frame channel, and duplicate VDD keepalive timer; for USB-only operation bind the TCP server to loopback and remove its unused inbound firewall rule. Update `USER_MANUAL.md` and host status text to document only automatic USB setup plus actionable ADB authorization/reconnect steps.
[ ] Add focused checks for ADB absent/timeout/non-zero exit, unauthorized then authorized device, delayed engine startup, cable unplug/replug, ADB server restart, host/engine restart, handshake delay, stale receive-loop termination, half-open socket heartbeat timeout, non-default `cfg.Port`, and multiple devices. Validate on a real phone that there is at most one connection attempt/session and that reconnection succeeds without restarting either app.

## Open questions
- Must the display continue streaming while the Android viewer is backgrounded or the screen is off? The simplest plan assumes no; if yes, the foreground service must own the connection and retry lifecycle.
- Can more than one authorized Android device be connected to this PC at a time? The plan assumes one and will report ambiguity instead of silently selecting the wrong device.
