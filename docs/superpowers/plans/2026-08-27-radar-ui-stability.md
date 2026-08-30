# Radar UI Stability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the Radar console responsive and crash-free under high point counts, continuous zoom, expanded editing, and parameter changes without changing Radar processing or Unity payloads.

**Architecture:** Coalesce runtime snapshots before the WPF Dispatcher, create bounded display frames with a visual-only point/trail budget, and render at a controlled cadence. Separate field-level editing feedback from full configuration validation and contain window-lifecycle races with structured diagnostics.

**Tech Stack:** .NET 8, WPF custom rendering, xUnit, Radar processing/IPC stack, Unity 2021.3 package tests

**Spec:** `docs/superpowers/specs/2026-08-27-console-ui-performance-stability-design.md`

## Global Constraints

- Every task begins with the complete current Radar WPF test suite; fix any existing failure before proceeding.
- UI coalescing and display sampling must never change `RadarSensorRuntimeSnapshot`, provider frames, IPC frames, or Unity `InteractionPoint.Fp`.
- High-load rendering has bounded pending snapshots, bounded retained display points, and bounded trail layers.
- Continuous edit validation must not deep-clone and normalize the entire Radar configuration on every keystroke.
- Closing or switching views must make late UI updates harmless.
- Every production change has a failing test first.

---

### Task 1: Capture the Radar baseline and reproduce bounded-load failures in tests

**Files:**
- Create: `tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs`
- Create: `docs/verification/radar-ui-baseline.md`

**Interfaces:**
- Produces deterministic test fixtures `RadarUiLoadFixture.CreateSnapshot(pointCount, sequence)` and `ManualRadarUiDispatcher` for later tasks.
- Records baseline render inputs, process CPU, working set, and observed failure behavior.

- [ ] **Step 1: Run the complete pre-change Radar regression suite**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
```

Expected: PASS before new tests are added.

- [ ] **Step 2: Add deterministic high-load test fixtures**

```csharp
internal static RadarSensorRuntimeSnapshot CreateSnapshot(int pointCount, long sequence)
{
    var points = Enumerable.Range(0, pointCount)
        .Select(index => new RadarPoint(
            (ushort)Math.Min(index, ushort.MaxValue),
            (ushort)(index % 360),
            MathF.Sin(index * 0.01f) * 5f,
            MathF.Cos(index * 0.01f) * 5f,
            5f))
        .ToArray();
    return new RadarSensorRuntimeSnapshot(
        "main", "sensor-1", sequence,
        DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
        points, points, Array.Empty<RadarCluster>(), Array.Empty<SensorDetection>(),
        30d, 0d, 0, 0, 0);
}
```

Add a manual Dispatcher fixture that counts queued callbacks without executing them, then can drain them deterministically on the test thread.

- [ ] **Step 3: Write passing characterization tests for the current load fixtures**

Assert the fixture creates exactly 100,000 finite source points with the requested sequence and stable first/last coordinates. Assert the manual Dispatcher preserves callback order and reports its current pending count. Desired latest-only and display-budget assertions are added as RED tests in Tasks 2 and 4.

- [ ] **Step 4: Run the new tests and capture the expected failures**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter RadarUiLoadBaselineTests`

Expected: PASS; this task only establishes deterministic fixtures and evidence.

- [ ] **Step 5: Record the live baseline**

Using Radar simulation and the existing expanded editor, record CPU and working set at idle, during 30 seconds of continuous zoom, and while entering parameters. Record input point count, number of simultaneous point views, and any unhandled exception/log entry in `docs/verification/radar-ui-baseline.md`.

- [ ] **Step 6: Commit the passing fixtures and baseline evidence**

```powershell
git add tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs `
  docs/verification/radar-ui-baseline.md
