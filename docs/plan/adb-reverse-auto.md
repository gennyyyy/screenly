# Plan — Automatic ADB Reverse Tunnel

**Goal:** The Windows engine reliably establishes and maintains `adb reverse tcp:7543 tcp:<port>` for the whole time it runs, so the Android app connects over USB without manual commands.

**Scope:** `windows/Screenly.Engine/Program.cs` adb setup block + `FindAdb`. No Android changes.

## Problem (verified)
- Engine logs `ADB reverse timed out` and stops trying after 10 failed attempts (`attempt < 10`).
- `WaitForExit(5000)` is too short when the adb daemon is cold-starting; the read order can also hang.
- Once the loop exits, no further auto-setup happens for the lifetime of the process.

## Design
1. Run the adb setup loop for the **entire** engine lifetime (`while (!cts.IsCancellationRequested)`), never give up permanently.
2. Per attempt: 15s timeout; if adb missing, wait 5s and retry (don't terminate).
3. On failure: log, wait 3s (with jitter not required), retry.
4. On success: keep the existing 10s re-verify cycle.
5. Fix process I/O: wait with timeout first, then read stdout/stderr synchronously; kill only on timeout.
6. `FindAdb`: prefer PATH via `where`, fall back to the common SDK path; log which was chosen.

## Tasks
- E1: Refactor adb block in `Program.cs` per design. Remove the `attempt < 10` cap.
- E2: Add a unit-testable helper? The adb logic is process-bound; skip unit tests, rely on manual verification + build.
- E3: Build engine; run `dotnet build`; smoke-run engine with phone connected and confirm log shows "ADB reverse configured" without manual command and phone auto-connects.

## Acceptance
- Fresh engine start with a connected phone establishes reverse within ~20s and phone connects without manual `adb reverse`.
- Engine keeps retrying if started before the phone is plugged in; once plugged, connects.
- `dotnet build Screenly.sln` still 0 errors.

## Files
- `windows/Screenly.Engine/Program.cs`