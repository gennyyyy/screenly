# Display Resolution Match + VDD Accumulation Fix — Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the virtual display's resolution equal the connected Android device's resolution (capped), and stop Parsec VDD displays from accumulating across connects/recreates.

**Scope (in):** `windows/Screenly.Core/Display/*`, `windows/Screenly.Engine/Program.cs` display flow, diagnostic logging. **Out:** cursor compositing/stability, H.264 encoder null return, Android UI, protocol.

**Spec:** this plan. **Tech:** C# .NET 8, Parsec VDD 0.45.

## Global Constraints
- Do not touch cursor code (`CursorDrawer`, `CursorShapeCapture`) or Android files.
- Preserve JPEG capture path and touch mapping (streamW/H from HelloAck).
- No new dependencies. Keep overlapped I/O for ADD.
- Every change must be reversible; log before/after for each display operation.

## Root Cause Analysis

### Windows side
- `ParsecVddDisplay.CreateAsync`: ADD ioctl writes width/height/refresh little-endian (works). Cleans stale indices from `vdd-indices.txt`, then **deletes the file unconditionally** — if REMOVE failed, orphans become permanent.
- `ParsecVddDisplay.RemoveAsync`: writes the VDD index **big-endian** (`buf[0]=high, buf[1]=low`) while ADD is little-endian. Almost certainly why REMOVE returns false. Uses sync handle, stops ping first (correct). Retries 32-byte then 2-byte.
- `DisplayManager` (wrapper) `RemoveAsync(CancellationToken)` uses `_active`, swallows exceptions, leaves `_active` set on failure.
- `Program.EnsureDisplayForClient` (first connect): creates at persisted/config initial res, then if dynamic, calls `RecreateTo(capped client res)` → RemoveAsync (fails) + CreateAsync → **2 displays on first connect**, plus persisted mismatch.
- `Program.CreateCaptureAsync`: H.264 path inits encoder with requested `width/height` (possibly != actual capture dims) → mismatch. JPEG path already uses actual.
- `Program.RecreateTo`: swallows RemoveAsync failure, creates new display anyway → accumulation.
- No verification that ADD actually set the resolution; `GdiCapture.Start` reads actual via `EnumDisplaySettings` but result isn't compared/logged against requested.

### Android side
- Sends `screenWidth/Height = displayMetrics.widthPixels/heightPixels` (device native). Correct.
- `HelloAck.config.width/height` → `streamW/H` (capture actual). Touch maps via streamW/H. Consistent as long as Windows reports actual capture dims (it does).
- So Android already adapts; the mismatch is Windows-side (virtual display res != captured res != device res).

### Why accumulation persists across runs
- Failed REMOVE leaves orphan VDD. `CreateAsync` cleanup tries to remove persisted indices but deletes the file regardless of success → next run has no record → orphans permanent. Manual deletion required.

## Approach (phased, each phase independently verifiable)

### Phase 0 — Diagnostic (no behavior change)
Add logging in `CreateDisplayAt`: after `CreateAsync` + 5s delay, read actual Parsec display res via `GdiCapture.FindVirtualDisplayDevicePublic()` + `EnumDisplaySettings`, log `requested=WxH actual=WxH match=bool`. Run once to determine whether ADD honors the buffer. Decision gate: if match=false for all, stop and investigate buffer layout/driver; do not proceed to Phase 1's recreate removal.

### Phase 1 — Fix REMOVE (root cause of accumulation)
- Change REMOVE ioctl index write to **little-endian** in both the 32-byte and 2-byte buffers (and in `CreateAsync` stale-cleanup loop).
- `CreateAsync` stale cleanup: only delete `vdd-indices.txt` entries that were successfully removed; keep failed indices for next run. Delete the file only when all entries removed (or empty).
- `CreateAsync`: log `IoControl REMOVE result` per index.
- `RemoveAsync`: on failure, do NOT throw? Keep throwing (Program catches) but ensure `_displays`/handle state consistent: leave handle in map for retry (already does). Add `Log` of Win32 error via `Marshal.GetLastWin32Error()`.

### Phase 2 — Eliminate unnecessary recreates (root cause of per-connect accumulation)
- `EnsureDisplayForClient` first-connect: if `clientW>0 && clientH>0`, compute target = `CapResolution(clientW, clientH, capW, capH)` and call `CreateDisplayAt(target)` directly. Do not create at persisted/config then `RecreateTo`. Use persisted resolution only when client dims unavailable.
- Persist target for device after creation.
- `RecreateTo` (still used for later Config changes): after RemoveAsync, set `_active`/handle state correctly; if RemoveAsync throws, still proceed to create (accept orphan, persisted for next-run cleanup) — but log clearly. Do not silently swallow.
- `DisplayManager.RemoveAsync`: on failure, leave `_active` set so caller can retry; log. (Already leaves it; just add log.)

### Phase 3 — Encoder dimension consistency (H.264 path)
- `CreateCaptureAsync`: init H.264 encoder from `dc.Width/dc.Height` (actual), not requested width/height. Fallback path already uses `gc`.

### Phase 4 — Verification
- Unit tests: `ConfigStore.CapResolution`/`ScaleToCap` edge cases (already pure; add tests for portrait + cap). 
- Manual: create at 1280x720 (multiple of 8), verify actual = 1280x720 in log. Connect Android, verify HelloAck dims == device capped dims, no letterbox gap beyond aspect. Run connect/disconnect 5 times, verify display count doesn't grow (check `Get-PnpDevice Parsec` before/after).
- Check `vdd-indices.txt` empty/absent after clean shutdown.

## Files touched
- Modify: `windows/Screenly.Core/Display/DisplayManager.cs` (byte order, index file handling, logging)
- Modify: `windows/Screenly.Core/Display/DisplayManagerClass.cs` (RemoveAsync logging/state)
- Modify: `windows/Screenly.Engine/Program.cs` (EnsureDisplayForClient first-connect, CreateCaptureAsync encoder dims, CreateDisplayAt diagnostic log)
- Test: `windows/Screenly.Tests/` add CapResolution tests

## Interfaces
- Consumes: `IVirtualDisplay.CreateAsync/RemoveAsync` (unchanged signatures).
- Produces: none new. `DisplayManager.DisplayCount` unchanged (logging only).

## Risks / what could break
- If ADD does NOT honor buffer (Phase 0 gate), Phase 2's "create at client res" still creates a display but at driver's default res; Android sees actual capture res (HelloAck), so streaming stays consistent but virtual desktop res won't match device. Mitigation: Phase 0 gate stops before claiming success; document limitation if driver can't.
- Changing REMOVE to little-endian could be wrong if driver expects big-endian (current) and REMOVE actually works in some cases. Mitigation: log result; if LE fails too, try both and keep whichever succeeds; keep orphan cleanup as fallback.
- Removing the first-connect RecreateTo changes initial display timing (one CreateAsync instead of two) — should be faster, not slower. Watch for capture reading stale res; keep 5s delay.
- H.264 encoder dim change only affects H.264 path; JPEG unaffected.

## Out of scope
- Cursor compositing/stability, H.264 Encode returning null, Android changes, protocol changes, cleanup of build artifacts.
