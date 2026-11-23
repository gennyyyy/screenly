# Multi-Display Pool Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single shared VDD display with a bounded `DisplayPool` of per-client leases, so multiple Android devices get independent displays without VDD churn.

**Architecture:** A `DisplayPool` owns an `IDisplayDriver` (abstracted Parsec VDD) and enforces a capacity (`MaxDisplays`). Leases are created on pool growth, assigned to sessions, and removed only after a grace period when the last client disconnects. Removal is serialized on a dedicated thread with a timeout. A kill switch (`PerClientDisplays`) preserves the legacy path for Phase 1.

**Tech Stack:** C# / .NET 8, xUnit, existing Parsec VDD driver, `TimeProvider` for testability.

**Spec:** `docs/plan/multi-display-pool.md` (rev 3)

## Global Constraints

- .NET 8 / C# (existing stack). No new dependencies.
- `dotnet test` must pass after every task.
- Single-instance mutex and DPI awareness unchanged.
- USB-only, loopback-only (no LAN/mDNS).
- `PerClientDisplays` kill switch defaults true; legacy path stays until Phase 4.
- No parking mode in this plan (deferred).
- Dead-peer timeout 15 s (3x ping interval).

## Review Focus

- Grace-vs-reconnect race: a reconnect during grace must cancel removal; verified by fake-driver test.
- Capacity race: two concurrent Acquires at MaxDisplays=1 must not both ADD.
- Stale identity after removal: surviving captures must re-resolve device names.
- Replace-in-place at MaxDisplays=1: resolution change must still work (one REMOVE + one ADD).
- Quarantine at MaxDisplays=1: clients get POOL_DEGRADED, not a hang.
- Old-client compat: HELLO without deviceId still works (best-effort reuse).

---

## File Structure

### New files
- `windows/Screenly.Core/Display/IDisplayDriver.cs` — raw driver abstraction (Add, Remove, EnumerateAttached, SetMode, ReadMode, ResolveIdentity, Ping)
- `windows/Screenly.Core/Display/DisplayPool.cs` — pool state machine + lease management
- `windows/Screenly.Tests/DisplayPoolTests.cs` — unit tests with fake driver + fake clock
- `windows/Screenly.Tests/FakeDisplayDriver.cs` — test double

### Modified files
- `windows/Screenly.Core/Display/DisplayTypes.cs` — add `DisplayLease` record, `PoolState` enum, error codes
- `windows/Screenly.Core/Display/DisplayManager.cs` (ParsecVddDisplay) — implement `IDisplayDriver`
- `windows/Screenly.Core/Config/AppConfig.cs` — add `MaxDisplays`, `RemoveGraceSeconds`, `PerClientDisplays`
- `windows/Screenly.Engine/Program.cs` — replace shared-display logic with pool acquisition

---

## Task 1: IDisplayDriver interface and ParsecVddDisplay adapter

**Files:**
- Create: `windows/Screenly.Core/Display/IDisplayDriver.cs`
- Modify: `windows/Screenly.Core/Display/DisplayTypes.cs`
- Modify: `windows/Screenly.Core/Display/DisplayManager.cs`

**Interfaces:**
- Produces: `IDisplayDriver` with methods `Task<DisplayHandle> AddAsync(int width, int height, int refreshHz, CancellationToken ct)`, `Task RemoveAsync(DisplayHandle handle, CancellationToken ct)`, `IEnumerable<AttachedDisplay> EnumerateAttached()`, `Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct)`, `DisplayMode? ReadMode(string deviceName)`, `string? ResolveIdentity(DisplayHandle handle)`, `Task PingAsync(CancellationToken ct)`

- [ ] **Step 1: Define `IDisplayDriver` and supporting types in `DisplayTypes.cs`**

Add `AttachedDisplay` (Handle, DeviceName, Identity, Width, Height, RefreshHz) and `DisplayMode` (Width, Height, RefreshHz) records. Add `IDisplayDriver` interface.

- [ ] **Step 2: Implement `IDisplayDriver` on `ParsecVddDisplay`**

Wrap existing `CreateAsync`/`RemoveAsync`/`PingAsync` behind the interface. `EnumerateAttached` returns current `_displays` mapped to `AttachedDisplay`. `SetModeAsync` delegates to a new method that calls `ChangeDisplaySettingsEx` (currently not implemented — returns Task.CompletedTask for now; Phase 3 fills it). `ReadMode` calls `EnumDisplaySettings` via DisplayHelper. `ResolveIdentity` returns the device name (Phase 0 upgrades to stable identity).

- [ ] **Step 3: Build**

Run: `dotnet build windows/Screenly.sln`
Expected: BUILD SUCCESSFUL

- [ ] **Step 4: Commit**

```bash
git add windows/Screenly.Core/Display/
git commit -m "feat: add IDisplayDriver abstraction over ParsecVddDisplay"
```

---

## Task 2: Config additions

**Files:**
- Modify: `windows/Screenly.Core/Config/AppConfig.cs`

