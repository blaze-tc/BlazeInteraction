# Camera Control Workspace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a responsive CameraVision control console with three synchronized aspect-correct previews, capability-driven mode dropdowns, and unchanged Unity `InteractionPoint` consumption.

**Architecture:** Keep capture, inference, and IPC at their configured rates while a latest-only UI scheduler renders at no more than 15 FPS. Build immutable preview models from the same timestamped Camera status, render them with reusable WPF surfaces, and map one hand to one generic `InteractionPoint` whose `Fp` contains the 21 output-space landmarks.

**Tech Stack:** .NET 8, WPF custom FrameworkElement rendering, WriteableBitmap, OpenCvSharp, xUnit, Unity 2021.3 NUnit tests

**Spec:** `docs/superpowers/specs/2026-08-27-console-ui-performance-stability-design.md`

## Global Constraints

- Preserve existing public Unity event and `InteractionPoint` APIs.
- `PixelPosition` is the mapped hand center; `Fp` contains exactly 21 mapped landmark points ordered by landmark index.
- Do not draw skeleton connection lines in the Unity output preview.
- Raw, calibrated-region, and Unity previews are derived from one status timestamp.
- Raw camera pixels use aspect-fit with black letterbox space; clicks in letterbox space are rejected.
- UI preview defaults to 15 FPS and may drop stale UI frames; capture, inference, and IPC frames are not throttled by the UI.
- Run `Blaze.Provider.CameraVision.Tests` after every task and run the Radar WPF suite after XAML/theme integration changes.

---

### Task 1: Put all Camera landmarks into the generic footprint

**Files:**
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionProviderTests.cs`
- Modify: `tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs`
- Modify: `UnityPackage/com.blaze.interaction/Tests/Runtime/ProtocolAndBufferTests.cs`

**Interfaces:**
- Produces: `InteractionPoint.Fp` as `IReadOnlyList<Vector2Data>` containing landmark indices 0 through 20 in Unity logical coordinates.
- Preserves: hand extension schema version 1 and its 21 detailed landmark records.

- [ ] **Step 1: Change the Camera provider test to require `Fp` landmarks**

```csharp
foreach (var point in frame.Points)
{
    Assert.Equal(21, point.Fp.Count);
    var hand = point.Extensions!["hand"];
    var landmarks = hand.GetProperty("landmarks").EnumerateArray().ToArray();
    Assert.Equal(21, landmarks.Length);
    for (var index = 0; index < landmarks.Length; index++)
    {
        Assert.Equal(landmarks[index].GetProperty("pixelPosition").GetProperty("x").GetSingle(),
            point.Fp[index].X, 3);
        Assert.Equal(landmarks[index].GetProperty("pixelPosition").GetProperty("y").GetSingle(),
            point.Fp[index].Y, 3);
    }
}
```

- [ ] **Step 2: Run the focused test and verify it fails on the empty footprint**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter RealHandPipeline_PublishesEightStandardPointsWithTwentyOneLandmarks`

Expected: FAIL because `Fp` is empty.

- [ ] **Step 3: Map the ordered landmarks once and reuse them**

In `CreateActivePoint`, build this array before serializing the extension:

```csharp
var mappedLandmarks = hand.Landmarks
    .OrderBy(landmark => landmark.Index)
    .Select(landmark => new Vector2Data(
        landmark.NormalizedPosition.X * surface.LogicalWidth,
        landmark.NormalizedPosition.Y * surface.LogicalHeight))
    .ToArray();
```

Use `mappedLandmarks[index]` for the extension's `pixelPosition`, and assign:

```csharp
Fp = Array.AsReadOnly(mappedLandmarks),
```

- [ ] **Step 4: Add IPC round-trip coverage for 21 Camera footprint points**

Create an interaction frame with provider id `blaze.camera.vision`, one center point, and 21 footprint positions. Assert the codec preserves order, values, and point count. Mirror the same payload in `ProtocolAndBufferTests.cs` and assert Unity deserialization preserves `PixelPosition` and all 21 `Fp` values.

- [ ] **Step 5: Run provider, IPC, and Unity contract tests**

Run:

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release
```

Run: `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode`

Expected: all pass; existing Radar footprint tests remain unchanged.

- [ ] **Step 6: Commit the generic Camera footprint**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionProviderTests.cs `
  tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs `
  UnityPackage/com.blaze.interaction/Tests/Runtime/ProtocolAndBufferTests.cs
git commit -m "feat(camera): publish landmarks through interaction footprint"
```

