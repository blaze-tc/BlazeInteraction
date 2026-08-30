# Radar Project State and Footprint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make unified Radar settings project-local and persistent, report real Unity/Radar connectivity, and deliver every real Radar outline point through the provider-neutral `InteractionPoint.Fp` field while preserving the existing center interaction behavior.

**Architecture:** Extend Interaction IPC 1 additively with an immutable `Fp` pixel-coordinate list, carry current Radar cluster footprints through detection/fusion/tracking into the existing pointer batch, and adapt them into the standard contract. Unity resolves a project/player data scope and matching scoped Pipe; Bridge supplies that scope and authenticated client status to Providers through small Provider-neutral services.

**Tech Stack:** .NET 8, C# records, System.Text.Json, Named Pipes, WPF/MVVM, xUnit, Unity 2021.3.45f1, Newtonsoft.Json, NUnit, PowerShell release/test scripts, UnitySkills REST validation.

**Spec:** `docs/superpowers/specs/2026-08-24-radar-project-state-and-footprint-design.md`

## Global Constraints

- Work only on Gate A Radar: `RadarControl -> Interaction Core -> Radar Provider -> Interaction IPC -> com.blaze.interaction`.
- Do not implement CameraVision B1-B6; CameraVision may only compile against the additive default-empty `Fp` contract.
- `InteractionPoint.PixelPosition` is the center and `InteractionPoint.Fp` is the actual outline/scan point list in the same Surface pixel coordinate system.
- Always transmit center plus `Fp`; do not add center/edge output mode settings.
- Radar `Fp` contains all actual mapped cluster points, in deterministic acquisition order, without interpolation, synthesis, simplification, or silent truncation.
- `Fp` points do not receive independent IDs or phases; the outer `InteractionPoint` owns lifecycle.
- Editor state root is `<UnityProject>/Library/BlazeInteraction/`; Player state root is `<Application.persistentDataPath>/BlazeInteraction/`.
- Unified Provider mode must not fall back to `%LOCALAPPDATA%/Yuexin/RadarBridge/config.json` or legacy Radar IPC.
- Every task uses RED -> minimal GREEN -> complete related test project(s) -> commit.
- Stop on every Radar regression and restore all existing Radar behavior before continuing.
- Preserve all pre-existing untracked CameraVision documents and archives.

## File and Responsibility Map

- `src/Blaze.Interaction.Contracts/InteractionContracts.cs`: authoritative provider-neutral immutable `InteractionPoint.Fp` contract.
- `UnityPackage/com.blaze.interaction/Runtime/Contracts/InteractionMessageModels.cs`: Unity JSON mirror of `fp`.
- `UnityPackage/com.blaze.interaction/Runtime/InteractionFrameDispatcher.cs`: retain `Fp` through active-point caching and cancellation copies.
- `src/Radar.Contracts/RadarScreenContracts.cs`: Radar-internal screen-coordinate footprint transport; no public `RadarScanPoint` type.
- `src/Radar.Processing/RadarScreenFusionEngine.cs`: retain current footprints while fusing and tracking centers.
- `src/Radar.Bridge.Wpf/Services/RadarSensorPipeline.cs`: map every real cluster point to Surface pixels.
- `providers/Radar/Blaze.Provider.Radar/RadarFrameAdapter.cs`: convert Radar-internal footprint coordinates to `Vector2Data`.
- `UnityPackage/com.blaze.interaction/Runtime/Internal/InteractionProjectScope.cs`: deterministic Editor/Player paths and scoped Pipe name.
- `UnityPackage/com.blaze.interaction/Runtime/InteractionManager.cs`: configure the shared manager with the scoped Pipe before first connection.
- `UnityPackage/com.blaze.interaction/Runtime/InteractionBridgeLauncher.cs`: use one scope for probing, launching, and connecting.
- `src/Blaze.Interaction.Provider.Abstractions/ProviderServices.cs`: Provider-neutral storage and authenticated host status interfaces.
- `src/Blaze.Interaction.Bridge.Wpf/ProviderServices.cs`: Bridge-owned implementations and exact `IServiceProvider` registry.
- `src/Blaze.Interaction.Bridge.Wpf/BridgeCommandLine.cs`: parse `--data-root` and optional `--profile`.
- `src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs`: pass shared Provider services and publish authenticated Unity state.
- `providers/Radar/Blaze.Provider.Radar/RadarProviderConfiguration.cs`: bootstrap/load the writable project Radar profile.
- `providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs`: give the coordinator its writable path and map host status.
- `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`: derived Chinese Unity/Radar aggregate status text.
- `src/Radar.Bridge.Wpf/MainWindow.xaml`: bind the two real status values.
- `docs/protocol.md`: document optional `fp` and its coordinate/lifecycle rules.
- `docs/radar-project-state-footprint-test-report.md`: final commands, counts, live validation evidence, and limitations.

## Preflight: Capture the Hard Baseline

- [ ] Run `git status --short --branch` and confirm only the known user-owned CameraVision/archive files are untracked.
- [ ] Run `dotnet test BlazeInteraction.sln -c Release --nologo` and record the exact passed count.
- [ ] Run `powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release` and record the exact Radar hard-gate count.
- [ ] If either baseline fails, diagnose and restore the baseline before Task 1; do not reinterpret a pre-existing failure as an allowed regression.

---

### Task 1: Add `InteractionPoint.Fp` to the .NET Contract and IPC Codec

**Files:**
- Modify: `src/Blaze.Interaction.Contracts/InteractionContracts.cs`
- Modify: `tests/Blaze.Interaction.Contracts.Tests/InteractionContractTests.cs`
- Modify: `tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs`
- Modify: `docs/protocol.md`

**Interfaces:**
- Consumes: existing `Vector2Data`, `InteractionPoint`, `InteractionFrame`, and `InteractionJson.Options`.
- Produces: `InteractionPoint.Fp : IReadOnlyList<Vector2Data>`; JSON property `fp`; missing JSON becomes an immutable empty list.