**Interfaces:**
- Produces: `VirtualDisplayConfig` gains `MaxDisplays` (int, default 4, clamp 1..8), `RemoveGraceSeconds` (int, default 5, clamp 1..300), `PerClientDisplays` (bool, default true)

- [ ] **Step 1: Add fields to `VirtualDisplayConfig`**

Add `MaxDisplays`, `RemoveGraceSeconds`, `PerClientDisplays` with defaults and clamping in `ConfigStore.Load()`.

- [ ] **Step 2: Build + test**

Run: `dotnet build windows/Screenly.sln && dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: BUILD SUCCESSFUL, all tests pass

- [ ] **Step 3: Commit**

```bash
git add windows/Screenly.Core/Config/
git commit -m "feat: add MaxDisplays, RemoveGraceSeconds, PerClientDisplays config"
```

---

## Task 3: DisplayPool and DisplayLease core

**Files:**
- Create: `windows/Screenly.Core/Display/DisplayPool.cs`
- Create: `windows/Screenly.Tests/FakeDisplayDriver.cs`
- Create: `windows/Screenly.Tests/DisplayPoolTests.cs`

**Interfaces:**
- Consumes: `IDisplayDriver`, `TimeProvider`
- Produces: `DisplayPool` with `Acquire(width, height, hz, deviceId)`, `Release(lease)`, `RemoveGraceSeconds`, counters. `DisplayLease` record with `Id`, `Handle`, `Identity`, `DeviceName`, `Width`, `Height`, `Hz`, `Orientation`, `State`, `SessionId`, `ClientId`, `RemoveDueAt`.

- [ ] **Step 1: Write failing tests for grace-vs-reconnect race**

```csharp
[Fact]
public async Task ReconnectDuringGrace_CancelsRemoval()
{
    var driver = new FakeDisplayDriver();
    var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
    var pool = new DisplayPool(driver, clock, maxDisplays: 1, graceSeconds: 5);
    var lease = await pool.AcquireAsync(800, 1280, 60, "dev1");
    pool.Release(lease);
    clock.Advance(TimeSpan.FromSeconds(2));
    var lease2 = await pool.AcquireAsync(800, 1280, 60, "dev1");
    Assert.Same(lease, lease2);
    Assert.Equal(LeaseState.Assigned, lease2.State);
    Assert.Equal(0, driver.RemoveCount);
}
```

- [ ] **Step 2: Run test — verify it fails**

Run: `dotnet test windows/Screenly.Tests/Screenly.Tests.csproj --filter ReconnectDuringGrace`
Expected: FAIL (DisplayPool not defined)

- [ ] **Step 3: Implement `DisplayPool` with Acquire/Release and grace timer**

Implement state machine: Free → Assigned → PendingRemove. Grace timer via `TimeProvider`. Teardown worker removes after grace. Reconnect cancels. Counters: Created, Reused, RemovalScheduled, RemovalCanceled, Removed, RemoveFailures.

- [ ] **Step 4: Implement remaining Acquire steps**

Reconnect affinity (step 1), exact mode (step 2), grow (step 4), replace-in-place (step 5), NO_DISPLAY rejection (step 6). Capacity reservation before rate-limit wait. POOL_DEGRADED when quarantine consumes all capacity.

- [ ] **Step 5: Add capacity-race test**

```csharp
[Fact]
public async Task TwoConcurrentAcquires_AtMaxDisplays1_OnlyOneAdds()
{
    var driver = new FakeDisplayDriver { RemoveDelay = TimeSpan.FromSeconds(0) };
    var pool = new DisplayPool(driver, TimeProvider.System, maxDisplays: 1, graceSeconds: 5);
    var t1 = pool.AcquireAsync(800, 1280, 60, "dev1");
    var t2 = pool.AcquireAsync(800, 1280, 60, "dev2");
    await Task.WhenAll(t1, t2);
    Assert.Equal(1, driver.AddCount);
}
```

- [ ] **Step 6: Add randomized soak test**

Random connect/disconnect sequence with logged seed; assert ADD only on growth, REMOVE only after grace, no REMOVE on reconnect.

- [ ] **Step 7: Run all tests**

Run: `dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: all pass

- [ ] **Step 8: Commit**

```bash
git add windows/Screenly.Core/Display/DisplayPool.cs windows/Screenly.Tests/
git commit -m "feat: DisplayPool core with grace, reconnect, capacity, quarantine"
```

---

## Task 4: Replace shared display with pool in Program.cs (Phase 1)

**Files:**
- Modify: `windows/Screenly.Engine/Program.cs`

**Interfaces:**
- Consumes: `DisplayPool`, `IDisplayDriver`
- Replaces: `sharedDm`, `sharedHandle`, `sharedDeviceName`, `sharedW`, `sharedH`

- [ ] **Step 1: Add `PerClientDisplays` branch in HELLO handler**

If `cfg.PerClientDisplays` is false, use legacy shared path. Otherwise, use pool acquisition.

- [ ] **Step 2: Replace `CreateDisplayForState` with pool-based acquisition**

Acquire lease, store in `SessionState.Lease`, build capture with lease's device name.

- [ ] **Step 3: Update disconnect handler**