git commit -m "test(radar): capture UI high-load baseline"
```

### Task 2: Coalesce sensor snapshots before the UI Dispatcher

**Files:**
- Create: `src/Radar.Bridge.Wpf/Services/LatestUiSnapshotDispatcher.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs`

**Interfaces:**
- Produces: `internal sealed class LatestUiSnapshotDispatcher<TKey, TValue> : IDisposable where TKey : notnull`.
- Constructor: `(SynchronizationContext context, Action<TKey, TValue> apply)`.
- Methods/properties: `void Offer(TKey key, TValue value)`, `long SupersededCount`, `int PendingKeyCount`, `void Dispose()`.

- [ ] **Step 1: Run the full Radar WPF baseline**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 2: Write focused latest-per-sensor tests**

```csharp
[Fact]
public void Offer_TenThousandUpdatesForOneSensor_QueuesOneCallbackAndAppliesLatest()
{
    var context = new ManualSynchronizationContext();
    var applied = new List<long>();
    using var subject = new LatestUiSnapshotDispatcher<string, long>(
        context, (_, sequence) => applied.Add(sequence));
    for (var sequence = 1L; sequence <= 10_000; sequence++)
        subject.Offer("radar-1", sequence);
    Assert.Equal(1, context.PendingCount);
    context.Drain();
    Assert.Equal(new[] { 10_000L }, applied);
    Assert.Equal(9_999, subject.SupersededCount);
}
```

Add a two-sensor case that applies the newest value for both keys and a disposal case that applies nothing after disposal.

- [ ] **Step 3: Run and verify missing dispatcher fails compilation**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter LatestUiSnapshotDispatcher`

Expected: FAIL because the dispatcher does not exist.

- [ ] **Step 4: Implement the latest-per-key dispatcher**

Maintain one dictionary of latest values and one scheduled-context flag under a lock. `Offer` replaces the value for its key and increments `SupersededCount` when replacing. The posted callback swaps the current dictionary, applies each latest pair, and reposts only if new values arrived during application. `Dispose` clears pending values and prevents further posts.

- [ ] **Step 5: Route MainViewModel sensor snapshots through the dispatcher**

Replace the direct `SynchronizationContext.Post` in `OnSensorSnapshotUpdated` with `Offer(sensorId, snapshot)`. The apply callback looks up the current sensor and calls `ApplySnapshot` only if the sensor still exists. Dispose the coalescer with the ViewModel/coordinator lifecycle.

- [ ] **Step 6: Run the full Radar WPF suite**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS, including the 10,000-update contract.

- [ ] **Step 7: Commit UI snapshot coalescing**

```powershell
git add src/Radar.Bridge.Wpf/Services/LatestUiSnapshotDispatcher.cs `
  src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs `
  tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs
git commit -m "perf(radar): coalesce pending UI snapshots"
```

### Task 3: Reduce redundant notifications and defer full configuration validation

**Files:**
- Create: `src/Radar.Bridge.Wpf/ViewModels/DebouncedValidationScheduler.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/SensorItemViewModel.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/ScreenItemViewModel.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs`
- Create: `tests/Radar.Bridge.Wpf.Tests/RadarValidationSchedulingTests.cs`

**Interfaces:**
- Produces: `internal interface IValidationScheduler { void Schedule(Action validation); void Flush(); }`.
- Produces: `DebouncedValidationScheduler(TimeSpan delay, SynchronizationContext context)` with default delay 250 ms.
- Produces: `SensorItemViewModel.SnapshotDisplayChanged` as `event EventHandler?`, raised once after one snapshot has been applied.
- Preserves `IDataErrorInfo` field messages synchronously.

- [ ] **Step 1: Run the complete Radar WPF suite**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 2: Write failing validation-count tests**

Inject a counting full-validator into `SensorItemViewModel`. Change `RadarIp` 100 times and assert field `PropertyChanged` occurs 100 times but full validation occurs zero times before the debounce fires and once after it fires. Call the save/apply path and assert `Flush()` performs full validation immediately.

- [ ] **Step 3: Write failing notification-count tests**