- [ ] **Step 1: Write failing contract tests**

Add tests equivalent to:

```csharp
[Fact]
public void Point_FootprintIsAnImmutableSnapshot()
{
    var source = new List<Vector2Data> { new(10f, 20f), new(30f, 40f) };
    var point = CreatePoint(InteractionPhase.Move) with { Fp = source };

    source.Clear();

    Assert.Equal([new Vector2Data(10f, 20f), new Vector2Data(30f, 40f)], point.Fp);
    Assert.IsAssignableFrom<IReadOnlyList<Vector2Data>>(point.Fp);
}

[Fact]
public void Point_RejectsNullFootprintAndNullElements()
{
    Assert.Throws<ArgumentNullException>(() => CreatePoint(InteractionPhase.Move) with { Fp = null! });
    Assert.Throws<ArgumentException>(() => CreatePoint(InteractionPhase.Move) with
    {
        Fp = new Vector2Data[] { null! }
    });
}

[Fact]
public void Json_MissingFootprintUsesEmptyList()
{
    var json = InteractionJson.Serialize(CreatePoint(InteractionPhase.Move));
    var oldJson = json.Replace(",\"fp\":[]", string.Empty, StringComparison.Ordinal);

    var decoded = InteractionJson.Deserialize<InteractionPoint>(oldJson);

    Assert.NotNull(decoded.Fp);
    Assert.Empty(decoded.Fp);
}
```

Extend the IPC frame codec test with two coordinates and assert exact order and duplicates survive serialization.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release --filter "FullyQualifiedName~Footprint|FullyQualifiedName~MissingFootprint"
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~InteractionFrameCodecTests"
```

Expected: compile failures because `InteractionPoint.Fp` does not exist.

- [ ] **Step 3: Implement the immutable additive contract**

Add this shape to `InteractionPoint`:

```csharp
private IReadOnlyList<Vector2Data> _fp = Array.Empty<Vector2Data>();

public IReadOnlyList<Vector2Data> Fp
{
    get => _fp;
    init
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = value.ToArray();
        if (snapshot.Any(static point => point is null))
        {
            throw new ArgumentException("The footprint cannot contain null elements.", nameof(value));
        }

        _fp = Array.AsReadOnly(snapshot);
    }
}
```

Do not mark `Fp` as `required`; that would break old IPC JSON. Update `docs/protocol.md` to state pixel coordinate space, missing-as-empty behavior, ordering, and outer-point lifecycle ownership.

- [ ] **Step 4: Run complete related tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Runtime.Tests/Blaze.Interaction.Runtime.Tests.csproj -c Release
```

Expected: all pass; existing providers that omit `Fp` serialize it as `[]`.

- [ ] **Step 5: Commit**

```powershell
git add src/Blaze.Interaction.Contracts/InteractionContracts.cs tests/Blaze.Interaction.Contracts.Tests/InteractionContractTests.cs tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs docs/protocol.md
git commit -m "feat(contracts): add interaction footprint points"
```

---

### Task 2: Preserve `Fp` in the Unity Runtime

**Files:**
- Modify: `UnityPackage/com.blaze.interaction/Runtime/Contracts/InteractionMessageModels.cs`
- Modify: `UnityPackage/com.blaze.interaction/Runtime/InteractionFrameDispatcher.cs`
- Modify: `UnityPackage/com.blaze.interaction/Tests/Runtime/ProtocolAndBufferTests.cs`
- Modify: `UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionFrameDispatcherTests.cs`

**Interfaces:**
- Consumes: JSON `fp` emitted by Task 1.
- Produces: `InteractionPoint.Fp : List<Vector2Data>` in Unity; dispatcher Add/Update/Remove events retain the list.

- [ ] **Step 1: Write failing Unity serialization and dispatcher tests**

Add NUnit tests equivalent to:

```csharp
[Test]
public void Protocol_DeserializesFootprintAndDefaultsMissingFootprintToEmpty()
{
    const string pointJson = "\"id\":7,\"surfaceId\":\"front\",\"providerId\":\"p\"," +
        "\"providerInstanceId\":\"i\",\"sourceId\":\"s\",\"phase\":\"Move\"," +
        "\"normalizedPosition\":{\"x\":0.5,\"y\":0.5}," +
        "\"pixelPosition\":{\"x\":100,\"y\":200},\"confidence\":1," +
        "\"timestampUnixMs\":2,\"extensions\":null";
    const string json = "{\"messageType\":\"InteractionFrame\",\"protocolVersion\":1,\"sequence\":1," +
        "\"payload\":{\"providerId\":\"p\",\"providerInstanceId\":\"i\",\"surfaceId\":\"front\"," +
        "\"sequence\":1,\"timestampUnixMs\":2,\"points\":[{" + pointJson +
        ",\"fp\":[{\"x\":11,\"y\":22},{\"x\":33,\"y\":44}]}]}}";

    var point = InteractionIpcProtocol.Deserialize(json)
        .DeserializePayload<InteractionFrame>().Points[0];

    Assert.That(point.Fp, Has.Count.EqualTo(2));
    Assert.That(point.Fp[1].X, Is.EqualTo(33f));
}

[Test]
public void DisconnectCancellation_PreservesFootprint()
{
    var point = Point(7, InteractionPhase.Move, 100f);
    point.Fp.Add(new Vector2Data { X = 90f, Y = 50f });
    InteractionPoint removed = null;
    dispatcher.PointRemoved += value => removed = value;

    dispatcher.ApplyFrame(Frame(1, point));
    dispatcher.SetConnectionState(false);

    Assert.That(removed.Phase, Is.EqualTo(InteractionPhase.Cancel));
    Assert.That(removed.Fp, Has.Count.EqualTo(1));
}
```

Also cover missing `fp` -> non-null empty list and explicit `fp:null` -> rejected by `ApplyFrame`.