### Task 2: Enumerate and select actual Camera capture modes

**Files:**
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Camera/CameraCaptureContracts.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Camera/OpenCvCameraCaptureBackend.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Camera/CameraCapabilityEnumerator.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/ICameraVisionControl.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraCapabilityEnumeratorTests.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraCaptureTests.cs`

**Interfaces:**
- Produces: `public sealed record CameraCaptureMode(int Width, int Height, double FramesPerSecond)`.
- Produces: `public sealed record CameraDeviceCapabilities(CameraDeviceDescriptor Device, IReadOnlyList<CameraCaptureMode> Modes, bool UsesFallbackPresets, string? Warning)`.
- Adds to `ICameraCaptureBackend`: `bool TryGetActiveMode(out CameraCaptureMode? mode)`.
- Adds to `ICameraVisionControl`: `Task<CameraDeviceCapabilities> GetCapabilitiesAsync(int deviceIndex, CancellationToken cancellationToken)`.

- [ ] **Step 1: Write failing capability-probing tests**

```csharp
[Fact]
public async Task EnumerateAsync_ReturnsOnlyVerifiedUniqueModesSortedByResolutionAndRate()
{
    var backend = new ProbeBackend(
        accepted: [new(640, 480, 30), new(1280, 720, 30), new(1280, 720, 60)]);
    var subject = new CameraCapabilityEnumerator(() => backend,
        candidates: [new(1280, 720, 60), new(640, 480, 30), new(1280, 720, 30)]);

    var result = await subject.EnumerateAsync(
        new CameraDeviceDescriptor(0, "Camera 0"), CancellationToken.None);

    Assert.False(result.UsesFallbackPresets);
    Assert.Equal(new CameraCaptureMode[]
    {
        new(640, 480, 30), new(1280, 720, 30), new(1280, 720, 60)
    }, result.Modes);
}

[Fact]
public async Task EnumerateAsync_WhenNoModeCanBeVerified_ReturnsFallbackWithWarning()
{
    var subject = new CameraCapabilityEnumerator(() => new UnverifiableBackend());
    var result = await subject.EnumerateAsync(
        new CameraDeviceDescriptor(2, "Camera 2"), CancellationToken.None);
    Assert.True(result.UsesFallbackPresets);
    Assert.NotEmpty(result.Modes);
    Assert.False(string.IsNullOrWhiteSpace(result.Warning));
}
```

- [ ] **Step 2: Run and verify missing types fail compilation**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter CameraCapabilityEnumeratorTests`

Expected: FAIL because the capability types do not exist.

- [ ] **Step 3: Implement active mode reporting in OpenCV backend**

After a successful open and property setup, return actual backend values:

```csharp
public bool TryGetActiveMode(out CameraCaptureMode? mode)
{
    var capture = _capture;
    if (capture is null || !capture.IsOpened()) { mode = null; return false; }
    var width = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameWidth));
    var height = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameHeight));
    var fps = capture.Get(VideoCaptureProperties.Fps);
    if (width <= 0 || height <= 0 || !double.IsFinite(fps) || fps <= 0)
    { mode = null; return false; }
    mode = new CameraCaptureMode(width, height, fps);
    return true;
}
```

- [ ] **Step 4: Implement capability probing and compatibility fallback**

Use candidates `640×480`, `1280×720`, `1920×1080`, and `3840×2160` at `15`, `24`, `30`, and `60` FPS. Open each candidate, read the actual mode, accept only modes whose width/height match and whose FPS differs by no more than `1.0`, de-duplicate actual modes, then sort by pixel count, width, height, and FPS. If none can be verified, return common presets with `UsesFallbackPresets = true` and warning `"设备无法报告完整能力，正在使用兼容预设。"`.

- [ ] **Step 5: Expose the capability service through the provider control**

`CameraVisionProvider.GetCapabilitiesAsync` resolves the current device descriptor, delegates to `CameraCapabilityEnumerator`, and never mutates the active capture while enumeration is running. Serialize calls with a private `SemaphoreSlim` so refresh/device selection cannot open multiple probes concurrently.

