# CameraVision Provider Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert real or fake hand detections into calibrated, smoothed, stable multi-hand `InteractionPoint` frames without changing Camera Capture, IPC, Unity Dispatcher, or Radar.

**Architecture:** Pure tested components calculate tracking points, assign lightweight IDs, map camera coordinates through a four-point homography, and smooth per-track positions. A latest-frame inference service composes those components and `CameraVisionProvider` publishes standard frames plus explicit cancel points.

**Tech Stack:** .NET 8, C# 12, OpenCvSharp 4.13, xUnit, Provider API 1, Interaction IPC 1.

**Spec:** `docs/superpowers/specs/2026-08-24-camera-vision-hand-mvp-0-1-design.md`

## Global Constraints

- Requires the successful native-backend plan checkpoint.
- No inference queue; process only `LatestFrameSlot`'s current frame.
- Do not modify the Camera Capture ownership policy.
- No handedness or person identity.
- `Fp` remains empty for CameraVision Hand MVP 0.1.
- One `InteractionPoint` per active lightweight hand track.
- Radar 447/447 must pass at each integration checkpoint.

---

### Task 1: Tracking-point calculations

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/HandTrackingPoint.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/TrackingPointCalculator.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/TrackingPointCalculatorTests.cs`

**Interfaces:**
- Consumes: `DetectedHand` from the native-backend plan.
- Produces: `HandTrackingPoint.IndexTip`, `HandTrackingPoint.PalmCenter`, and `TrackingPointCalculator.Calculate(DetectedHand, HandTrackingPoint)`.

- [ ] **Step 1: Write failing Index Tip, Palm Center, and invalid input tests**

```csharp
[Fact]
public void PalmCenter_AveragesLandmarksZeroFiveNineThirteenSeventeen()
{
    var hand = HandWithSelectedPoints((0, 0f, 0f), (5, .2f, .2f),
        (9, .4f, .4f), (13, .6f, .6f), (17, .8f, .8f));
    Assert.Equal(new CameraPoint(.4f, .4f),
        TrackingPointCalculator.Calculate(hand, HandTrackingPoint.PalmCenter));
}
```

Also assert Index Tip reads landmark 8 and an undefined enum throws.

- [ ] **Step 2: Run the focused tests and observe compile failure**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter TrackingPointCalculatorTests --nologo
```

- [ ] **Step 3: Implement the direct calculations with no filtering side effects**

Do not smooth or clamp in this class. Return the raw normalized camera XY from validated landmarks.

- [ ] **Step 4: Run the complete CameraVision test project**

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Hand tests/Blaze.Provider.CameraVision.Tests/TrackingPointCalculatorTests.cs
git commit -m "feat(camera): calculate hand tracking points"
```

### Task 2: Four-point surface homography

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Mapping/CameraCalibration.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Mapping/HomographySurfaceMapper.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/HomographySurfaceMapperTests.cs`

**Interfaces:**
- Produces: `CameraCalibration(P1, P2, P3, P4)` and `HomographySurfaceMapper.TryMap(Vector2Data cameraPixel, out Vector2Data surfaceNormalized)`.

- [ ] **Step 1: Write failing corner, center, outside, and degenerate tests**

Use a 100-by-100 camera square. Assert P1 `(0,0)`, P2 `(1,0)`, P3 `(1,1)`, P4 `(0,1)`, center `(.5,.5)`, a point outside returns `false`, duplicate/collinear calibration fails validation, and a finite mapped landmark may remain outside when using the non-gating landmark method.

- [ ] **Step 2: Run the focused tests and verify compile failure**

- [ ] **Step 3: Implement with OpenCV `GetPerspectiveTransform`/`PerspectiveTransform`**

Store and dispose the homography `Mat`. Separate `TryMapTrackingPoint`, which enforces the calibrated polygon and unit interval, from `MapLandmark`, which requires finiteness but permits out-of-range XY.

- [ ] **Step 4: Run Camera tests and the Radar homography tests**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter HomographySurfaceMapperTests --nologo
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release --filter HomographyCalibrationTests --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Mapping tests/Blaze.Provider.CameraVision.Tests/HomographySurfaceMapperTests.cs
git commit -m "feat(camera): map hands with four point calibration"
```

### Task 3: Per-track EMA smoothing

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Tracking/EmaPositionFilter.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/EmaPositionFilterTests.cs`