- [ ] **Step 2: Run Unity EditMode RED**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode
```

Expected: compile failures because Unity `InteractionPoint` lacks `Fp`.

- [ ] **Step 3: Implement the Unity mirror and copy rule**

Add:

```csharp
[JsonProperty("fp")]
public List<Vector2Data> Fp { get; set; } = new List<Vector2Data>();
```

In `ApplyFrame`, reject a received point whose `Fp` is explicitly null. In `CopyWithPhase`, assign `Fp = new List<Vector2Data>(point.Fp)` so a cancellation snapshot cannot share a mutable list with later user code.

- [ ] **Step 4: Run complete Unity package tests for this layer**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release
```

Expected: all EditMode and source-compatibility tests pass.

- [ ] **Step 5: Commit**

```powershell
git add UnityPackage/com.blaze.interaction/Runtime/Contracts/InteractionMessageModels.cs UnityPackage/com.blaze.interaction/Runtime/InteractionFrameDispatcher.cs UnityPackage/com.blaze.interaction/Tests/Runtime/ProtocolAndBufferTests.cs UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionFrameDispatcherTests.cs
git commit -m "feat(unity): expose interaction footprint points"
```

---

### Task 3: Carry Current Footprints Through Radar Fusion and Tracking

**Files:**
- Modify: `src/Radar.Contracts/RadarScreenContracts.cs`
- Modify: `src/Radar.Processing/RadarScreenFusionEngine.cs`
- Modify: `tests/Radar.Processing.Tests/RadarScreenFusionEngineTests.cs`
- Modify: `tests/Radar.Ipc.Tests/IpcFrameCodecTests.cs`

**Interfaces:**
- Consumes: screen-mapped cluster points from Task 4 through `SensorDetection.Footprint`.
- Produces: `RadarScreenPoint`, `SensorDetection.Footprint`, `FusedScreenTarget.Footprint`, and `RadarScreenPointer.Footprint`; these are Radar-internal transport fields, not a `RadarScanPoint` Unity API.

- [ ] **Step 1: Write failing fusion tests**

Add deterministic multi-sensor and stale-data tests equivalent to:

```csharp
[Fact]
public void Tick_MergesAllCurrentFootprintsInSensorThenAcquisitionOrder()
{
    var engine = CreateEngine(confirmFrames: 1);
    var now = DateTimeOffset.UtcNow;
    engine.Publish(new SensorDetectionFrame("sensor-b", now,
    [
        Detection(2, 100, 100, [new(12, 12), new(13, 13)])
    ]));
    engine.Publish(new SensorDetectionFrame("sensor-a", now,
    [
        Detection(1, 102, 100, [new(1, 1), new(2, 2)])
    ]));

    var pointer = Assert.Single(engine.Tick(now.AddMilliseconds(1)).Pointers);

    Assert.Equal(
        [new RadarScreenPoint(1, 1), new(2, 2), new(12, 12), new(13, 13)],
        pointer.Footprint);
}

[Fact]
public void Tick_MissingTrackDoesNotRepeatPreviousFootprint()
{
    var engine = CreateEngine(confirmFrames: 1, lostFrames: 3);
    var now = DateTimeOffset.UtcNow;
    engine.Publish(new SensorDetectionFrame("sensor", now,
    [
        Detection(1, 100, 100, [new(90, 90)])
    ]));
    Assert.NotEmpty(Assert.Single(engine.Tick(now.AddMilliseconds(1)).Pointers).Footprint);

    var held = Assert.Single(engine.Tick(now.AddMilliseconds(2)).Pointers);

    Assert.Empty(held.Footprint);
}

private static SensorDetection Detection(
    int id,
    float pixelX,
    float pixelY,
    IReadOnlyList<RadarScreenPoint> footprint) =>
    new(id, pixelX, pixelY, 1f, footprint);
```

Extend Radar legacy IPC codec coverage to verify footprint coordinates round-trip and old messages without the field produce an empty list.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release --filter "FullyQualifiedName~RadarScreenFusionEngineTests"
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~IpcFrameCodecTests"
```

Expected: compile failures for missing footprint members.

- [ ] **Step 3: Implement minimal Radar-internal footprint transport**

Add the coordinate-only transport type:

```csharp
public readonly record struct RadarScreenPoint(float PixelX, float PixelY);
```

Add constructor-backed, frozen `IReadOnlyList<RadarScreenPoint> Footprint` properties to `RadarScreenPointer`, `SensorDetection`, and `FusedScreenTarget`. Existing constructor calls must default to `Array.Empty<RadarScreenPoint>()`.

Change the internal observation shape to:

```csharp
private sealed record FusedObservation(
    float PixelX,
    float PixelY,
    float Confidence,
    int SourceSensorCount,
    IReadOnlyList<RadarScreenPoint> Footprint);