- [ ] **Step 6: Run the complete Camera test project**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 7: Commit capability enumeration**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Camera `
  providers/CameraVision/Blaze.Provider.CameraVision/UI/ICameraVisionControl.cs `
  providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraCapabilityEnumeratorTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraCaptureTests.cs
git commit -m "feat(camera): enumerate selectable capture modes"
```

### Task 3: Persist per-device Camera selections and drive linked dropdowns

**Files:**
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Configuration/CameraVisionConfiguration.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Configuration/CameraVisionConfigurationStore.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewModel.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionConfigurationTests.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewModelTests.cs`

**Interfaces:**
- Produces: `internal sealed record CameraDeviceProfile(CameraCaptureMode Mode, bool MirrorX, CameraRotation Rotation)`.
- Adds `IReadOnlyDictionary<int, CameraDeviceProfile> DeviceProfiles` to configuration schema version 2.
- Produces VM properties `IReadOnlyList<CameraResolutionOption> Resolutions`, `CameraResolutionOption? SelectedResolution`, `IReadOnlyList<double> FrameRates`, `double SelectedFrameRate`, `int ActualWidth`, `int ActualHeight`, and `string? CapabilityWarning`.

- [ ] **Step 1: Write failing migration and profile persistence tests**

```csharp
[Fact]
public async Task LoadAsync_MigratesSchema1CaptureIntoSelectedDeviceProfile()
{
    await File.WriteAllTextAsync(store.ConfigurationPath, Schema1Json(2, 1280, 720, 30));
    var loaded = await store.LoadAsync(CancellationToken.None);
    var profile = loaded.DeviceProfiles[2];
    Assert.Equal(new CameraCaptureMode(1280, 720, 30), profile.Mode);
}

[Fact]
public async Task SaveAndLoad_PreservesIndependentProfilesForTwoDevices()
{
    var configuration = ConfigurationWithProfiles(
        (0, new CameraCaptureMode(1280, 720, 30)),
        (1, new CameraCaptureMode(1920, 1080, 60)));
    await store.SaveAsync(configuration, CancellationToken.None);
    var loaded = await store.LoadAsync(CancellationToken.None);
    Assert.Equal(2, loaded.DeviceProfiles.Count);
    Assert.Equal(60, loaded.DeviceProfiles[1].Mode.FramesPerSecond);
}
```

- [ ] **Step 2: Run migration tests and verify they fail**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter CameraVisionConfigurationTests`

Expected: FAIL because schema 2 profiles are absent.

- [ ] **Step 3: Add schema-1 migration and schema-2 profile storage**

Keep `Capture` as the selected device's effective capture options for runtime compatibility. On schema-1 load, create one profile from `Capture`. On schema-2 load, validate every device index and mode, clone into a read-only dictionary, and select the current profile by `Capture.DeviceIndex`.

- [ ] **Step 4: Write failing linked-dropdown ViewModel tests**

```csharp
[Fact]
public async Task SelectingDevice_LoadsItsProfileAndFiltersFrameRatesByResolution()
{
    var control = ControlWithCapabilities(
        device0: [new(1280, 720, 30)],
        device1: [new(1920, 1080, 30), new(1920, 1080, 60)]);
    var vm = CreateViewModel(control);
    vm.DeviceIndex = 1;
    await vm.WaitForCapabilitiesAsync();
    Assert.Equal(new CameraResolutionOption(1920, 1080), vm.SelectedResolution);
    Assert.Equal(new double[] { 30, 60 }, vm.FrameRates);
}
```

- [ ] **Step 5: Implement device/resolution/FPS linkage**

Device selection asynchronously fetches capabilities. Resolution selection filters `Modes` by width/height. FPS selection comes only from modes for the selected resolution. If a saved mode is unavailable, choose the nearest mode using absolute pixel-count difference, then FPS difference, and set `CapabilityWarning`. The existing `ApplyCommand` writes the selected mode into `Capture` and updates `DeviceProfiles[DeviceIndex]`.

- [ ] **Step 6: Run all Camera configuration and ViewModel tests**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 7: Commit linked selections and profiles**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Configuration `
  providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewModel.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionConfigurationTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewModelTests.cs