Apply one snapshot and assert the Sensor ViewModel raises one dedicated `SnapshotDisplayChanged` event plus the four status property names exactly once. Assert `RadarScreenFusionView` reacts only to `SnapshotDisplayChanged`, not `FrequencyText` or error-counter notifications.

- [ ] **Step 4: Implement debounced full validation**

Keep the existing indexer checks immediate. Cache the latest full-validation result in `_hasConfigurationValidationErrors`; schedule `ConfigurationValidator.ValidateAndNormalize` after 250 ms of inactivity. Save/apply calls `Flush()` before checking command eligibility. Do not clone and normalize configuration in the `HasValidationErrors` getter.

- [ ] **Step 5: Stop broadcasting all-property and all-command refreshes**

Replace `OnPropertyChanged(string.Empty)` with the specific derived properties affected by the changed field. Notify only commands whose `CanExecute` depends on validation or selection. Add `SnapshotDisplayChanged` as the sole visualization signal for snapshot updates.

- [ ] **Step 6: Run Radar WPF and configuration suites**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 7: Commit responsive edit validation**

```powershell
git add src/Radar.Bridge.Wpf/ViewModels `
  tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarValidationSchedulingTests.cs
git commit -m "perf(radar): defer full validation during editing"
```

### Task 4: Bound retained point-cloud display work

**Files:**
- Create: `src/Radar.Bridge.Wpf/Controls/RadarDisplayBudget.cs`
- Modify: `src/Radar.Bridge.Wpf/Controls/RadarPointPersistenceBuffer.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarPointPersistenceBufferTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct RadarDisplayBudget(int MaximumPointsPerLayer, int MaximumTrailLayers)`.
- Produces: `public sealed record RadarDisplayLayer(DateTimeOffset Timestamp, IReadOnlyList<RadarPoint> Points, int SourcePointCount, double Opacity)`.
- Produces: `RadarDisplayBudget.ForViewport(double width, double height, bool isInteracting)`.
- Changes buffer read API to `IReadOnlyList<RadarDisplayLayer> GetLayers(DateTimeOffset now, RadarDisplayBudget budget)`.
- `RadarDisplayLayer` exposes sampled display points and original `SourcePointCount`.

- [ ] **Step 1: Run the complete Radar WPF suite**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 2: Write failing budget and deterministic sampling tests**

For a `1920×1080` viewport, assert normal budget is at most 12,000 points/layer and six trail layers; interaction budget is at most 4,000 points/layer and two trail layers. Feed 100,000 points and assert `SourcePointCount == 100_000`, displayed points stay within budget, first/last angular points are retained, and repeated calls return the same sampled indices.

- [ ] **Step 3: Run and verify budget tests fail**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "RadarPointPersistenceBufferTests|RadarUiLoadBaselineTests"`

Expected: FAIL because current layers return all retained points.

- [ ] **Step 4: Implement viewport and interaction budgets**

Compute normal point budget from viewport area, clamped to `[2_000, 12_000]`; interaction budget is one third, clamped to `[1_000, 4_000]`. Keep at most six normal layers and two interaction layers. Deterministic sampling uses an evenly spaced index stride and always includes the first and last point.

- [ ] **Step 5: Preserve source data and expose display diagnostics**

Never mutate or replace snapshot point collections. Store `SourcePointCount` beside the sampled display array and expose total source/display counts for diagnostics. Expired layers remain governed by the existing 220 ms persistence window.

- [ ] **Step 6: Run Radar WPF and Provider tests**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
```

Expected: PASS; provider footprint counts remain unchanged.

- [ ] **Step 7: Commit bounded display frames**

```powershell
git add src/Radar.Bridge.Wpf/Controls/RadarDisplayBudget.cs `
  src/Radar.Bridge.Wpf/Controls/RadarPointPersistenceBuffer.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarPointPersistenceBufferTests.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarUiLoadBaselineTests.cs
git commit -m "perf(radar): bound retained display points"
```