```

Build `Footprint` from fusion members ordered by normalized `SensorId`, retaining each detection's list order. When a track matches a current observation, replace its footprint. When `MissingFrames > 0`, expose an empty footprint. Do not smooth footprint coordinates.

- [ ] **Step 4: Run complete Radar Processing and legacy IPC tests**

Run:

```powershell
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release
```

Expected: all pass; existing center, phase, tracking and fusion assertions remain unchanged.

- [ ] **Step 5: Commit**

```powershell
git add src/Radar.Contracts/RadarScreenContracts.cs src/Radar.Processing/RadarScreenFusionEngine.cs tests/Radar.Processing.Tests/RadarScreenFusionEngineTests.cs tests/Radar.Ipc.Tests/IpcFrameCodecTests.cs
git commit -m "feat(radar): retain current target footprints"
```

---

### Task 4: Map Every Real Radar Cluster Point into `InteractionPoint.Fp`

**Files:**
- Modify: `src/Radar.Bridge.Wpf/Services/RadarSensorPipeline.cs`
- Modify: `providers/Radar/Blaze.Provider.Radar/RadarFrameAdapter.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarSensorPipelineTests.cs`
- Modify: `tests/Blaze.Provider.Radar.Tests/RadarFrameAdapterTests.cs`
- Modify: `tests/Radar.EndToEnd.Tests/MultiScreenRadarEndToEndTests.cs`

**Interfaces:**
- Consumes: Task 3 `SensorDetection(..., footprint)` and `RadarScreenPointer.Footprint`.
- Produces: one outer `InteractionPoint` per existing center pointer with `Fp` containing all associated current actual points as `Vector2Data`.

- [ ] **Step 1: Write failing pipeline and adapter tests**

Add a pipeline test whose cluster has three real points, including a duplicate, and assert all three mapped values and their order:

```csharp
Assert.Equal(3, detection.Footprint.Count);
Assert.Equal(new RadarScreenPoint(100f, 200f), detection.Footprint[0]);
Assert.Equal(new RadarScreenPoint(110f, 210f), detection.Footprint[1]);
Assert.Equal(detection.Footprint[1], detection.Footprint[2]);
```

Add an adapter test:

```csharp
var pointer = new RadarScreenPointer(
    7,
    RadarPointerPhase.Move,
    .5f,
    .5f,
    50f,
    50f,
    1f,
    123,
    [
        new RadarScreenPoint(100f, 200f),
        new RadarScreenPoint(110f, 210f)
    ]);

var point = Assert.Single(Assert.Single(adapter.Adapt(Batch(pointer))).Points);

Assert.Equal(new Vector2Data(pointer.PixelX, pointer.PixelY), point.PixelPosition);
Assert.Equal([new Vector2Data(100f, 200f), new Vector2Data(110f, 210f)], point.Fp);
```

Extend the multi-screen end-to-end assertion so each delivered pointer retains its own footprint and no point crosses Surface boundaries.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarSensorPipelineTests"
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release --filter "FullyQualifiedName~RadarFrameAdapterTests"
dotnet test tests/Radar.EndToEnd.Tests/Radar.EndToEnd.Tests.csproj -c Release --filter "FullyQualifiedName~MultiScreenRadarEndToEndTests"
```

Expected: footprint assertions fail because only centers are currently mapped.

- [ ] **Step 3: Implement all-or-nothing footprint mapping**

Replace center-only mapping with a focused helper:

```csharp
private bool TryMapPoint(float physicalX, float physicalY, out RadarScreenPoint point)
{
    point = default;
    if (!_options.Calibration.TryMap(physicalX, physicalY, out var localX, out var localY) ||
        !float.IsFinite(localX) || !float.IsFinite(localY))
    {
        return false;
    }

    var mapped = _options.OutputMapper.Map(localX, localY);
    if (!float.IsFinite(mapped.PixelX) || !float.IsFinite(mapped.PixelY))
    {
        return false;
    }

    point = new RadarScreenPoint(mapped.PixelX, mapped.PixelY);
    return true;
}
```

Map the center and every `cluster.Points` entry. If any actual point cannot be mapped to finite coordinates, reject that detection and log one diagnostic; never send a partial footprint. Preserve duplicates and input order. `RadarFrameAdapter` converts the frozen Radar list with `new Vector2Data(item.PixelX, item.PixelY)`.

- [ ] **Step 4: Run complete Radar hard gate**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
dotnet test tests/Radar.EndToEnd.Tests/Radar.EndToEnd.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all pass and Radar count is at least the captured preflight count.

- [ ] **Step 5: Commit**

```powershell
git add src/Radar.Bridge.Wpf/Services/RadarSensorPipeline.cs providers/Radar/Blaze.Provider.Radar/RadarFrameAdapter.cs tests/Radar.Bridge.Wpf.Tests/RadarSensorPipelineTests.cs tests/Blaze.Provider.Radar.Tests/RadarFrameAdapterTests.cs tests/Radar.EndToEnd.Tests/MultiScreenRadarEndToEndTests.cs
git commit -m "feat(radar): publish every actual footprint point"
```

---

### Task 5: Resolve One Unity Project Scope for Pipe, Storage, and Client

**Files:**
- Create: `UnityPackage/com.blaze.interaction/Runtime/Internal/InteractionProjectScope.cs`
- Modify: `UnityPackage/com.blaze.interaction/Runtime/InteractionManager.cs`
- Modify: `UnityPackage/com.blaze.interaction/Runtime/InteractionBridgeLauncher.cs`
- Modify: `UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionBridgeLauncherTests.cs`
- Create: `UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionProjectScopeTests.cs`

**Interfaces:**
- Consumes: `InteractionRuntimeSettings.PipeName`, `.ProfilePath`, Editor `Application.dataPath`, Player `Application.persistentDataPath`.
- Produces: `InteractionProjectScope(DataRoot, ProfilePath, PipeName)`; `InteractionManager.ConfigureShared(...)` called before `Connect`.

- [ ] **Step 1: Write failing scope and launcher tests**

Add tests for these exact cases:

```csharp
[Test]
public void EditorScope_UsesProjectLibraryAndStableScopedPipe()
{
    var scope = InteractionProjectScopeResolver.Resolve(
        true,
        @"E:\ProjectA\Assets",
        @"C:\PlayerData",
        "Blaze.InteractionBridge",
        "");

    Assert.That(scope.DataRoot, Is.EqualTo(Path.GetFullPath(@"E:\ProjectA\Library\BlazeInteraction")));
    Assert.That(scope.ProfilePath, Is.Null);
    Assert.That(scope.PipeName, Does.StartWith("Blaze.InteractionBridge."));
    Assert.That(scope.PipeName, Is.EqualTo(InteractionProjectScopeResolver.Resolve(
        true, @"E:\ProjectA\Assets", @"C:\Other", "Blaze.InteractionBridge", "").PipeName));
}

[Test]
public void DifferentProjects_GetDifferentPipesAndDataRoots()
{
    var a = ResolveEditor(@"E:\ProjectA\Assets");
    var b = ResolveEditor(@"E:\ProjectB\Assets");
    Assert.That(a.DataRoot, Is.Not.EqualTo(b.DataRoot));
    Assert.That(a.PipeName, Is.Not.EqualTo(b.PipeName));
}

private static InteractionProjectScope ResolveEditor(string assetsPath) =>
    InteractionProjectScopeResolver.Resolve(
        true,
        assetsPath,
        @"C:\PlayerData",
        "Blaze.InteractionBridge",
        "");
```