git commit -m "feat(camera): persist device capture profiles"
```

### Task 4: Build aspect-fit and synchronized three-preview models

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/AspectFitTransform.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraPreviewModels.cs`
- Replace: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraPreviewOverlayBuilder.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionStatusSnapshot.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/AspectFitTransformTests.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraPreviewOverlayBuilderTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct AspectFitTransform(double Scale, double OffsetX, double OffsetY, double ContentWidth, double ContentHeight)` with `SourceToViewport` and `TryViewportToSource`.
- Produces: `CameraRawPreviewModel`, `CameraCalibrationPreviewModel`, and `CameraUnityPreviewModel`, all carrying `long TimestampUnixMs`.
- Produces: `CameraPreviewModelBuilder.Build(CameraVisionStatusSnapshot snapshot, InteractionSurface surface, double rawWidth, double rawHeight, double calibrationWidth, double calibrationHeight, double unityWidth, double unityHeight)`.

- [ ] **Step 1: Write failing aspect-fit tests**

```csharp
[Fact]
public void Create_WideSourceInSquareViewport_LetterboxesVertically()
{
    var fit = AspectFitTransform.Create(1920, 1080, 1000, 1000);
    Assert.Equal(1000, fit.ContentWidth, 3);
    Assert.Equal(562.5, fit.ContentHeight, 3);
    Assert.Equal(218.75, fit.OffsetY, 3);
    Assert.False(fit.TryViewportToSource(new Vector2Data(500, 100), out _));
    Assert.True(fit.TryViewportToSource(new Vector2Data(500, 500), out var source));
    Assert.Equal(960, source.X, 2);
    Assert.Equal(540, source.Y, 2);
}
```

- [ ] **Step 2: Run and verify missing transform fails compilation**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter AspectFitTransformTests`

Expected: FAIL because `AspectFitTransform` is missing.

- [ ] **Step 3: Implement the immutable aspect-fit transform**

Use `scale = Math.Min(viewportWidth / sourceWidth, viewportHeight / sourceHeight)`, center the content rectangle, reject non-finite/non-positive dimensions, and reject reverse-map points outside the content rectangle.

- [ ] **Step 4: Write failing synchronized preview-model tests**

Test a snapshot at timestamp `1234` containing two hands. Assert all three models have timestamp `1234`; raw points use Camera pixels and aspect fit; calibration output is rectangular; Unity model contains two centers and two arrays of exactly 21 points; `CameraUnityPreviewModel` exposes no bone collection.

- [ ] **Step 5: Implement the preview model builder**

The raw model contains the aspect-fitted bitmap rectangle, four calibration vertices, hand centers, joints, and optional outlines. The calibration model contains the perspective-warped BGR24 image plus the selected quadrilateral. The Unity model is built from the exact `InteractionPoint` payload generated for that hand, using `PixelPosition` and `Fp`; do not independently derive its positions from Camera pixels.

- [ ] **Step 6: Add timestamp and output points to Camera status**

Extend `CameraVisionStatusSnapshot` with `long TimestampUnixMs`, `int ActualWidth`, `int ActualHeight`, and `IReadOnlyList<InteractionPoint> OutputPoints`. `PublishControlStatus(CameraHandFrame)` passes the same output points subsequently sent through `FrameReceived`.

- [ ] **Step 7: Run Camera model tests**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release`

Expected: PASS.

- [ ] **Step 8: Commit synchronized preview models**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/UI `
  tests/Blaze.Provider.CameraVision.Tests/AspectFitTransformTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraPreviewOverlayBuilderTests.cs
git commit -m "feat(camera): build synchronized aspect-fit previews"
```

### Task 5: Add latest-only UI scheduling and reusable render surfaces

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/LatestUiFrameScheduler.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraImageSurface.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraPointSurface.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewModel.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/LatestUiFrameSchedulerTests.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraRenderSurfaceTests.cs`

**Interfaces:**
- Produces: `LatestUiFrameScheduler<T>(TimeSpan minimumInterval, ICameraUiDispatcher dispatcher, Action<T> render)` with `Offer(T frame)`, `RenderedCount`, `SupersededCount`, and `Dispose()`.
- Produces: `internal interface IMonotonicClock { TimeSpan Elapsed { get; } }`; production uses `Stopwatch`, tests use `ManualClock.Advance(TimeSpan)`.
- Produces: `CameraImageSurface : FrameworkElement` with `UpdateFrame(CameraPreviewSnapshot frame, CameraRawPreviewModel overlay)`.
- Produces: `CameraPointSurface : FrameworkElement` with `Update(CameraUnityPreviewModel model)`.

