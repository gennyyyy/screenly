# Plan — Per-Client Virtual Displays Without VDD Churn

**Status:** Proposed. No implementation dispatched.

## Goal

Allow multiple Android devices to connect at the same time, each with its own independent Windows virtual display, without creating/removing Parsec VDD displays on client connect, disconnect, or resolution change.

## Non-goals

- Touch/pen injection per display (still a stub).
- Audio.
- Hardware H.264 encoding.
- Cross-platform support.

## Current state

- One shared Parsec VDD display is created/reused for all sessions.
- Multiple sessions are accepted, but they mirror the same display.
- A different client resolution removes and recreates the shared display, invalidating existing sessions.
- Historical per-client add/remove produced ghost displays, failed REMOVE ioctls, topology changes, receive-loop stalls, and real-monitor disruption. This is the “VDD churn” being avoided.

## Core invariant

**A VDD is added only when the pool grows. When its last client disconnects, its removal is scheduled after a short grace period; a reconnect cancels it. Removals are serialized, verified, and rate-limited so they cannot become an add/remove storm.**

| Event | Allowed VDD operation |
|---|---|
| First client, pool empty | ADD one display |
| Second client, free lease exists | REUSE; no ADD/REMOVE |
| Client disconnect | Schedule REMOVE after a 5 s grace; cancel if the client reconnects |
| Client reconnect during grace | Cancel removal; reuse the lease |
| Resolution change | Change mode on the existing lease; no ADD/REMOVE |
| Codec/FPS switch | Disconnect sessions; normal teardown rules apply; no immediate ADD |
| Engine shutdown | REMOVE all pool displays immediately |
| Engine crash | Startup stale-index cleanup removes orphaned indices |

Removal is the one remaining churn source. It is bounded by a single serialized teardown worker, a grace period, and a rate limit (for example, one add/remove per 2 s). It is never interleaved with a create.

## Architecture

### `DisplayPool`

Owns the Parsec VDD driver and a fixed maximum number of leases.

```text
DisplayLease
  Id
  DisplayHandle
  DeviceName          // \\.\DISPLAYn mapped to the VDD index
  Width, Height
  Orientation         // 0/90/180/270
  State               // Free | Assigned | PendingRemove | Removed
  SessionId?
  RemoveDueAt?        // grace deadline
```

- `MaxDisplays` config, default 4, clamp 1..8 (Parsec VDD supports 16; CPU is the real limit).
- `Acquire(width, height)` under one `SemaphoreSlim`:
  1. exact-resolution lease in `Free` or `PendingRemove` → cancel removal, assign;
  2. free lease → change mode via registry preset + `ChangeDisplaySettingsEx`;
  3. pool below max → ADD + resolve device name + assign;
  4. otherwise reject with `NO_DISPLAY`.
- `Release(lease)` marks `PendingRemove`, sets `RemoveDueAt`, and signals the teardown worker. A reconnect cancels the deadline and reuses the lease.
- The teardown worker removes one lease at a time after the grace period. It stops the capture/encoder/sender, removes the VDD, verifies the monitor is gone, and re-enumerates device mappings. On failure it quarantines the index for startup cleanup and does not retry indefinitely.
- Counters: `Created`, `Reused`, `ModeChanges`, `RemovalScheduled`, `RemovalCanceled`, `Removed`, `RemoveFailures` in `status.json` and the engine log.

### Display identity

- After ADD, map the VDD index to the Windows device name (`EnumDisplayDevices`, Parsec device enumeration, monitor device path).
- Capture is constructed with that explicit `DeviceName`; never use “last Parsec device” once more than one exists.
- Validate width/height/orientation by reading `EnumDisplaySettings` back after mode change.
- If mapping fails, release the lease and return `DISPLAY_MAPPING_FAILED`; do not stream from the primary screen silently.

### Session ownership

`SessionState` owns one `DisplayLease` plus its capture, encoder, latest-frame slot, sender, and orientation.

Remove `sharedDm`/`sharedHandle`/`sharedW`/`sharedH`. The frame loop already iterates session states; each now captures its own display.

### Resolution and registry presets

Parsec VDD supports up to five registry preset slots. The pool may have more displays than distinct modes.

- Maintain a mode table of distinct `(width, height, hz)` presets.
- Reuse an existing preset when possible.
- If all five slots are occupied and a new distinct mode is requested, reject `MODE_UNSUPPORTED` or map to the nearest supported mode. Do not remove/re-add a display to change mode.
- Keep the existing installer ACL grant for `HKLM\SOFTWARE\Parsec\vdd`.

### Display topology

- Do **not** call `SetDisplayConfig(SDC_TOPOLOGY_EXTEND | SDC_APPLY)` on connect; it previously disabled real monitors.
- Use `ChangeDisplaySettingsEx` on the lease’s device only.
- Phase 0 tests whether Windows extends the desktop automatically. If not, use explicit `SetDisplayConfig` paths limited to the virtual display; never apply a global topology.
- On disconnect, the display exists only for the grace period. The teardown worker stops streaming first, then removes the VDD; Windows may briefly reflow the desktop during removal. This is the accepted cost of per-disconnect removal.
- If removal fails, the index is persisted for startup cleanup and excluded from reuse; do not loop REMOVE attempts while the engine is live.