Change the launcher process test to require quoted `--data-root`, optional quoted `--profile`, and the same scoped Pipe passed to the probe and shared manager.

- [ ] **Step 2: Run Unity EditMode RED**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode
```

Expected: compile failures for the missing resolver and launcher argument failures.

- [ ] **Step 3: Implement deterministic project scope**

Create:

```csharp
internal sealed class InteractionProjectScope
{
    internal InteractionProjectScope(string dataRoot, string profilePath, string pipeName)
    {
        DataRoot = dataRoot;
        ProfilePath = profilePath;
        PipeName = pipeName;
    }

    internal string DataRoot { get; }
    internal string ProfilePath { get; }
    internal string PipeName { get; }
}

internal static class InteractionProjectScopeResolver
{
    internal static InteractionProjectScope Resolve(
        bool isEditor,
        string applicationDataPath,
        string persistentDataPath,
        string basePipeName,
        string profilePath);
}
```

Rules: Editor project root is the parent of `Assets`; Player root is `persistentDataPath`; relative profile overrides resolve under that environment root; absolute overrides remain absolute. Hash the normalized case-insensitive scope identity with SHA-256 and append the first 16 uppercase hex characters to the base Pipe Name.

Add:

```csharp
internal static InteractionManager ConfigureShared(
    string pipeName,
    int connectTimeoutMilliseconds,
    int reconnectDelayMilliseconds,
    int serverResponseTimeoutMilliseconds)
```

It may replace the shared manager only before it is connected; otherwise throw. Launcher resolves one scope, probes its Pipe, launches that Pipe, configures the shared manager with that Pipe, then connects. Do not leave any `settings.PipeName` direct use in probe/launch/connect paths.

- [ ] **Step 4: Run complete Unity runtime tests**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform PlayMode
```

Expected: all pass; existing default behavior works through the scoped effective Pipe.

- [ ] **Step 5: Commit**

```powershell
git add UnityPackage/com.blaze.interaction/Runtime/Internal/InteractionProjectScope.cs UnityPackage/com.blaze.interaction/Runtime/InteractionManager.cs UnityPackage/com.blaze.interaction/Runtime/InteractionBridgeLauncher.cs UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionBridgeLauncherTests.cs UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionProjectScopeTests.cs
git commit -m "feat(unity): scope bridge state to the current project"
```

---

### Task 6: Bootstrap and Persist the Radar Profile in the Project Sandbox

**Files:**
- Create: `src/Blaze.Interaction.Provider.Abstractions/ProviderServices.cs`
- Create: `src/Blaze.Interaction.Bridge.Wpf/ProviderServices.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/BridgeCommandLine.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/App.xaml.cs`
- Create: `providers/Radar/Blaze.Provider.Radar/RadarProviderConfiguration.cs`
- Modify: `providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs`
- Modify: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs`
- Modify: `tests/Blaze.Provider.Radar.Tests/RadarInteractionProviderTests.cs`

**Interfaces:**
- Consumes: launcher `--data-root` and optional `--profile` from Task 5.
- Produces: `IProviderStorageContext`; Radar writable path `<data-root>/Providers/blaze.radar.f10f20/config.json`; coordinator receives `configurationPath` so existing atomic store is active.

- [ ] **Step 1: Write failing CLI, service, bootstrap, and reload tests**

Add tests equivalent to:

```csharp
[Fact]
public void CommandLine_ParsesProjectDataRootAndProfileOverride()
{
    var options = BridgeCommandLine.Parse(
    [
        "--data-root", @"E:\Unity\Game\Library\BlazeInteraction",
        "--profile", @"E:\Unity\Game\Radar\custom.json"
    ]);

    Assert.Equal(Path.GetFullPath(@"E:\Unity\Game\Library\BlazeInteraction"), options.DataRoot);
    Assert.Equal(Path.GetFullPath(@"E:\Unity\Game\Radar\custom.json"), options.ProfilePath);
}

[Fact]
public async Task FirstLoad_CopiesBundledDefaultThenSaveSurvivesReload()
{
    using var provider = new TemporaryDirectory();
    using var data = new TemporaryDirectory();
    var bundledPath = Path.Combine(provider.Path, "profiles", "radar-default.json");
    var bundled = RadarAppConfiguration.CreateDefault();
    bundled.Screens[0].WidthPixels = 1111;
    await RadarConfigurationStore.SaveAsync(bundledPath, bundled);
    var storage = new FakeProviderStorageContext(data.Path, null);

    var loaded = await RadarProviderConfiguration.LoadAsync(provider.Path, storage, CancellationToken.None);
    loaded.Configuration.Screens[0].WidthPixels = 2222;
    await RadarConfigurationStore.SaveAsync(loaded.ConfigurationPath, loaded.Configuration);
    var reloaded = await RadarProviderConfiguration.LoadAsync(provider.Path, storage, CancellationToken.None);

    Assert.Equal(2222, reloaded.Configuration.Screens[0].WidthPixels);
    Assert.StartsWith(Path.GetFullPath(data.Path), reloaded.ConfigurationPath, StringComparison.OrdinalIgnoreCase);
}

private sealed record FakeProviderStorageContext(string DataRoot, string? ProfilePath)
    : IProviderStorageContext
{
    public string GetProviderDataDirectory(string providerId) =>
        Path.Combine(DataRoot, "Providers", providerId);
}
```

Add isolation, explicit profile override, unwritable root, and malformed profile tests. The malformed source must remain byte-for-byte unchanged.
Add a command-line test proving omission of `--data-root` is rejected rather than falling back to global AppData.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~CommandLine|FullyQualifiedName~ProviderStorage"
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release --filter "FullyQualifiedName~Configuration|FullyQualifiedName~Persistence"
```