Release lease (schedule removal), dispose capture/encoder/sender immediately.

- [ ] **Step 4: Update CONFIG handler**

Resolution change: try replace-in-place (Phase 1), mode change added in Phase 3.

- [ ] **Step 5: Update frame loop**

Each session captures from its own lease's device name.

- [ ] **Step 6: Update cursor loop**

Already iterates sessions; verify visible-only-to-containing-display.

- [ ] **Step 7: Build + test**

Run: `dotnet build windows/Screenly.sln && dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: BUILD SUCCESSFUL, all tests pass

- [ ] **Step 8: Commit**

```bash
git add windows/Screenly.Engine/Program.cs
git commit -m "feat: replace shared display with DisplayPool (Phase 1)"
```

---

## Task 5: Phase 0 — Prove identity and topology (manual, separate commit)

**Files:**
- Modify: `windows/Screenly.Core/Capture/GdiCapture.cs` (logging only)
- Modify: `windows/Screenly.Core/Capture/DxgiCapture.cs` (logging only)

This task is manual verification on real hardware. No automated tests.

- [ ] **Step 1: Add diagnostic logging**

Log every Parsec device name/index/W/H/orientation, monitor device path, adapter LUID.

- [ ] **Step 2: Snapshot-diff helper**

Implement `EnumerateParsecDevices()` returning sorted list of device names. Diff before/after ADD.

- [ ] **Step 3: Manual verification procedure**

Follow Phase 0 steps in spec: two VDDs, identity stability, mode change (known + novel), topology auto-extend, capture during removal, ping during removal, REMOVE hang timing.

- [ ] **Step 4: Write findings note**

Document results in `docs/plan/phase0-findings.md`. Gate: findings note must exist before Phase 1 code is written.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: Phase 0 diagnostics and findings (no behavior change)"
```

---

## Task 6: Phase 2 — Multi-client admission

**Files:**
- Modify: `windows/Screenly.Engine/Program.cs`
- Modify: `windows/Screenly.Core/Display/DisplayPool.cs`

- [ ] **Step 1: Raise MaxDisplays default to 4**

Update config default.

- [ ] **Step 2: Remove shared-state usage from pool path**

Ensure all session state references lease, not shared fields.

- [ ] **Step 3: Add POOL_DEGRADED handling**

When quarantine consumes all capacity, return POOL_DEGRADED. Android surfaces restart message.

- [ ] **Step 4: Update WriteStatusFile for displays[]**

Add pool snapshot parameter, serialize displays[], quarantined, counters.

- [ ] **Step 5: Android: surface ERROR messages**

Update ScreenlyClient to handle NO_DISPLAY (retry) and POOL_DEGRADED (restart message).

- [ ] **Step 6: Build + test**

Run: `dotnet build windows/Screenly.sln && dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: BUILD SUCCESSFUL, all tests pass

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: multi-client admission (Phase 2)"
```

---

## Task 7: Phase 3 — Resolution, orientation, cursor

**Files:**
- Modify: `windows/Screenly.Core/Display/DisplayManager.cs` (SetModeAsync)
- Modify: `windows/Screenly.Engine/Program.cs`

- [ ] **Step 1: Implement SetModeAsync with ChangeDisplaySettingsEx**

Mode change on existing lease: write registry preset, call ChangeDisplaySettingsEx, verify readback.

- [ ] **Step 2: Add mode-change path to Acquire (step 3)**

Wire up the mode-change lease path in DisplayPool.

- [ ] **Step 3: Per-lease orientation in CONFIG/HELLO_ACK**

Already per-capture; verify each session gets its own orientation.

- [ ] **Step 4: Cursor visible-only-to-containing-display**

Verify cursor loop sends visible only to the display containing cursor, hidden to others.

- [ ] **Step 5: Build + test**

Run: `dotnet build windows/Screenly.sln && dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: BUILD SUCCESSFUL, all tests pass

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: per-lease resolution, orientation, cursor (Phase 3)"
```

---

## Task 8: Phase 4 — Capacity, soak, cleanup

**Files:**
- Modify: `windows/Screenly.Engine/Program.cs` (remove kill switch + legacy path)
- Modify: `windows/Screenly.Core/Config/AppConfig.cs`

- [ ] **Step 1: Measure 1/2/3/4 clients at 800x1280 H.264**

Record capture/encode/send/FPS. Set tested MaxDisplays default.

- [ ] **Step 2: Decide RemoveGraceSeconds default**

From ReconnectedWithin60sAfterRemove counter over real use.

- [ ] **Step 3: Remove PerClientDisplays and legacy shared path**

Delete the kill switch and shared fields (sharedDm, sharedHandle, etc.).

- [ ] **Step 4: Manual 100-cycle soak on real hardware**

No new tooling. Verify no ghost accumulation.

- [ ] **Step 5: Build + test**

Run: `dotnet build windows/Screenly.sln && dotnet test windows/Screenly.Tests/Screenly.Tests.csproj`
Expected: BUILD SUCCESSFUL, all tests pass

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: capacity, soak, cleanup (Phase 4)"
```