### Cursor and orientation

- Cursor loop already iterates sessions and clips to each capture’s monitor rect; verify it sends visible only to the display containing the cursor and hidden to the others.
- Orientation is per capture; each `CONFIG`/`HELLO_ACK` carries that lease’s orientation. Android already locks to it.
- No Android protocol change required for multi-display; add optional `displayId` to `HELLO_ACK` for diagnostics and `NO_DISPLAY`/`DISPLAY_MAPPING_FAILED` error codes.

### Status and Host

Add per-display status:

```json
"displays": [
  { "displayId": 1, "width": 800, "height": 1280, "orientation": 0, "state": "assigned", "sessionId": "..." },
  { "displayId": 2, "width": 1280, "height": 800, "orientation": 1, "state": "parked", "sessionId": null }
]
```

Host can continue showing `connectedDevices`; a later UI pass can list leases.

## Phases

### Phase 0 — Prove identity and topology (no behavior change)

- Log every Parsec device name/index and its width/height/orientation.
- Add two VDDs manually, confirm index → device-name mapping and capture isolation.
- Confirm `ChangeDisplaySettingsEx` can change a single display’s mode without `SetDisplayConfig`.
- Confirm real monitors are untouched.
- **Gate:** two displays can be captured independently; mapping is stable across create/remove cycles.

### Phase 1 — Pool core, single client

- Add `DisplayPool`/`DisplayLease`, `MaxDisplays=1` feature flag.
- Replace shared-display acquisition with pool acquisition.
- Disconnect schedules removal after the grace period; reconnect cancels it.
- Add counters/logs and the serialized teardown worker.
- **Gate:** single-client JPEG/H.264, codec/FPS switch, orientation, cursor, ADB, installer, and reconnect all pass; disconnect removes exactly one display after grace; a reconnect inside grace does not add/remove.

### Phase 2 — Multi-client admission

- Raise `MaxDisplays` to 4 (config).
- Per-session capture/encoder/sender already exist; remove shared state.
- Reject the 5th client with `NO_DISPLAY`.
- **Gate:** two devices get independent displays; no ADD on second connect; each disconnect removes only its own display after grace; reconnects inside grace reuse the lease; no ghost accumulation.

### Phase 3 — Resolution, orientation, cursor

- Mode change on existing lease; preset reuse; `MODE_UNSUPPORTED` path.
- Per-lease orientation in `CONFIG`/`HELLO_ACK`; Android locks per device.
- Cursor visible on one display only; hidden on others.
- **Gate:** two devices at different resolutions; orientation changes on one display do not affect the other; cursor stays on the correct display.

### Phase 4 — Capacity and regression

- Measure 1/2/3 clients at 800×1280 H.264; record capture/encode/send/FPS.
- Set a tested `MaxDisplays` default; document that software encoding is CPU-bound.
- Churn regression: engine log must show `ADD` only at pool growth and `REMOVE` only for a display whose last client stayed disconnected past grace, never on reconnect, codec/FPS switch, or resolution change.
- Existing regression: JPEG/H.264, 30/60 selector, codec switch, orientation, cursor, reconnect, installer, bundled adb.

## Acceptance

- Two Android devices connect over USB, each with its own virtual display and stream.
- No `VDD_IOCTL_ADD` on the second connect if a free lease exists.
- Each disconnected display is removed exactly once after the grace period; a reconnect inside grace cancels removal.
- Reconnect reuses the same display; no ghost display growth.
- Real monitors are never disabled by topology changes.
- Single-device behavior is unchanged when `MaxDisplays=1`.
- `dotnet test` and Android build pass.

## Risks

- **Index → device-name mapping** is the highest risk; validate before pool work.
- **Removal is churn**: per-disconnect removal can reflow the desktop and briefly disturb other monitors. The grace period, serialization, verification, and rate limit bound it but cannot remove it entirely.
- **Resolution presets** are limited to five; excess distinct modes must be rejected or snapped.
- **CPU**: each client has its own capture (GDI ~13 ms) and software encoder (~2.5 ms). Two clients may not hold 30 FPS; measure and set a tested limit.
- **Teardown failure** can orphan an index; keep the existing startup cleanup and do not retry indefinitely while live.
- **Topology changes** can reflow the desktop; do not apply global extend on connect.

## Open questions

- Grace period before removal: **5 s** (confirmed) to absorb transient USB reconnects; reconnect cancels removal.
- Is it acceptable that removal can briefly reflow the Windows desktop? The alternative is parked displays, which you rejected.
- Maximum supported devices: 4 default; tested limit may be lower due software encode.
- Which client resolutions must be supported simultaneously? Five registry presets are the hard limit.