Expected: missing option/interface/loader failures and the existing no-op persistence behavior is exposed.

- [ ] **Step 3: Implement Provider-neutral storage services**

Define:

```csharp
public interface IProviderStorageContext
{
    string DataRoot { get; }
    string? ProfilePath { get; }
    string GetProviderDataDirectory(string providerId);
}
```

`BridgeProviderStorageContext` normalizes the root once, validates Provider IDs against `[A-Za-z0-9._-]+`, and returns `<DataRoot>/Providers/<providerId>`. `BridgeServiceProvider` returns exact registered service instances and null for unknown service types. Pass the same instance to `ProviderCreateContext` and `ProviderInitializationContext` instead of `EmptyServiceProvider`.

Extend `BridgeLaunchOptions`/`BridgeHostOptions` with required `DataRoot` and optional `ProfilePath`; wire them from `App.xaml.cs`.

- [ ] **Step 4: Implement Radar profile bootstrap and real persistence**

Create:

```csharp
internal sealed record RadarProviderConfigurationResult(
    RadarAppConfiguration Configuration,
    string ConfigurationPath);

internal static class RadarProviderConfiguration
{
    internal static Task<RadarProviderConfigurationResult> LoadAsync(
        string providerDirectory,
        IProviderStorageContext storage,
        CancellationToken cancellationToken);
}
```

Resolve `storage.ProfilePath` when supplied; otherwise use `Path.Combine(storage.GetProviderDataDirectory(RadarFrameAdapter.ProviderId), "config.json")`. On first run, create the destination directory and copy the bundled `profiles/radar-default.json` without overwriting. Load with `RadarConfigurationStore.LoadAsync`, not silent recovery. Pass the returned path as `configurationPath` to `RadarBridgeCoordinator`.

- [ ] **Step 5: Run complete persistence-related tests and Radar hard gate**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all pass; Radar test count does not fall below preflight.

- [ ] **Step 6: Commit**

```powershell
git add src/Blaze.Interaction.Provider.Abstractions/ProviderServices.cs src/Blaze.Interaction.Bridge.Wpf/ProviderServices.cs src/Blaze.Interaction.Bridge.Wpf/BridgeCommandLine.cs src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs src/Blaze.Interaction.Bridge.Wpf/App.xaml.cs providers/Radar/Blaze.Provider.Radar/RadarProviderConfiguration.cs providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs tests/Blaze.Provider.Radar.Tests/RadarInteractionProviderTests.cs
git commit -m "fix(radar): persist settings in the unity project sandbox"
```

---

### Task 7: Publish Authenticated Unity Connection State to the Radar Provider

**Files:**
- Modify: `src/Blaze.Interaction.Provider.Abstractions/ProviderServices.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/ProviderServices.cs`
- Modify: `src/Blaze.Interaction.Ipc/InteractionPipeServer.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs`
- Modify: `providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs`
- Modify: `src/Radar.Bridge.Wpf/Services/RadarBridgeCoordinator.cs`
- Modify: `tests/Blaze.Interaction.Ipc.Tests/InteractionPipeServerTests.cs`
- Modify: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs`
- Modify: `tests/Blaze.Provider.Radar.Tests/RadarInteractionProviderTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarBridgeCoordinatorTests.cs`

**Interfaces:**
- Consumes: authenticated `InteractionPipeServer.ClientConnected` after HelloAck and new matching disconnect event.
- Produces: `IInteractionHostStatus.Current`, `Changed`; Radar `UnityClientStatus.IsConnected` reflects only acknowledged unified IPC.

- [ ] **Step 1: Write failing connection lifecycle tests**

Add server tests:

```csharp
[Fact]
public async Task AcknowledgedSession_RaisesConnectedThenDisconnectedExactlyOnce()
{
    await using var fixture = await ServerFixture.StartAsync();
    var states = new ConcurrentQueue<string>();
    fixture.Server.ClientConnected += (_, _) => states.Enqueue("connected");
    fixture.Server.ClientDisconnected += (_, _) => states.Enqueue("disconnected");

    await using (var client = await fixture.ConnectAndSendHelloAsync())
    {
        Assert.Equal(
            InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(client, fixture.Token)).MessageType);
        await WaitUntilAsync(() => states.Contains("connected"), fixture.Token);
    }
    await WaitUntilAsync(() => states.Contains("disconnected"), fixture.Token);

    Assert.Equal(["connected", "disconnected"], states);
}
```

Add rejected-Hello and failed-HelloAck tests proving no false connected/disconnected pair. Add Provider tests that push connected/disconnected snapshots and assert the Radar ViewModel runtime status changes without enabling legacy IPC.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~ConnectedThenDisconnected|FullyQualifiedName~RejectedHello"
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release --filter "FullyQualifiedName~UnityStatus"
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarBridgeCoordinatorTests"
```

Expected: missing disconnect event and provider status bridge failures.

- [ ] **Step 3: Define and implement host status service**

In Provider Abstractions define:

```csharp
public sealed record InteractionHostStatus(
    bool IsConnected,
    int ProcessId,
    string ClientVersion,
    IReadOnlyList<InteractionSurface> Surfaces)
{
    public static InteractionHostStatus Disconnected { get; } =
        new(false, 0, string.Empty, Array.Empty<InteractionSurface>());
}

public interface IInteractionHostStatus
{
    InteractionHostStatus Current { get; }
    event Action<InteractionHostStatus>? Changed;
}
```

`BridgeInteractionHostStatus` freezes surfaces and only publishes on real change. Register it in the same `BridgeServiceProvider` from Task 6.

- [ ] **Step 4: Emit disconnect and map state into Radar**