### Task 5: Render Radar views at a controlled cadence

**Files:**
- Create: `src/Radar.Bridge.Wpf/Controls/RadarRenderScheduler.cs`
- Modify: `src/Radar.Bridge.Wpf/Controls/RadarPointCloudView.cs`
- Modify: `src/Radar.Bridge.Wpf/Controls/RadarScreenFusionView.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarVisualizationLayoutTests.cs`
- Create: `tests/Radar.Bridge.Wpf.Tests/RadarRenderSchedulerTests.cs`

**Interfaces:**
- Produces: `internal sealed class RadarRenderScheduler : IDisposable` with `RequestRender()`, `BeginInteraction()`, `EndInteraction()`, `RenderedCount`, and `CoalescedRequestCount`.
- Normal cadence: maximum 30 FPS. Active zoom/drag cadence: maximum 20 FPS with interaction display budget.

- [ ] **Step 1: Run complete Radar WPF tests**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 2: Write failing render-scheduler tests**

Request 1,000 renders at one clock instant and assert only one Dispatcher render is pending. Advance 33.4 ms and assert one normal render. During interaction, advance 49 ms and assert no render, then 1 ms more and assert one render. After disposal, requests do nothing.

- [ ] **Step 3: Run and verify scheduler tests fail compilation**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter RadarRenderSchedulerTests`

Expected: FAIL because the scheduler is absent.

- [ ] **Step 4: Implement controlled invalidation**

The scheduler retains one pending request, uses a monotonic clock, and posts `InvalidateVisual` only when the active cadence permits. `RadarPointCloudView` calls `RequestRender` from snapshot, region, timer, and size changes instead of directly invalidating each time.

- [ ] **Step 5: Apply lightweight interaction mode to zoom and expanded editing**

Mouse wheel/drag and Zoom dependency-property changes call `BeginInteraction`; a 150 ms inactivity timer calls `EndInteraction` and requests one final normal-budget render. Both the embedded and expanded point views use the same policy.

- [ ] **Step 6: Limit fusion invalidations to display changes**

`RadarScreenFusionView` subscribes to `SnapshotDisplayChanged` or the Snapshot property only. It ignores frequency, CRC, validation, connection text, and command changes. Its own render requests use the same 30 FPS scheduler.

- [ ] **Step 7: Run complete Radar WPF suite**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS with bounded invalidation counts.

- [ ] **Step 8: Commit controlled rendering**

```powershell
git add src/Radar.Bridge.Wpf/Controls `
  tests/Radar.Bridge.Wpf.Tests/RadarVisualizationLayoutTests.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarRenderSchedulerTests.cs
git commit -m "perf(radar): coalesce point-cloud rendering"
```

### Task 6: Harden expanded-editor and shutdown lifecycle

**Files:**
- Modify: `src/Radar.Bridge.Wpf/MainWindow.xaml.cs`
- Modify: `src/Radar.Bridge.Wpf/RadarRegionEditorWindow.xaml.cs`
- Modify: `src/Radar.Bridge.Wpf/App.xaml.cs`
- Modify: `src/Radar.Bridge.Wpf/Logging/AsyncFileLoggerProvider.cs`
- Create: `tests/Radar.Bridge.Wpf.Tests/RadarEditorLifecycleTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RuntimeLoggingTests.cs`

**Interfaces:**
- Produces: one expanded editor per selected sensor, reused/activated while open.
- Produces structured log fields: sensor id, input points, displayed points, trail layers, rendered count, coalesced count, and exception details.

- [ ] **Step 1: Run complete Radar WPF tests**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 2: Write failing editor lifecycle tests**

Open the same sensor editor 100 times and assert only one window/view subscription exists. Close it, deliver a late snapshot and render request, and assert no exception and no pending Dispatcher work. Reopen and assert exactly one new subscription. Add a window-owner shutdown case.