- [ ] **Step 1: Write failing latest-only scheduler tests**

```csharp
[Fact]
public async Task Offer_BurstBeforeNextInterval_RendersOnlyLatestFrame()
{
    var clock = new ManualClock();
    var rendered = new List<int>();
    using var scheduler = new LatestUiFrameScheduler<int>(
        TimeSpan.FromMilliseconds(66.666), dispatcher, rendered.Add, clock);
    scheduler.Offer(1);
    scheduler.Offer(2);
    scheduler.Offer(3);
    await dispatcher.DrainAsync();
    Assert.Equal(new[] { 3 }, rendered);
    Assert.Equal(2, scheduler.SupersededCount);
}
```

The test file defines `ManualClock : IMonotonicClock` and a `RecordingCameraUiDispatcher : ICameraUiDispatcher` whose `DrainAsync()` runs the single queued callback.

- [ ] **Step 2: Run and verify the scheduler test fails compilation**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter LatestUiFrameSchedulerTests`

Expected: FAIL because the scheduler is missing.

- [ ] **Step 3: Implement the bounded scheduler**

Store one pending item under a lock. Schedule at most one Dispatcher callback. At callback time, atomically take the latest item, enforce the minimum interval using a monotonic clock, invoke `render`, and schedule again only if a newer pending item arrived. After disposal, discard pending items and never invoke `render`.

- [ ] **Step 4: Write failing reusable-surface tests**

Instantiate each surface on an STA thread, update it with 100 same-size frames, and assert `WriteableBitmap` identity remains constant. Resize once, assert one replacement, then update another 100 frames and assert no further replacement. For point rendering, assert the surface has no per-point child `UIElement` collection.

- [ ] **Step 5: Implement reusable bitmap and custom drawing**

`CameraImageSurface` owns one `WriteableBitmap` per active pixel size and calls `WritePixels` with the preview buffer. Its `OnRender` paints black background, draws the bitmap in the model's aspect-fit content rectangle, then draws the four calibration edges/handles and hand marks using frozen pens/brushes. `CameraPointSurface.OnRender` draws each center and `Fp` point directly; it must not create `Ellipse`, `Line`, or `Polyline` controls.

- [ ] **Step 6: Route status snapshots through a 15 FPS scheduler**

Keep `CameraVisionSettingsViewModel`'s status coalescing for status text. Add a separate `LatestUiFrameScheduler<CameraVisionStatusSnapshot>` with `TimeSpan.FromSeconds(1d / 15d)` for visual models. Expose rendered/superseded counters in status diagnostics. Do not delay `FrameReceived` or processing events.

- [ ] **Step 7: Run all Camera tests and allocation-sensitive tests**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release`

Expected: PASS; burst test renders the newest item and has bounded pending state.

- [ ] **Step 8: Commit the bounded Camera UI renderer**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/UI `
  tests/Blaze.Provider.CameraVision.Tests/LatestUiFrameSchedulerTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraRenderSurfaceTests.cs
git commit -m "perf(camera): bound preview rendering workload"
```

### Task 6: Assemble the three-layer Camera console

**Files:**
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewModel.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionWindowLayoutTests.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewFactoryTests.cs`

**Interfaces:**
- Produces named preview hosts: `RawCameraPreview`, `CalibrationPreview`, and `UnityPointPreview`.
- Produces named dropdowns: `DeviceCombo`, `ResolutionCombo`, and `FrameRateCombo`.
- Preserves commands: `RefreshDevicesCommand`, `ReconnectCommand`, `ResetCalibrationCommand`, and `ApplyCommand`.

- [ ] **Step 1: Write failing XAML structure tests**

```csharp
[Fact]
public void CameraWindow_ContainsThreeVerticalPreviewRowsAndLinkedDropdowns()
{
    var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "providers",
        "CameraVision", "Blaze.Provider.CameraVision", "UI",
        "CameraVisionSettingsWindow.xaml"));
    Assert.Contains("x:Name=\"RawCameraPreview\"", xaml);
    Assert.Contains("x:Name=\"CalibrationPreview\"", xaml);
    Assert.Contains("x:Name=\"UnityPointPreview\"", xaml);
    Assert.Contains("Height=\"3*\"", xaml);
    Assert.Equal(2, Regex.Matches(xaml, "Height=\\\"1\\*\\\"").Count);
    Assert.Contains("x:Name=\"ResolutionCombo\"", xaml);
    Assert.Contains("x:Name=\"FrameRateCombo\"", xaml);
    Assert.DoesNotContain("Stretch=\"Fill\"", xaml);
}