`InteractionPipeServer` must invoke `ClientDisconnected` exactly once in the acknowledged session's `finally`, after clearing/deactivating the active session. It must not fire for a client that never reached `TryAcknowledge()`.

Bridge maps Hello to `InteractionHostStatus` on `ClientConnected` and resets on `ClientDisconnected`/dispose. `RadarCoordinatorRuntime` subscribes to `IInteractionHostStatus`, converts surfaces to `RadarScreenInfo`, and calls a focused coordinator method:

```csharp
public void ApplyUnityConnectionStatus(UnityClientStatus status)
```

Provider mode uses this method; standalone Radar legacy mode retains its existing Radar IPC events. Unsubscribe during runtime disposal.

- [ ] **Step 5: Run complete IPC, Bridge, Provider and Radar tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj -c Release
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all pass with no Radar count regression.

- [ ] **Step 6: Commit**

```powershell
git add src/Blaze.Interaction.Provider.Abstractions/ProviderServices.cs src/Blaze.Interaction.Bridge.Wpf/ProviderServices.cs src/Blaze.Interaction.Ipc/InteractionPipeServer.cs src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs src/Radar.Bridge.Wpf/Services/RadarBridgeCoordinator.cs tests/Blaze.Interaction.Ipc.Tests/InteractionPipeServerTests.cs tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs tests/Blaze.Provider.Radar.Tests/RadarInteractionProviderTests.cs tests/Radar.Bridge.Wpf.Tests/RadarBridgeCoordinatorTests.cs
git commit -m "fix(radar): report authenticated unity connection state"
```

---

### Task 8: Display Unity and Aggregate Radar Status in the Control Window

**Files:**
- Modify: `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/ScreenItemViewModel.cs`
- Modify: `src/Radar.Bridge.Wpf/MainWindow.xaml`
- Modify: `tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/MainWindowBindingTests.cs`

**Interfaces:**
- Consumes: Radar `UnityClientStatus` from Task 7 and `SensorItemViewModel.RuntimeState`/`Configuration.Enabled`.
- Produces: `UnityConnectionText`, `EnabledRadarCount`, `ConnectedRadarCount`, and `RadarConnectionText`.

- [ ] **Step 1: Write failing ViewModel and XAML binding tests**

Add exact state cases:

```csharp
[Theory]
[InlineData(0, 0, "雷达：0/0 未配置")]
[InlineData(0, 2, "雷达：0/2 未连接")]
[InlineData(1, 2, "雷达：1/2 部分连接")]
[InlineData(2, 2, "雷达：2/2 已连接")]
public async Task RadarConnectionText_ReflectsEnabledRunningSensors(
    int running,
    int enabled,
    string expected)
{
    var configuration = new RadarAppConfiguration
    {
        Screens = [Screen("front", true, Enumerable.Range(1, enabled).Select(i => $"s{i}").ToArray())]
    };
    var runtime = new TestRuntime();
    using var viewModel = new MainViewModel(configuration, runtime);
    foreach (var sensor in configuration.Screens[0].Sensors.Take(running))
    {
        runtime.PublishSensorState(new RadarSensorRuntimeStateChanged(
            "front", sensor.SensorId, RadarSensorRuntimeState.Running));
    }
    await WaitUntilAsync(() => viewModel.ConnectedRadarCount == running);
    Assert.Equal(expected, viewModel.RadarConnectionText);
}

[Fact]
public async Task UnityConnectionText_UsesChineseConnectedState()
{
    var runtime = new TestRuntime();
    using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
    runtime.PublishUnityStatus(new UnityClientStatus(
        true, 42, "2021.3.45f1", [], null, 0, null));
    await WaitUntilAsync(() => viewModel.UnityStatus.IsConnected);
    Assert.Equal("Unity：已连接", viewModel.UnityConnectionText);
}
```

Change `TestRuntime.UnityStatus` to a private-set property, give `UnityStatusChanged` a backing event, and add `PublishUnityStatus` that updates the property before raising the event.

Binding tests must assert the header binds `UnityConnectionText` and `RadarConnectionText` and no longer formats the raw Boolean.

- [ ] **Step 2: Run RED tests**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~MainViewModelTests|FullyQualifiedName~MainWindowBindingTests"
```

Expected: properties and bindings do not exist.

- [ ] **Step 3: Implement derived status properties and notifications**

Add:

```csharp
public string UnityConnectionText => UnityStatus.IsConnected
    ? "Unity：已连接"
    : "Unity：未连接";

public int EnabledRadarCount => Screens.Sum(screen =>
    screen.Sensors.Count(sensor => sensor.Configuration.Enabled));

public int ConnectedRadarCount => Screens.Sum(screen =>
    screen.Sensors.Count(sensor =>
        sensor.Configuration.Enabled && sensor.RuntimeState == RadarSensorRuntimeState.Running));

public string RadarConnectionText => EnabledRadarCount switch
{
    0 => "雷达：0/0 未配置",
    _ when ConnectedRadarCount == 0 => $"雷达：0/{EnabledRadarCount} 未连接",
    _ when ConnectedRadarCount == EnabledRadarCount =>
        $"雷达：{ConnectedRadarCount}/{EnabledRadarCount} 已连接",
    _ => $"雷达：{ConnectedRadarCount}/{EnabledRadarCount} 部分连接"
};
```

Raise all dependent property notifications after Unity status, sensor state, sensor add/delete, and configuration refresh. Subscribe/unsubscribe screen property changes so changes to enabled/runtime state cannot leave the header stale.

- [ ] **Step 4: Run full WPF and Radar hard-gate tests**

Run:

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all pass; the old Boolean binding assertion is removed only after the new text bindings pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs src/Radar.Bridge.Wpf/ViewModels/ScreenItemViewModel.cs src/Radar.Bridge.Wpf/MainWindow.xaml tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs tests/Radar.Bridge.Wpf.Tests/MainWindowBindingTests.cs
git commit -m "feat(radar): show unity and radar connection status"
```

---