- [ ] **Step 3: Write failing recoverable-render logging test**

Inject a renderer that throws once. Assert the exception is logged with sensor/display metrics, the view disables only that render, and the Bridge process-level shutdown callback is not invoked.

- [ ] **Step 4: Implement single-editor ownership and deterministic cleanup**

Track editor instances by sensor id in `MainWindow`. Repeated requests activate the existing window. On close, remove it from the map, detach all event handlers, dispose the render scheduler, stop timers, and clear snapshot references. Main window close closes owned editors before coordinator disposal.

- [ ] **Step 5: Add recoverable UI exception boundaries and structured diagnostics**

Catch display-only exceptions at the view render boundary, log them through the existing logger, clear the faulty pending display frame, and allow the next snapshot to render. Keep `DispatcherUnhandledException` as the final boundary for non-display failures and include full exception details.

- [ ] **Step 6: Run Radar WPF tests**

Run: `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 7: Commit lifecycle hardening**

```powershell
git add src/Radar.Bridge.Wpf/MainWindow.xaml.cs `
  src/Radar.Bridge.Wpf/RadarRegionEditorWindow.xaml.cs `
  src/Radar.Bridge.Wpf/App.xaml.cs `
  src/Radar.Bridge.Wpf/Logging/AsyncFileLoggerProvider.cs `
  tests/Radar.Bridge.Wpf.Tests/RadarEditorLifecycleTests.cs `
  tests/Radar.Bridge.Wpf.Tests/RuntimeLoggingTests.cs
git commit -m "fix(radar): harden expanded editor lifecycle"
```

### Task 7: Full Radar, Interaction, Unity, and live stability gate

**Files:**
- Modify: `docs/verification/radar-ui-baseline.md`
- Create: `docs/verification/console-ui-stability-final.md`

**Interfaces:**
- Verifies no Radar contract or Gate A regression.
- Records automated and live evidence for Camera and Radar UI stability.

- [ ] **Step 1: Run the complete managed test gate**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
dotnet test tests/Radar.Device.Tests/Radar.Device.Tests.csproj -c Release
dotnet test tests/Radar.Protocol.Tests/Radar.Protocol.Tests.csproj -c Release
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release
dotnet test tests/Radar.EndToEnd.Tests/Radar.EndToEnd.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Ui.Tests/Blaze.Provider.Radar.Ui.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Runtime.Tests/Blaze.Interaction.Runtime.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release
```

Expected: all pass. Stop and fix any Radar regression before continuing.

- [ ] **Step 2: Run complete Unity EditMode and PlayMode tests**

Run: `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform All -IncludeSamples`

Expected: all pass with Camera `Fp` count 21 and unchanged Radar real-point footprints.

- [ ] **Step 3: Run the Radar high-load simulation soak**

Run for 30 minutes with a simulation/replay containing at least 100,000 source points per frame. Continuously zoom for 60 seconds, open/close expanded editor 100 times, edit each parameter group, save/apply, and switch provider selection twice. Record input/display point counts, UI coalesced updates, CPU, and working set at five-minute intervals.

Acceptance: no process exit, no unhandled exception, no UI freeze longer than two seconds, no unbounded pending callback count, and no sustained working-set growth after warm-up.

- [ ] **Step 4: Run actual Radar and Camera cross-mode verification**

With Unity connected, switch Radar → Camera → Radar. Verify each mode restores its own project-local configuration. Confirm Radar Unity frames retain complete real scan `Fp`; confirm Camera frames contain center `PixelPosition` and 21 joint `Fp` values.

- [ ] **Step 5: Record final evidence and commit the gate**

Write every command, result count, Unity result, soak measurements, log path, and any resolved regression into `docs/verification/console-ui-stability-final.md`. Update the baseline with before/after measurements, then:

```powershell
git add docs/verification/radar-ui-baseline.md `
  docs/verification/console-ui-stability-final.md
git commit -m "test: verify console UI stability gate"
```