**Interfaces:**
- Produces: `EmaPositionFilter.Update(long trackId, Vector2Data current)`, `Remove(long trackId)`, and `Reset()`.

- [ ] **Step 1: Write failing tests**

Assert the first sample is unchanged; factor `.35` evaluates `previous + delta * .35`; tracks are independent; remove/re-entry resets history; zero and one factors behave exactly; non-finite/out-of-range factors are rejected.

- [ ] **Step 2: Run focused tests and observe compile failure**

- [ ] **Step 3: Implement minimal dictionary-backed EMA state**

No timers, prediction, Kalman, or One Euro code.

- [ ] **Step 4: Run all Camera tests and commit**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
git add providers/CameraVision/Blaze.Provider.CameraVision/Tracking tests/Blaze.Provider.CameraVision.Tests/EmaPositionFilterTests.cs
git commit -m "feat(camera): smooth hand tracks with EMA"
```

### Task 4: Lightweight multi-hand track assignment

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Tracking/HandTrackAssigner.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Tracking/HandTrackModels.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/HandTrackAssignerTests.cs`

**Interfaces:**
- Produces: `HandTrackAssignment Update(IReadOnlyList<HandCandidate>)` containing `Active`, `RemovedTrackIds`, and immutable snapshots.

- [ ] **Step 1: Write failing behavior tests**

Cover eight first-frame hands get eight unique positive IDs; ordinary movement preserves IDs even when backend order reverses; ninth hand is not truncated; unmatched detections create monotonic IDs; a missing hand survives the configured tolerance; the next miss removes it once; re-entry gets a new ID; crossing may swap but never duplicates an ID.

- [ ] **Step 2: Run focused tests and verify compile failure**

- [ ] **Step 3: Implement deterministic nearest-neighbor matching**

Sort candidate track/detection pairs by squared distance, then track ID, then detection index. Greedily accept pairs within `MaximumMatchDistance`. Do not add handedness, person identity, prediction, or an artificial hand count cap.

- [ ] **Step 4: Run focused tests repeatedly to prove determinism**

```powershell
1..20 | ForEach-Object { dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter HandTrackAssignerTests --nologo --no-restore; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
```

- [ ] **Step 5: Run Camera tests and commit**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
git add providers/CameraVision/Blaze.Provider.CameraVision/Tracking tests/Blaze.Provider.CameraVision.Tests/HandTrackAssignerTests.cs
git commit -m "feat(camera): assign lightweight multi hand IDs"
```

### Task 5: CameraVision configuration and project-scoped store

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Configuration/CameraVisionConfiguration.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Configuration/CameraVisionConfigurationStore.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/profiles/camera-vision-default.json`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionConfigurationTests.cs`

**Interfaces:**
- Produces: schema version 1 configuration and `LoadOrCreateAsync`, `SaveAsync`, `ResetAsync` using `IProviderStorageContext.GetProviderDataDirectory("blaze.camera.vision")`.

- [ ] **Step 1: Write failing defaults, validation, isolation, and corruption tests**

Assert defaults: 1280x720 at 30 FPS, MaxHands 8, IndexTip, confidence values in range, EMA .35, positive lost tolerance. Assert MaxHands 32 is valid. Assert two data roots resolve to different files. Assert first load creates `camera-vision.json`. Assert malformed JSON is preserved and reported rather than overwritten.

- [ ] **Step 2: Run and observe compile failure**

- [ ] **Step 3: Implement immutable configuration and atomic writes**

Write UTF-8 JSON to a sibling temporary file, flush, then `File.Move(..., overwrite: true)`. Store calibration by `SurfaceId`. Do not use the bundled profile as writable storage.

- [ ] **Step 4: Run Camera configuration tests and existing Radar persistence tests**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter CameraVisionConfigurationTests --nologo
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release --filter Configuration --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Configuration providers/CameraVision/Blaze.Provider.CameraVision/profiles tests/Blaze.Provider.CameraVision.Tests/CameraVisionConfigurationTests.cs
git commit -m "feat(camera): persist project scoped camera settings"
```