private static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Unable to locate RadarControl.sln.");
}
```

- [ ] **Step 2: Run and verify layout test fails**

Run: `dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter CameraVisionWindowLayoutTests`

Expected: FAIL because the three hosts and dropdowns are absent.

- [ ] **Step 3: Implement the themed three-row workspace**

Use one left Grid with rows `3*`, `1*`, `1*`, splitters or fixed gaps, black preview backgrounds, and themed card borders. Put `CameraImageSurface` in the raw and calibration rows, and `CameraPointSurface` in the Unity row. Use the right ScrollViewer for “设备、识别、区域、输出、状态” groups and shared theme styles only.

- [ ] **Step 4: Replace numeric resolution/FPS text boxes with linked dropdowns**

Bind `ResolutionCombo.ItemsSource` to `Resolutions`, `SelectedItem` to `SelectedResolution`, `FrameRateCombo.ItemsSource` to `FrameRates`, and `SelectedItem` to `SelectedFrameRate`. Display actual width/height and actual FPS separately as read-only status values.

- [ ] **Step 5: Wire aspect-correct calibration input and render updates**

On raw preview click, call `TryViewportToSource`; ignore clicks in black letterbox space. Pass source pixel coordinates to `SetCalibrationPointAsync`. Window size changes update transforms but do not clone the BGR buffer or synchronously rebuild UI elements. Dispose scheduler and unsubscribe events when closed.

- [ ] **Step 6: Run Camera and Radar UI suites**

Run:

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 7: Commit the Camera console layout**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/UI `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionWindowLayoutTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewFactoryTests.cs
git commit -m "feat(camera): add synchronized three-view console"
```

### Task 7: Camera integration, Unity verification, and soak checkpoint

**Files:**
- Modify: `tests/Blaze.Interaction.Release.Tests/InteractionPublishLayoutTests.cs`
- Modify: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs`
- Create: `docs/verification/camera-console-stability.md`

**Interfaces:**
- Verifies: published provider contains all required Camera assemblies/resources.
- Verifies: Bridge hosts the Camera window with the shared header and returns to provider selection.
- Records: actual resolution, Camera FPS, inference FPS, output FPS, UI rendered frames, UI superseded frames, CPU, and working set.

- [ ] **Step 1: Write failing release-layout assertions**

Assert the published Camera provider contains the Camera assembly, model, native DLL, profile, manifest, and the embedded shared theme resource. Add a Bridge integration assertion that Camera settings still open through `IProviderSettingsViewFactory` after returning from the selector.

- [ ] **Step 2: Run release and Bridge tests and verify new assertions fail if packaging is incomplete**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
```

- [ ] **Step 3: Update packaging inputs only where the tests prove they are missing**

Ensure the existing publish pipeline copies the provider output containing the embedded resource; do not add a second theme file next to the provider DLL.

- [ ] **Step 4: Run complete managed and Unity verification**

Run:

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Runtime.Tests/Blaze.Interaction.Runtime.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
```

Run: `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform All -IncludeSamples`

- [ ] **Step 5: Perform the real-camera 15-minute interaction soak**

With Unity connected, verify at least two simultaneous hands, all three previews, four-corner dragging, device/mode switching, reconnect, continuous parameter edits, window resize, and return-to-selector. Capture process CPU and working set at 0, 5, 10, and 15 minutes. Acceptance requires no unhandled exception, no permanently growing UI queue, no sustained memory growth after warm-up, and unchanged Unity output FPS when UI preview is limited to 15 FPS.

- [ ] **Step 6: Record evidence and commit the Camera checkpoint**

Write measured values and test command results to `docs/verification/camera-console-stability.md`, then:

```powershell
git add tests/Blaze.Interaction.Release.Tests/InteractionPublishLayoutTests.cs `
  tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs `
  docs/verification/camera-console-stability.md
git commit -m "test(camera): verify console integration and stability"
```