### Task 9: Embed the Updated Bridge and Perform Full Gate A Verification

**Files:**
- Modify generated payload: `UnityPackage/com.blaze.interaction/Bridge~/win-x64/**`
- Modify: `scripts/test-embedded-bridge.ps1`
- Create: `docs/radar-project-state-footprint-test-report.md`
- Modify: `tests/Radar.Unity.Compatibility.Tests/EmbeddedBridgePayloadTests.cs`

**Interfaces:**
- Consumes: all source changes and release scripts from Tasks 1-8.
- Produces: Unity-importable Package with matching Bridge/Provider binaries and an evidence-backed Gate A test report.

- [ ] **Step 1: Add failing embedded-payload assertions before publishing**

Require the embedded contract assembly to contain the new `InteractionPoint.Fp` property and retain exactly one root executable. Add a metadata-only assertion so the test does not load the embedded assembly into the default context:

```csharp
[Fact]
public void EmbeddedContracts_ExposeInteractionFootprint()
{
    var assemblyPath = Path.Combine(
        FindRepositoryRoot(),
        "UnityPackage", "com.blaze.interaction", "Bridge~", "win-x64",
        "Blaze.Interaction.Contracts.dll");
    using var stream = File.OpenRead(assemblyPath);
    using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var interactionPoint = metadata.TypeDefinitions
        .Select(metadata.GetTypeDefinition)
        .Single(type => metadata.GetString(type.Namespace) == "Blaze.Interaction.Contracts" &&
                        metadata.GetString(type.Name) == "InteractionPoint");

    Assert.Contains(interactionPoint.GetProperties(), handle =>
        metadata.GetString(metadata.GetPropertyDefinition(handle).Name) == "Fp");
}
```

Run:

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~EmbeddedBridgePayloadTests"
```

Expected: FAIL because the tracked embedded Bridge still contains the previous binaries.

- [ ] **Step 2: Publish and embed from a validated temporary output directory**

First update `scripts/test-embedded-bridge.ps1` to create a unique data root beneath its existing validated temporary test directory, pass that exact path through `--data-root`, and remove only that validated child during existing cleanup. Then run:

Run:

```powershell
$radarPublishRoot = Join-Path $env:TEMP ("BlazeInteractionBridge-" + [Guid]::NewGuid().ToString("N"))
powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 -OutputDirectory $radarPublishRoot -EmbedUnityPackage
```

Expected: publish succeeds, external and embedded hashes match, and CameraVision is only republished unchanged for package completeness.

- [ ] **Step 3: Run release and embedded Bridge verification**

Run:

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -StartupTimeoutSeconds 20
```

Expected: all pass; real Hello/HelloAck smoke uses the scoped Pipe arguments accepted by Bridge.

- [ ] **Step 4: Run all automated gates**

Run:

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -UnityEditor "D:\Developer\2021.3.45f1\Editor\Unity.exe" -TestPlatform All -IncludeSamples
```

Expected: no failures or inconclusive Unity tests; Radar and full-solution counts are at least their preflight baselines.

- [ ] **Step 5: Validate in the already-open Unity project using UnitySkills**

Use UnitySkills in this order:

1. `GET /health` and `GET /skills?summary=1`.
2. Submit a `workflow_plan`; dry-run the exact `project_get_info`, `editor_get_state`, `console_get_stats`, `console_get_logs`, test and Play Mode calls before execution.
3. Confirm the project is the intended BlazeInteraction test project and the BasicInteraction sample is loaded.
4. Enter Play Mode and wait for the unified Bridge/Radar control window.
5. Verify `Unity：已连接` only after HelloAck and `Unity：未连接` after leaving Play Mode.
6. Start simulation or real Radar as available; verify the aggregate Radar count matches device rows.
7. Change a harmless visible Radar parameter and screen region, click “保存并应用配置”, and record the displayed project configuration path.
8. Exit and re-enter Play Mode; verify the exact value and region return without manual re-entry.
9. Capture a received frame and verify the center plus `Fp`; for simulation/real data, assert `Fp.Count` equals the cluster's actual mapped point count.
10. Finish with Unity Console stats showing zero errors; record hardware-only checks explicitly if real Radar is unavailable.

- [ ] **Step 6: Write the final evidence report**

Before writing the report, capture the verified commit with:

```powershell
$verifiedRadarCommit = git rev-parse HEAD
```

Create `docs/radar-project-state-footprint-test-report.md` with the title `Radar Project State and Footprint Test Report`, date `2026-08-24`, and `$verifiedRadarCommit`. Add:

- An `Automated Results` table with Gate, exact command, Passed, Failed, and Result columns. Copy the observed numeric counts from the full solution, Radar hard gate, Unity EditMode/PlayMode, and embedded Bridge runs; every Failed value must be `0` and every Result must be `PASS`.
- A `Live Unity Evidence` section containing the observed project path, sandbox configuration path, parameter name and before/after/reload values, Unity state transitions, Radar aggregate transitions, center pixel coordinate, `Fp.Count`, source cluster point count, and final Unity Console error count.
- A `Hardware Status` section containing either `Real Radar validation: PASS` with the observed device or `Real Radar validation: REQUIRES HARDWARE VALIDATION` with the concrete missing-hardware reason.

Do not create or commit the report until every required value has been observed.

- [ ] **Step 7: Commit embedded payload and evidence**

```powershell
git add UnityPackage/com.blaze.interaction/Bridge~/win-x64 scripts/test-embedded-bridge.ps1 tests/Radar.Unity.Compatibility.Tests/EmbeddedBridgePayloadTests.cs docs/radar-project-state-footprint-test-report.md
git commit -m "build(unity): embed verified radar project-state release"
```

- [ ] **Step 8: Final clean verification**

Run:

```powershell
git status --short --branch
git diff HEAD~1 --check
```

Expected: only the original user-owned untracked CameraVision/archive files remain; no generated temporary Unity test project or unrelated file is staged.