### Task 6: Compose the latest-frame hand inference service

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/CameraHandProcessingService.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/CameraHandSnapshots.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraHandProcessingServiceTests.cs`

**Interfaces:**
- Consumes: `CameraCaptureService.LatestFrames`, `IHandDetectionBackend`, mapping, tracking, and EMA components.
- Produces: immutable `CameraHandFrame` snapshots with active hands, removed IDs, preview overlay data, timing, and counters.

- [ ] **Step 1: Write failing orchestration tests with `FakeHandDetectionBackend`**

Cover BGR-to-RGB channel order; source buffer remains valid until detect returns; old frames are dropped without backlog; eight hands survive; confidence filtering; invalid hands are rejected; outside tracking points emit no active hand; all 21 landmarks map; backend latency does not block the calling/UI thread; cancellation disposes frames/backend exactly once.

- [ ] **Step 2: Run focused tests and observe compile failure**

- [ ] **Step 3: Implement one inference loop with explicit ownership**

Use one long-running async task. Take the latest frame, convert with `Cv2.CvtColor(..., ColorConversionCodes.BGR2RGB)`, pin only during `DetectAsync`, produce immutable managed snapshots, and dispose every `Mat` in `finally`.

- [ ] **Step 4: Run leak/restart tests fifty times**

Add a test loop that starts/stops the service 50 times with a fake backend and asserts every created frame and backend is disposed.

- [ ] **Step 5: Run Camera tests and commit**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
git add providers/CameraVision/Blaze.Provider.CameraVision/Hand tests/Blaze.Provider.CameraVision.Tests/CameraHandProcessingServiceTests.cs
git commit -m "feat(camera): process latest frames for hand detection"
```

### Task 7: Publish standard hand InteractionFrames

**Files:**
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionPlugin.cs`
- Replace tests in: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionProviderTests.cs`
- Remove after replacements pass: `providers/CameraVision/Blaze.Provider.CameraVision/Detection/FakeVisualDetector.cs`
- Remove after replacements pass: `providers/CameraVision/Blaze.Provider.CameraVision/Detection/VisualDetection.cs`
- Remove after replacements pass: `tests/Blaze.Provider.CameraVision.Tests/FakeVisualDetectorTests.cs`

**Interfaces:**
- Consumes: `CameraHandProcessingService`.
- Produces: provider-standard active Hover points and one-time Cancel points; raw `Fp` is empty; `Extensions["hand"]` is schema version 1.

- [ ] **Step 1: Replace fake-point expectations with failing hand-frame tests**

Assert eight hands create eight points, IDs remain stable on reordering, `PixelPosition` matches logical surface size, extension contains 21 ordered landmarks with pixel/normalized/Z, no handedness property exists, `Fp` is empty, loss emits one Cancel, camera/backend unavailable emits no fake point, and restart works 50 times.

- [ ] **Step 2: Run provider tests and verify failures against the Gate B0 fake detector**

- [ ] **Step 3: Refactor provider construction and lifecycle**

Load scoped configuration through `IProviderStorageContext`; resolve injected `IHandDetectionBackend` for tests or create `MediaPipeHandBackend` in production; start capture then processing; stop processing before capture; propagate actionable Faulted status; keep event observer isolation.

- [ ] **Step 4: Build the exact hand extension**

Use `JsonSerializer.SerializeToElement` and `InteractionExtensions`. The point's tracking position is EMA-smoothed; extension landmark positions are homography-mapped but not EMA-smoothed. Do not add a Camera phase or Provider-specific IPC message.

- [ ] **Step 5: Remove production fake detection only after replacement tests pass**

Run:

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
```

Expected: all tests PASS and no production code references `FakeVisualDetector`.

- [ ] **Step 6: Run Camera loader, full solution, and Radar hard gate**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all new Camera tests PASS; full suite grows beyond 752 with zero failures; Radar remains 447/447.

- [ ] **Step 7: Commit provider integration**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision tests/Blaze.Provider.CameraVision.Tests
git commit -m "feat(camera): publish multi hand interaction frames"
```

### Task 8: Provider-core checkpoint

**Files:** None unless tests expose a defect.

- [ ] **Step 1: Confirm no provider-specific change entered IPC or Unity**

```powershell
git diff c0bee25 -- src/Blaze.Interaction.Ipc UnityPackage/com.blaze.interaction/Runtime/InteractionFrameDispatcher.cs
```

Expected: no Camera-specific branching.

- [ ] **Step 2: Run complete required regression commands**

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

- [ ] **Step 3: Confirm clean checkpoint before Bridge UI work**

```powershell
git status --short
```

Expected: clean. Then begin `2026-08-24-camera-vision-bridge-ui-plan.md`.
