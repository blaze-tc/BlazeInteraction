# VRCave Multi-Screen Multi-Radar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 RadarControl 升级为可由 Unity 配置任意逻辑屏幕、由单个 RadarBridge 管理每屏多个雷达并完成同屏融合、再向 Unity Camera 和原生 EventSystem 输出稳定点位的 1.2.0 版本。

**Architecture:** Unity 的 `RadarRuntimeSettings` 是屏幕拓扑唯一来源，IPC v2 在握手时同步拓扑；Bridge 为每个雷达创建隔离的数据源管线，为每个屏幕创建融合/跟踪管线，并通过一条 Named Pipe 发布多屏批次。Unity 主线程将批次派发为逐屏帧和逐点委托，旧版主屏 `PointerFrameReceived` 继续驱动 `RadarInputModule`，新 `RadarScreenCameraRouter` 负责逻辑屏幕到 Camera 的射线映射。

**Tech Stack:** .NET 8、C# 12、WPF、System.Text.Json、Named Pipes、xUnit、Unity 2021.3 LTS、UGUI EventSystem、Newtonsoft.Json、Unity Test Framework、PowerShell。

## Global Constraints

- 目标版本固定为 SDK/Bridge `1.2.0`，IPC 协议固定为 `2`，Git 标签固定为 `v1.2.0`。
- Unity 包名保持 `com.blaze.radar`，Unity 公共命名空间保持 `Blaze.Radar`，Named Pipe 名称保持 `Yuexin.RadarBridge`。
- Unity 最低版本保持 `2021.3`；Bridge 保持 `net8.0-windows`、`win-x64`、self-contained、多文件发布。
- 屏幕数量不设硬编码上限；只能在 Unity 编辑器配置，Player 运行期间不从外部 JSON 增删屏幕。
- 屏幕像素坐标原点固定为左下角，X 向右、Y 向上；`OutputRectPixels` 使用相同坐标系。
- 一个 Bridge 进程、一条 Named Pipe；每个雷达故障必须与其他雷达和屏幕隔离。
- Bridge 完成同屏跨雷达去重、跟踪和平滑；Unity 不再次融合。
- 热变更或拓扑移除时，每个活动 Pointer 先且仅发一次 `Up`，下一批发零点帧，再重置 Tracker。
- 现有 `PointerFrameReceived`、Basic Interaction Sample、父进程退出、WPF 软件渲染与像素对齐行为必须保持可用。
- 发布包必须内嵌完整 `UnityPackage/com.blaze.radar/Bridge~/win-x64/RadarBridge.exe` 目录，并验证版本标记与 SHA-256。

## File Structure

### .NET contracts and configuration

- Create `src/Radar.Contracts/RadarScreenContracts.cs`: 屏幕定义、屏幕 Pointer、逐屏帧和批次 DTO。
- Modify `src/Radar.Contracts/IpcMessages.cs`: IPC v2 Hello/HelloAck/PointerBatch 消息。
- Modify `src/Radar.Ipc/IpcProtocolVersion.cs`: 协议版本 2 与明确拒绝信息。
- Create `src/Radar.Configuration/RadarScreenConfiguration.cs`: Schema 2 屏幕、传感器、输出矩形、融合配置。
- Create `src/Radar.Configuration/LegacyRadarAppConfiguration.cs`: 只用于读取 Schema 1 的 DTO。
- Modify `src/Radar.Configuration/RadarAppConfiguration.cs`: Schema 2 根对象与可复用旧配置节。
- Modify `src/Radar.Configuration/RadarConfigurationStore.cs`: Schema 1 备份、迁移和原子保存。
- Modify `src/Radar.Configuration/ConfigurationValidator.cs`: 屏幕/传感器/分辨率/输出矩形/融合参数校验。

### Processing and Bridge runtime

- Create `src/Radar.Processing/RadarOutputMapper.cs`: 雷达局部 0–1 到屏幕像素矩形映射。
- Create `src/Radar.Processing/RadarScreenFusionEngine.cs`: 检测过期、跨雷达去重、屏幕跟踪、Pointer 生命周期。
- Create `src/Radar.Bridge.Wpf/Services/IRadarSensorPipeline.cs`: 每雷达管线边界与快照事件。
- Create `src/Radar.Bridge.Wpf/Services/RadarSensorPipeline.cs`: Real/Simulation/Replay、解析、过滤、聚类、映射、录制。
- Create `src/Radar.Bridge.Wpf/Services/RadarBridgeCoordinator.cs`: 屏幕拓扑、管线字典、融合 Tick、IPC 批次和全局关闭。
- Create `src/Radar.Bridge.Wpf/BridgeVersion.cs`: 从旧单雷达 Runtime 中独立出 Bridge 版本常量。
- Modify `src/Radar.Bridge.Wpf/Services/IRadarBridgeRuntime.cs`: 多屏快照、状态和按传感器命令。
- Retire `src/Radar.Bridge.Wpf/Services/RadarBridgeRuntime.cs`: 在 Coordinator 验证完成后删除单雷达实现。
- Modify `src/Radar.Ipc/RadarPipeServer.cs`: Hello 拓扑验证后再 Ack。

### WPF UI

- Create `src/Radar.Bridge.Wpf/ViewModels/ScreenItemViewModel.cs`: 屏幕选择、分辨率、融合/跟踪/交互属性。
- Create `src/Radar.Bridge.Wpf/ViewModels/SensorItemViewModel.cs`: 雷达选择、连接、来源、映射和状态属性。
- Modify `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`: 只编排选择、命令、过滤日志和配置保存。
- Create `src/Radar.Bridge.Wpf/Controls/RadarScreenFusionView.cs`: 屏幕像素空间中的多雷达映射矩形、检测、融合目标和 Pointer。
- Modify `src/Radar.Bridge.Wpf/Controls/RadarPointCloudView.cs`: 只显示选中雷达原始/有效物理点。
- Modify `src/Radar.Bridge.Wpf/MainWindow.xaml`: 屏幕/雷达列表、双可视区、屏幕/雷达参数页、有界日志。
- Modify `src/Radar.Bridge.Wpf/App.xaml.cs`: DI 改为 Coordinator 和传感器工厂。

### Unity SDK and samples

- Create `UnityPackage/com.blaze.radar/Runtime/RadarScreenDefinition.cs`: 可序列化屏幕拓扑及纯数据校验。
- Modify `UnityPackage/com.blaze.radar/Runtime/RadarRuntimeSettings.cs`: 任意屏幕列表与主屏访问器。
- Modify `UnityPackage/com.blaze.radar/Runtime/RadarMessageModels.cs`: IPC v2 JSON 模型。
- Modify `UnityPackage/com.blaze.radar/Runtime/RadarPipeClient.cs`: 多屏批次缓冲与握手。
- Modify `UnityPackage/com.blaze.radar/Runtime/RadarFrameDispatcher.cs`: 新委托、最新逐屏帧与旧主屏适配。
- Modify `UnityPackage/com.blaze.radar/Runtime/RadarInputModule.cs`: `ScreenId` 过滤与屏幕局部坐标消费。
- Create `UnityPackage/com.blaze.radar/Runtime/RadarScreenCameraRouter.cs`: `ScreenId → Camera`、像素换算和射线。
- Modify `UnityPackage/com.blaze.radar/Editor/RadarSettingsProvider.cs`: 屏幕数组编辑与即时错误提示。
- Modify `UnityPackage/com.blaze.radar/Editor/RadarBuildProcessor.cs`: 非法拓扑构建失败和 1.2.0 Bridge 验证。
- Modify `UnityPackage/com.blaze.radar/Samples~/BasicInteraction/RadarDemoLogger.cs`: 主屏 ID、分辨率和点位日志。
- Create `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/*`: 三屏 Camera 路由、模拟器、射线、粒子、点位和日志 Demo。

### Tests, scripts, documentation and release

- Add focused xUnit tests under existing `tests/*` projects for protocol/config/processing/runtime/UI.
- Add Unity runtime tests under `UnityPackage/com.blaze.radar/Tests/Runtime/`.
- Create `scripts/test-unity-package.ps1`: 临时 Unity 2021.3 工程与 EditMode/PlayMode 批处理测试。
- Create `tests/Radar.EndToEnd.Tests/`: 三屏四雷达 IPC 模拟验收。
- Modify `scripts/publish-bridge.ps1` and `scripts/test-embedded-bridge.ps1`: 1.2.0 发布、启动、版本和父进程测试。
- Modify package README/CHANGELOG/Documentation、根 README/INSTALL 和集成/故障文档。

---

### Task 1: Define IPC v2 multi-screen contracts

**Files:**
- Create: `src/Radar.Contracts/RadarScreenContracts.cs`
- Modify: `src/Radar.Contracts/IpcMessages.cs`
- Modify: `src/Radar.Ipc/IpcProtocolVersion.cs`
- Test: `tests/Radar.Ipc.Tests/IpcFrameCodecTests.cs`

**Interfaces:**
- Consumes: 现有 `RadarPointerPhase`、`IpcEnvelope.Create<TPayload>` 和 camelCase JSON 策略。
- Produces: `RadarScreenDefinitionPayload`, `RadarScreenInfo`, `RadarScreenPointer`, `RadarScreenPointerFrame`, `PointerBatchPayload`, IPC protocol `2`。

- [ ] **Step 1: Write failing v2 round-trip and version tests**

```csharp
[Fact]
public void EncodeAndAppend_RoundTripsMultiScreenPointerBatch()
{
    var batch = new PointerBatchPayload([
        new RadarScreenPointerFrame(
            new RadarScreenInfo("front", "正面", 4096, 1536, true, 1),
            7,
            1000,
            [new RadarScreenPointer(3, RadarPointerPhase.Move, 0.25f, 0.75f, 1024f, 1152f, 0.9f, 1000)])
    ]);

    var bytes = IpcFrameCodec.Encode(IpcEnvelope.Create(IpcMessageType.PointerBatch, 7, batch, 1000));
    var decoded = Assert.Single(new IpcFrameDecoder().Append(bytes));
    var result = decoded.DeserializePayload<PointerBatchPayload>();

    Assert.Equal(2, decoded.ProtocolVersion);
    Assert.Equal("front", Assert.Single(result.Screens).Screen.ScreenId);
    Assert.Equal(1024f, Assert.Single(result.Screens[0].Pointers).PixelX);
}

[Fact]
public void ProtocolVersion_RejectsVersionOneWithExplicitMessage()
{
    var envelope = IpcEnvelope.Create(IpcMessageType.Hello, 1, new { }, protocolVersion: 1);
    var result = IpcProtocolVersion.Validate(envelope);
    Assert.False(result.IsCompatible);
    Assert.Contains("version 1", result.Error);
    Assert.Contains("version 2", result.Error);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~IpcFrameCodecTests"
```

Expected: compilation fails because `PointerBatch`, `RadarScreenInfo` and related types do not exist, and the current protocol is 1.

- [ ] **Step 3: Add the exact v2 contract surface**

```csharp
namespace Yuexin.Radar.Contracts;

public readonly record struct RadarPixelRect(int X, int Y, int Width, int Height);

public sealed record RadarScreenDefinitionPayload(
    string ScreenId,
    string Name,
    int DefaultWidthPixels,
    int DefaultHeightPixels,
    bool IsPrimary,
    int Order);

public sealed record RadarScreenInfo(
    string ScreenId,
    string Name,
    int WidthPixels,
    int HeightPixels,
    bool IsPrimary,
    int Order);

public sealed record RadarScreenPointer(
    int PointerId,
    RadarPointerPhase Phase,
    float NormalizedX,
    float NormalizedY,
    float PixelX,
    float PixelY,
    float Confidence,
    long TimestampUnixMilliseconds);

public sealed record RadarScreenPointerFrame(
    RadarScreenInfo Screen,
    long Sequence,
    long TimestampUnixMilliseconds,
    IReadOnlyList<RadarScreenPointer> Pointers);
```

Update the message enum and payloads exactly as follows:

```csharp
public enum IpcMessageType
{
    Hello = 1,
    HelloAck = 2,
    Status = 3,
    PointerFrame = 4,
    RawScanFrame = 5,
    ConfigurationChanged = 6,
    Error = 7,
    Ping = 8,
    Pong = 9,
    Shutdown = 10,
    PointerBatch = 11
}

public sealed record HelloPayload(
    int UnityProcessId,
    string UnityVersion,
    IReadOnlyList<RadarScreenDefinitionPayload> Screens);

public sealed record HelloAckPayload(
    string BridgeVersion,
    int ProtocolVersion,
    bool Connected,
    string Capability,
    IReadOnlyList<RadarScreenInfo> Screens);

public sealed record PointerBatchPayload(IReadOnlyList<RadarScreenPointerFrame> Screens);
```

Change `IpcEnvelope.Create` default `protocolVersion` and `IpcProtocolVersion.Current` to `2`. Retain `PointerFramePayload` only as an internal legacy adapter payload; never publish it on protocol v2.

- [ ] **Step 4: Run IPC tests and verify v2 JSON names and values**

```powershell
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release
```

Expected: all IPC tests pass; encoded envelopes contain `"protocolVersion":2`, `"messageType":"PointerBatch"`, and camelCase screen fields.

- [ ] **Step 5: Commit the protocol contract**

```powershell
git add src/Radar.Contracts src/Radar.Ipc tests/Radar.Ipc.Tests
git commit -m "feat: define multi-screen IPC v2 contracts"
```

### Task 2: Implement Schema 2 configuration, migration and validation

**Files:**
- Create: `src/Radar.Configuration/RadarScreenConfiguration.cs`
- Create: `src/Radar.Configuration/LegacyRadarAppConfiguration.cs`
- Modify: `src/Radar.Configuration/RadarAppConfiguration.cs`
- Modify: `src/Radar.Configuration/RadarConfigurationStore.cs`
- Modify: `src/Radar.Configuration/ConfigurationValidator.cs`
- Test: `tests/Radar.Configuration.Tests/RadarConfigurationTests.cs`
- Modify: `config/default-profile.json`
- Modify: `config/f20-profile.json`

**Interfaces:**
- Consumes: `RadarModel`, all current device/transform/range/clustering/calibration/interaction property shapes.
- Produces: `RadarAppConfiguration.Screens`, `RadarScreenConfiguration.EffectiveWidthPixels`, `RadarScreenConfiguration.EffectiveHeightPixels`, `RadarConfigurationStore.LoadAsync` migration backup.

- [ ] **Step 1: Write failing defaults, migration and strict-validation tests**

```csharp
[Fact]
public void NewConfiguration_CreatesOnePrimaryMainScreenAndSensor()
{
    var configuration = RadarAppConfiguration.CreateDefault();
    var screen = Assert.Single(configuration.Screens);
    var sensor = Assert.Single(screen.Sensors);
    Assert.Equal(2, configuration.SchemaVersion);
    Assert.Equal("main", screen.ScreenId);
    Assert.True(screen.IsPrimary);
    Assert.Equal(new RadarPixelRect(0, 0, 1920, 1080), sensor.OutputRectPixels);
}

[Fact]
public async Task LoadAsync_SchemaOne_CreatesBackupAndMigratesAllSections()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "config.json");
    await File.WriteAllTextAsync(path, """
        {"schemaVersion":1,"device":{"deviceModel":"F20","radarIp":"10.0.0.8","port":8487},
         "range":{"minimumDistanceMeters":0.2,"maximumDistanceMeters":20,"visualizationRangeMeters":8}}
        """);

    var migrated = await RadarConfigurationStore.LoadAsync(path);

    Assert.Equal(2, migrated.SchemaVersion);
    Assert.Equal(RadarModel.F20, migrated.Screens[0].Sensors[0].Device.DeviceModel);
    Assert.Equal("10.0.0.8", migrated.Screens[0].Sensors[0].Device.RadarIp);
    Assert.Single(Directory.GetFiles(directory, "config.schema1.*.bak"));
}

[Fact]
public void Validator_RejectsDuplicateIdsAndOutOfBoundsOutputRect()
{
    var configuration = RadarAppConfiguration.CreateDefault();
    configuration.Screens.Add(configuration.Screens[0].CloneWithId("main"));
    configuration.Screens[0].Sensors[0].OutputRectPixels = new RadarPixelRect(1800, 0, 400, 1080);

    var result = ConfigurationValidator.ValidateAndNormalize(configuration);

    Assert.False(result.IsValid);
    Assert.Contains(result.Errors, value => value.Contains("duplicate screenId", StringComparison.OrdinalIgnoreCase));
    Assert.Contains(result.Errors, value => value.Contains("outputRectPixels", StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 2: Run configuration tests and confirm the schema assumptions fail**

```powershell
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
```

Expected: compilation fails on `Screens`, `RadarPixelRect`, `CloneWithId`, and Schema 2 defaults.

- [ ] **Step 3: Define the Schema 2 model with exact defaults**

```csharp
public enum RadarResolutionMode { FollowUnityDefault = 0, Override = 1 }
public enum RadarSensorSourceMode { Real = 0, Simulation = 1, Replay = 2 }

public sealed class RadarFusionConfiguration
{
    public int OutputRateHz { get; set; } = 30;
    public int SensorDataMaxAgeMilliseconds { get; set; } = 150;
    public float FusionDistancePixels { get; set; } = 80f;
}

public sealed class RadarScreenTrackingConfiguration
{
    public int ConfirmFrames { get; set; } = 2;
    public int LostFrames { get; set; } = 3;
    public float MaximumAssociationDistancePixels { get; set; } = 160f;
    public float SmoothingAlpha { get; set; } = 0.5f;
}

public sealed class RadarSensorConfiguration
{
    public string SensorId { get; set; } = "sensor-1";
    public string DisplayName { get; set; } = "雷达 1";
    public bool Enabled { get; set; } = true;
    public RadarSensorSourceMode SourceMode { get; set; } = RadarSensorSourceMode.Real;
    public RadarDeviceConfiguration Device { get; set; } = new();
    public RadarTransformConfiguration Transform { get; set; } = new();
    public RadarRangeConfiguration Range { get; set; } = new();
    public RadarClusteringConfiguration Clustering { get; set; } = new();
    public RadarCalibrationConfiguration Calibration { get; set; } = new();
    public RadarPixelRect OutputRectPixels { get; set; } = new(0, 0, 1920, 1080);
    public string ReplayFilePath { get; set; } = string.Empty;
    public double ReplaySpeed { get; set; } = 1d;
    public bool ReplayLoop { get; set; }
}

public sealed class RadarScreenConfiguration
{
    public string ScreenId { get; set; } = "main";
    public string UnityDisplayName { get; set; } = "Main";
    public bool IsAssociated { get; set; }
    public bool IsPrimary { get; set; } = true;
    public int UnityOrder { get; set; }
    public int UnityDefaultWidthPixels { get; set; } = 1920;
    public int UnityDefaultHeightPixels { get; set; } = 1080;
    public RadarResolutionMode ResolutionMode { get; set; }
    public int WidthPixels { get; set; } = 1920;
    public int HeightPixels { get; set; } = 1080;
    public RadarFusionConfiguration Fusion { get; set; } = new();
    public RadarScreenTrackingConfiguration Tracking { get; set; } = new();
    public RadarInteractionConfiguration Interaction { get; set; } = new();
    public List<RadarSensorConfiguration> Sensors { get; set; } = [new()];
    public int EffectiveWidthPixels => ResolutionMode == RadarResolutionMode.Override ? WidthPixels : UnityDefaultWidthPixels;
    public int EffectiveHeightPixels => ResolutionMode == RadarResolutionMode.Override ? HeightPixels : UnityDefaultHeightPixels;
}

public sealed class RadarAppConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public RadarIpcConfiguration Ipc { get; set; } = new();
    public List<RadarScreenConfiguration> Screens { get; set; } = [new()];
    public static RadarAppConfiguration CreateDefault() => new();
}
```

`RadarScreenTrackingConfiguration.FromLegacy` copies `ConfirmFrames`, `LostFrames` and `SmoothingAlpha`, sets `MaximumAssociationDistancePixels=160`, and returns a migration warning explaining that the legacy physical-meter association distance has no reliable pixel equivalent before screen calibration.

`CloneWithId` is an explicit deep clone helper used by UI duplication; it must copy nested configuration values and replace `ScreenId`, while copied sensors receive new deterministic IDs (`sensor-1-copy`, `sensor-2-copy`, with numeric suffix collision handling).

- [ ] **Step 4: Implement Schema 1 detection, one-time backup and atomic Schema 2 save**

```csharp
private static int ReadSchemaVersion(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.TryGetProperty("schemaVersion", out var value) && value.TryGetInt32(out var version)
        ? version
        : 1;
}

private static RadarAppConfiguration MigrateSchemaOne(LegacyRadarAppConfiguration legacy)
{
    var sensor = new RadarSensorConfiguration
    {
        SensorId = "sensor-1",
        DisplayName = "雷达 1",
        Device = legacy.Device,
        Transform = legacy.Transform,
        Range = legacy.Range,
        Clustering = legacy.Clustering,
        Calibration = legacy.Calibration,
        OutputRectPixels = new RadarPixelRect(0, 0, 1920, 1080)
    };
    return new RadarAppConfiguration
    {
        SchemaVersion = 2,
        Ipc = legacy.Ipc,
        Screens = [new RadarScreenConfiguration
        {
            ScreenId = "main",
            UnityDisplayName = "Main",
            IsPrimary = true,
            Tracking = RadarScreenTrackingConfiguration.FromLegacy(legacy.Tracking),
            Interaction = legacy.Interaction,
            Sensors = [sensor]
        }]
    };
}
```

In `LoadAsync`, before replacing a Schema 1 file, copy the original bytes to `config.schema1.<yyyyMMddHHmmssfff>.bak`, call `SaveAsync` for the migrated document, and return the migrated object. `SaveAsync` continues to write `<path>.tmp` then uses `File.Move(..., overwrite: true)`.

- [ ] **Step 5: Enforce every approved validation rule**

Implement exact checks:

```text
screenId: ^[a-z0-9_-]{1,64}$, unique ordinal-ignore-case
sensorId: same pattern, unique within its screen
effective width/height: 1..32768
when no screen is associated before Hello, zero associated primary screens are allowed; when any screen is associated, exactly one associated primary screen is required
output rect: width/height > 0, X/Y >= 0, right <= effective width, top <= effective height
outputRateHz: 1..120
sensorDataMaxAgeMilliseconds: 10..5000
fusionDistancePixels and maximumAssociationDistancePixels: finite and > 0
confirm/lost frames: 1..120; smoothingAlpha: (0,1]
real sensor IP valid; port 1..65535; source enum defined
replay speed: finite and 0.1..8.0; a Replay sensor may be saved without a file, but Start is disabled until ReplayFilePath exists
all existing range, angle, dead-zone, cluster and calibration checks applied per sensor
```

Warnings may normalize model-dependent maximum distance and visualization range; duplicate IDs and rectangle overflow remain errors and prevent saving.

- [ ] **Step 6: Convert both shipped profiles to Schema 2 and rerun tests**

```powershell
dotnet test tests/Radar.Configuration.Tests/Radar.Configuration.Tests.csproj -c Release
```

Expected: all configuration tests pass; both profile JSON files load with `schemaVersion: 2`, one `main` screen and one full-screen sensor.

- [ ] **Step 7: Commit Schema 2**

```powershell
git add src/Radar.Configuration tests/Radar.Configuration.Tests config
git commit -m "feat: add multi-screen configuration schema"
```

### Task 3: Add output mapping and deterministic screen fusion

**Files:**
- Create: `src/Radar.Processing/RadarOutputMapper.cs`
- Create: `src/Radar.Processing/RadarScreenFusionEngine.cs`
- Test: `tests/Radar.Processing.Tests/RadarOutputMapperTests.cs`
- Test: `tests/Radar.Processing.Tests/RadarScreenFusionEngineTests.cs`

**Interfaces:**
- Consumes: `RadarPixelRect`, `RadarScreenPointer`, `RadarPointerPhase` and a processing-layer `RadarScreenFusionOptions` value mapped from configuration by Bridge.
- Produces: `RadarOutputMapper.Map`, `SensorDetectionFrame`, `RadarScreenFusionEngine.Publish`, `Tick`, and `Reset`.

- [ ] **Step 1: Write failing mapper and fusion behavior tests**

```csharp
[Fact]
public void Map_UsesBottomLeftPixelRectangle()
{
    var mapper = new RadarOutputMapper(new RadarPixelRect(2048, 0, 2048, 1536), 4096, 1536);
    var mapped = mapper.Map(0.5f, 0.25f);
    Assert.Equal(3072f, mapped.PixelX);
    Assert.Equal(384f, mapped.PixelY);
    Assert.Equal(0.75f, mapped.NormalizedX);
    Assert.Equal(0.25f, mapped.NormalizedY);
}

[Fact]
public void Tick_MergesOnlyCrossSensorDetectionsWithinThreshold()
{
    var engine = CreateEngine(fusionDistancePixels: 80f, confirmFrames: 1);
    var now = DateTimeOffset.UnixEpoch;
    engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 1000, 500, 1f)]));
    engine.Publish(new SensorDetectionFrame("f2", now, [new(1, 1040, 520, 0.8f), new(2, 1080, 520, 0.7f)]));

    var result = engine.Tick(now);

    Assert.Equal(2, result.Targets.Count);
    Assert.Contains(result.Targets, target => target.SourceSensorCount == 2 && target.PixelX == 1020f);
}

[Fact]
public void Tick_PreservesPointerAcrossSensorHandoffAndEmitsOneUpAfterLoss()
{
    var engine = CreateEngine(fusionDistancePixels: 80f, confirmFrames: 1, lostFrames: 2);
    var now = DateTimeOffset.UnixEpoch;
    engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 900, 500, 1f)]));
    var down = Assert.Single(engine.Tick(now).Pointers);
    engine.Publish(new SensorDetectionFrame("f2", now.AddMilliseconds(33), [new(1, 940, 500, 1f)]));
    var move = Assert.Single(engine.Tick(now.AddMilliseconds(33)).Pointers);
    var empty = engine.Tick(now.AddMilliseconds(200));
    var up = Assert.Single(engine.Tick(now.AddMilliseconds(233)).Pointers);
    Assert.Equal(down.PointerId, move.PointerId);
    Assert.Empty(empty.Pointers);
    Assert.Equal(RadarPointerPhase.Up, up.Phase);
    Assert.Empty(engine.Tick(now.AddMilliseconds(266)).Pointers);
}
```

- [ ] **Step 2: Run processing tests and verify missing types fail compilation**

```powershell
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release --filter "FullyQualifiedName~RadarOutputMapperTests|FullyQualifiedName~RadarScreenFusionEngineTests"
```

Expected: compilation fails because mapper/fusion types are absent.

- [ ] **Step 3: Implement the mapper with finite and bounds guards**

```csharp
public readonly record struct MappedScreenPoint(float PixelX, float PixelY, float NormalizedX, float NormalizedY);

public sealed class RadarOutputMapper
{
    private readonly RadarPixelRect _rect;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    public RadarOutputMapper(RadarPixelRect rect, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || rect.Width <= 0 || rect.Height <= 0 ||
            rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > screenWidth || rect.Y + rect.Height > screenHeight)
            throw new ArgumentOutOfRangeException(nameof(rect));
        _rect = rect;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public MappedScreenPoint Map(float localNormalizedX, float localNormalizedY)
    {
        if (!float.IsFinite(localNormalizedX) || !float.IsFinite(localNormalizedY))
            throw new ArgumentOutOfRangeException(nameof(localNormalizedX));
        var pixelX = _rect.X + Math.Clamp(localNormalizedX, 0f, 1f) * _rect.Width;
        var pixelY = _rect.Y + Math.Clamp(localNormalizedY, 0f, 1f) * _rect.Height;
        return new(pixelX, pixelY, pixelX / _screenWidth, pixelY / _screenHeight);
    }
}
```

- [ ] **Step 4: Implement deterministic fusion, screen tracking and reset semantics**

```csharp
public readonly record struct SensorDetection(int DetectionId, float PixelX, float PixelY, float Confidence);
public sealed record SensorDetectionFrame(string SensorId, DateTimeOffset Timestamp, IReadOnlyList<SensorDetection> Detections);
public sealed record FusedScreenTarget(int TrackId, float PixelX, float PixelY, float Confidence, int SourceSensorCount, bool IsConfirmed);
public sealed record RadarScreenFusionResult(IReadOnlyList<FusedScreenTarget> Targets, IReadOnlyList<RadarScreenPointer> Pointers);

public sealed class RadarScreenFusionOptions
{
    public required RadarScreenInfo Screen { get; init; }
    public int SensorDataMaxAgeMilliseconds { get; init; } = 150;
    public float FusionDistancePixels { get; init; } = 80f;
    public float MaximumAssociationDistancePixels { get; init; } = 160f;
    public int ConfirmFrames { get; init; } = 2;
    public int LostFrames { get; init; } = 3;
    public float SmoothingAlpha { get; init; } = 0.5f;
    public RadarInteractionMode InteractionMode { get; init; } = RadarInteractionMode.Touch;
    public int DwellMilliseconds { get; init; } = 800;
    public float DwellRadiusNormalized { get; init; } = 0.03f;
    public float DragThresholdNormalized { get; init; } = 0.015f;
    public int MinimumPressMilliseconds { get; init; } = 30;
    public float MaximumClickMovementNormalized { get; init; } = 0.03f;
}

public interface IRadarScreenFusionEngine
{
    public void Publish(SensorDetectionFrame frame);
    public RadarScreenFusionResult Tick(DateTimeOffset timestamp);
    public IReadOnlyList<RadarScreenPointer> Reset(DateTimeOffset timestamp);
}
```

`RadarScreenFusionEngine` implements this interface and has constructor `RadarScreenFusionEngine(RadarScreenFusionOptions options)`; validate the same finite/range rules as configuration so invalid programmatic use fails immediately.

Implement `Tick` in this exact order: discard frames older than `SensorDataMaxAge`; sort detections by `SensorId`, then X, then Y; sort cross-sensor candidate pairs by distance; greedily add candidates while a fusion group contains at most one detection from each sensor; average coordinates and confidence; associate fused targets to existing tracks by shortest distance within `MaximumAssociationDistancePixels`; smooth matched tracks; confirm after `ConfirmFrames`; remove after `LostFrames`; feed confirmed normalized targets to the existing interaction semantics. `Reset` returns one `Up` for every pressed Touch pointer, clears tracks/latest frames, and a second call returns an empty list.

- [ ] **Step 5: Add edge cases and run the complete processing suite**

Add tests for three-sensor groups, same-sensor close points staying separate, stale frame removal, stable sort on equal distances, smoothing, Dwell/HoverOnly/EnterTrigger, and reset idempotence. Then run:

```powershell
dotnet test tests/Radar.Processing.Tests/Radar.Processing.Tests.csproj -c Release
```

Expected: all existing physical-coordinate tests and all new pixel-space fusion tests pass.

- [ ] **Step 6: Commit the processing engine**

```powershell
git add src/Radar.Processing tests/Radar.Processing.Tests
git commit -m "feat: fuse multi-radar screen detections"
```

### Task 4: Extract an isolated pipeline per radar sensor

**Files:**
- Create: `src/Radar.Bridge.Wpf/Services/IRadarSensorPipeline.cs`
- Create: `src/Radar.Bridge.Wpf/Services/RadarSensorPipeline.cs`
- Create: `src/Radar.Bridge.Wpf/Services/RadarSensorPipelineFactory.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/RadarSensorPipelineTests.cs`
- Reference: `src/Radar.Bridge.Wpf/Services/RadarBridgeRuntime.cs` for the existing connection/simulation/replay/recording code paths.

**Interfaces:**
- Consumes: one `RadarSensorConfiguration`, effective screen width/height, `RadarConnectionService`, recording reader/writer, parser/filter/cluster/calibration classes and `RadarOutputMapper`.
- Produces: isolated `IRadarSensorPipeline` with `SnapshotUpdated`, `DetectionFrameUpdated`, state/log events and source-specific lifecycle commands.

- [ ] **Step 1: Write failing isolation, latest-frame and mapping tests**

```csharp
[Fact]
public async Task TwoSimulationPipelines_RunAndStopIndependently()
{
    await using var first = CreatePipeline("front", "f1", new RadarPixelRect(0, 0, 2048, 1536));
    await using var second = CreatePipeline("front", "f2", new RadarPixelRect(2048, 0, 2048, 1536));
    var firstFrame = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondFrame = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    first.DetectionFrameUpdated += value => firstFrame.TrySetResult(value);
    second.DetectionFrameUpdated += value => secondFrame.TrySetResult(value);

    await first.StartAsync();
    await second.StartAsync();
    await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await first.StopAsync();
    var stillRunning = await secondFrame.Task.WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Equal("f2", stillRunning.SensorId);
    Assert.Equal(RadarSensorRuntimeState.Running, second.State);
}

[Fact]
public async Task ProcessingChannel_DropsOldFramesInsteadOfAccumulatingLatency()
{
    await using var pipeline = CreatePipeline("main", "sensor-1", new RadarPixelRect(0, 0, 1920, 1080));
    pipeline.InjectScanForTest(Frame(sequence: 1));
    pipeline.InjectScanForTest(Frame(sequence: 2));
    pipeline.InjectScanForTest(Frame(sequence: 3));
    var snapshot = await pipeline.WaitForSnapshotForTestAsync();
    Assert.Equal(3, snapshot.Sequence);
    Assert.True(pipeline.DroppedInputFrameCount >= 2);
}
```

- [ ] **Step 2: Run the WPF runtime tests and verify missing pipeline types fail**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarSensorPipelineTests"
```

Expected: compilation fails because the isolated pipeline interface and implementation do not exist.

- [ ] **Step 3: Define the pipeline boundary and immutable snapshots**

```csharp
public enum RadarSensorRuntimeState { Stopped, Starting, Running, Reconnecting, Faulted }

public sealed record RadarSensorRuntimeSnapshot(
    string ScreenId,
    string SensorId,
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> RawPoints,
    IReadOnlyList<RadarPoint> ValidPoints,
    IReadOnlyList<RadarCluster> Clusters,
    IReadOnlyList<SensorDetection> Detections,
    double ScanFrequencyHz,
    double ReceivedBytesPerSecond,
    long CrcErrorCount,
    long DiscardedByteCount,
    long DroppedInputFrameCount);

public interface IRadarSensorPipeline : IAsyncDisposable
{
    string ScreenId { get; }
    string SensorId { get; }
    RadarSensorRuntimeState State { get; }
    long DroppedInputFrameCount { get; }
    event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
    event Action<SensorDetectionFrame>? DetectionFrameUpdated;
    event Action<RadarSensorRuntimeState>? StateChanged;
    event Action<string>? LogReceived;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task StartRecordingAsync(string path, CancellationToken cancellationToken = default);
    Task StopRecordingAsync();
    Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default);
    void PauseReplay();
    void ResumeReplay();
    void StepReplay();
    Task StopReplayAsync();
}
```

`RadarSensorPipelineFactory.Create(screen, sensor)` must construct the real class and is the only production creation path used by the Coordinator. Tests may inject a fake factory.

- [ ] **Step 4: Move the current single-source code into the pipeline without changing algorithms**

For each pipeline, create a bounded `Channel<RadarScanFrame>` with `Capacity=1`, `SingleReader=true`, `FullMode=DropOldest`. `StartAsync` switches only on the configured source:

```csharp
switch (_sensor.SourceMode)
{
    case RadarSensorSourceMode.Real:
        _sourceTask = RunConnectionAsync(_sourceCancellation.Token);
        break;
    case RadarSensorSourceMode.Simulation:
        _sourceTask = GenerateSimulationAsync(_sourceCancellation.Token);
        break;
    case RadarSensorSourceMode.Replay:
        if (string.IsNullOrWhiteSpace(_sensor.ReplayFilePath) || !File.Exists(_sensor.ReplayFilePath))
            throw new FileNotFoundException("Select an existing .radarrec file before starting Replay.", _sensor.ReplayFilePath);
        _sourceTask = ReplayAsync(_sensor.ReplayFilePath, _sensor.ReplaySpeed, _sensor.ReplayLoop, _sourceCancellation.Token);
        break;
    default:
        throw new InvalidOperationException($"Unsupported source mode {_sensor.SourceMode}.");
}
```

The processing loop must preserve the current order: coordinate conversion → transform → range/angle/active/mask/dead-zone filtering → sequential clustering → physical tracking/calibration to local 0–1 → `RadarOutputMapper` to screen pixels. Publish detections with cluster index as `DetectionId`; never publish non-finite or out-of-range mapped points.

- [ ] **Step 5: Preserve recording/replay and tag every log**

Recording captures raw TCP bytes only for a Real source; Replay reads the selected sensor's configured `.radarrec`; Simulation cannot start recording. Every emitted message must be wrapped as:

```csharp
private void PublishLog(string message)
{
    InvokeSafely(LogReceived, $"[{ScreenId}/{SensorId}] {message}");
}
```

Source exceptions set only this pipeline to `Faulted` or `Reconnecting`; they are reported through `LogReceived` and do not cancel a shared token owned by another sensor.

- [ ] **Step 6: Run pipeline and legacy runtime tests**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarSensorPipelineTests|FullyQualifiedName~RadarReplayGateTests|FullyQualifiedName~RadarBridgeRuntimeTests"
```

Expected: isolated pipeline tests pass and all reusable single-radar processing/recording assertions remain green. Tests tied specifically to the old coordinator may be moved in Task 5, but must not be deleted without an equivalent assertion.

- [ ] **Step 7: Commit the sensor pipeline extraction**

```powershell
git add src/Radar.Bridge.Wpf/Services tests/Radar.Bridge.Wpf.Tests
git commit -m "refactor: isolate each radar sensor pipeline"
```

### Task 5: Build the screen-centric Coordinator and topology handshake

**Files:**
- Create: `src/Radar.Bridge.Wpf/Services/RadarBridgeCoordinator.cs`
- Create: `src/Radar.Bridge.Wpf/BridgeVersion.cs`
- Modify: `src/Radar.Bridge.Wpf/Services/IRadarBridgeRuntime.cs`
- Modify: `src/Radar.Ipc/RadarPipeServer.cs`
- Modify: `src/Radar.Bridge.Wpf/App.xaml.cs`
- Delete: `src/Radar.Bridge.Wpf/Services/RadarBridgeRuntime.cs`
- Test: `tests/Radar.Ipc.Tests/RadarPipeServerTests.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/RadarBridgeCoordinatorTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarBridgeRuntimeTests.cs` by moving still-valid assertions to Coordinator/pipeline tests, then delete the obsolete file.

**Interfaces:**
- Consumes: Schema 2, `IRadarSensorPipeline` factory, `RadarScreenFusionEngine`, `RadarPipeServer`, IPC v2 contracts.
- Produces: `IRadarBridgeRuntime` multi-screen operations, validated Hello reconciliation, one `PointerBatch` per output tick.

- [ ] **Step 1: Write failing Hello validation and three-screen/four-sensor Coordinator tests**

```csharp
[Fact]
public async Task Server_RejectsDuplicateScreenIdsBeforeAuthentication()
{
    var response = await ConnectAndSendHelloAsync([
        Screen("front", true, 4096, 1536),
        Screen("front", false, 1920, 1080)
    ]);
    Assert.Equal(IpcMessageType.Error, response.MessageType);
    Assert.Equal("invalid_screen_topology", response.DeserializePayload<ErrorPayload>().Code);
}

[Fact]
public async Task Coordinator_PublishesAllEnabledScreensAndMergesFrontOverlap()
{
    await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), FakePipelineFactory());
    await coordinator.StartInfrastructureAsync();
    await coordinator.ApplyUnityTopologyAsync(Hello(
        Screen("left", false, 1920, 1440),
        Screen("front", true, 4096, 1536),
        Screen("right", false, 1920, 1440)));
    coordinator.PublishForTest("front", "f1", Detection(1000, 700));
    coordinator.PublishForTest("front", "f2", Detection(1040, 700));

    var batch = coordinator.TickForTest(DateTimeOffset.UnixEpoch);

    Assert.Equal(["left", "front", "right"], batch.Screens.Select(frame => frame.Screen.ScreenId));
    Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
    Assert.Empty(batch.Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
}
```

- [ ] **Step 2: Run focused server/Coordinator tests and confirm they fail**

```powershell
dotnet test tests/Radar.Ipc.Tests/Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~RadarPipeServerTests"
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarBridgeCoordinatorTests"
```

Expected: tests fail because Hello currently acknowledges before topology reconciliation and the Coordinator does not exist.

- [ ] **Step 3: Make Hello authentication an awaited decision**

```csharp
public sealed record HelloAuthenticationResult(HelloAckPayload? Ack, ErrorPayload? Error)
{
    public bool Accepted => Ack is not null && Error is null;
    public static HelloAuthenticationResult Accept(HelloAckPayload ack) => new(ack, null);
    public static HelloAuthenticationResult Reject(string code, string message) => new(null, new ErrorPayload(code, message));
}

public sealed class RadarPipeServerOptions
{
    public string PipeName { get; set; } = "Yuexin.RadarBridge";
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public Func<HelloPayload, CancellationToken, ValueTask<HelloAuthenticationResult>> AuthenticateHelloAsync { get; set; }
        = (hello, token) => ValueTask.FromResult(HelloAuthenticationResult.Reject("hello_handler_missing", "Hello handler is not configured."));
}
```

`HandleClientAsync` must validate protocol 2, deserialize Hello, await `AuthenticateHelloAsync`, send `Error` and close on rejection, or send the returned Ack and only then raise `ClientConnected`.

- [ ] **Step 4: Define the multi-screen runtime API**

```csharp
public sealed record RadarScreenRuntimeSnapshot(
    RadarScreenInfo Screen,
    IReadOnlyList<RadarSensorRuntimeSnapshot> Sensors,
    IReadOnlyList<FusedScreenTarget> Targets,
    IReadOnlyList<RadarScreenPointer> Pointers,
    long Sequence,
    DateTimeOffset Timestamp);

public sealed record UnityClientStatus(
    bool IsConnected,
    int ProcessId,
    string UnityVersion,
    IReadOnlyList<RadarScreenInfo> Screens,
    DateTimeOffset? LastBatchSentAt,
    long LastBatchSequence,
    string? LastError);

public interface IRadarBridgeRuntime : IAsyncDisposable
{
    event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated;
    event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated;
    event Action<string>? LogReceived;
    event Action<UnityClientStatus>? UnityStatusChanged;
    UnityClientStatus UnityStatus { get; }
    Task StartInfrastructureAsync(CancellationToken cancellationToken = default);
    Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default);
    Task DisconnectSensorAsync(string screenId, string sensorId);
    Task ConnectScreenAsync(string screenId, CancellationToken cancellationToken = default);
    Task DisconnectScreenAsync(string screenId);
    Task ConnectAllAsync(CancellationToken cancellationToken = default);
    Task DisconnectAllAsync();
    Task StartAllSimulationAsync(CancellationToken cancellationToken = default);
    Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default);
    Task StopRecordingAsync(string screenId, string sensorId);
    Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default);
    void PauseReplay(string screenId, string sensorId);
    void ResumeReplay(string screenId, string sensorId);
    void StepReplay(string screenId, string sensorId);
    Task StopReplayAsync(string screenId, string sensorId);
    Task ApplyConfigurationAsync(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Reconcile Unity topology and own one fusion engine per associated screen**

Use a `Dictionary<string, ScreenRuntime>` with `StringComparer.OrdinalIgnoreCase`. The Hello handler must:

1. reject empty/duplicate/invalid IDs, invalid dimensions, zero or multiple primary screens;
2. update name/order/default resolution for matched screens;
3. create an empty associated configuration for new screen IDs;
4. for IDs absent from Hello, call `fusion.Reset(now)`, queue returned Up events followed by a zero frame, stop their sensors and mark them unassociated;
5. rebuild mapper/fusion instances when effective resolution changes;
6. save Schema 2 atomically;
7. return Ack with effective screen info ordered by Unity `Order` then `ScreenId`.

Coordinator sensor event handlers store only the latest `SensorDetectionFrame` and forward it to the owning fusion engine. One sensor exception is logged as `[screen/sensor]` and never cancels the Coordinator lifetime.

- [ ] **Step 6: Publish deterministic batches at each screen's configured rate**

Use one 1 ms Coordinator scheduler and per-screen `nextDue` timestamps. At a screen's due time, call `Tick`; cache its latest frame. Whenever at least one screen ticks, publish a `PointerBatchPayload` containing every associated screen ordered by topology, using the cached frame or a zero frame. Increment one global batch sequence and log:

```csharp
PublishLog($"[IPC] batch={sequence} screens={frames.Count} pointers={frames.Sum(f => f.Pointers.Count)} latencyMs={latency.TotalMilliseconds:0.0}");
```

The first batch after Hello must include every screen even when all are empty.

- [ ] **Step 7: Run all .NET tests and remove the obsolete runtime only when green**

Before deleting the old Runtime file, move its current `BridgeVersion.Value` into `src/Radar.Bridge.Wpf/BridgeVersion.cs`; keep the value at `1.1.5` until the release alignment in Task 14.

```powershell
dotnet test RadarControl.sln -c Release
```

Expected: all projects pass; no source or test references `RadarBridgeRuntime`; DI resolves `IRadarBridgeRuntime` to `RadarBridgeCoordinator`.

- [ ] **Step 8: Commit the Coordinator**

```powershell
git add src/Radar.Bridge.Wpf src/Radar.Ipc tests/Radar.Bridge.Wpf.Tests tests/Radar.Ipc.Tests
git commit -m "feat: coordinate multi-screen radar runtimes"
```

### Task 6: Add screen and sensor view models with safe command routing

**Files:**
- Create: `src/Radar.Bridge.Wpf/ViewModels/ScreenItemViewModel.cs`
- Create: `src/Radar.Bridge.Wpf/ViewModels/SensorItemViewModel.cs`
- Modify: `src/Radar.Bridge.Wpf/ViewModels/MainViewModel.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/MainViewModelTests.cs`

**Interfaces:**
- Consumes: Schema 2 objects and the exact `IRadarBridgeRuntime` methods from Task 5.
- Produces: selected screen/sensor collections, per-scope commands, screen/sensor properties, filtered bounded log entries.

- [ ] **Step 1: Write failing selection, command-target and bounded-log tests**

```csharp
[Fact]
public async Task ConnectSensorCommand_TargetsCurrentScreenAndSensor()
{
    var runtime = new FakeRadarBridgeRuntime();
    var viewModel = CreateViewModel(ThreeScreenFourSensorConfiguration(), runtime);
    viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
    viewModel.SelectedSensor = viewModel.SelectedScreen.Sensors.Single(sensor => sensor.SensorId == "f2");

    await ExecuteAsync(viewModel.ConnectSensorCommand);

    Assert.Equal(("front", "f2"), runtime.LastConnectedSensor);
}

[Fact]
public void LogFilter_KeepsLatestFiveHundredMatchingEntries()
{
    var viewModel = CreateViewModel(ThreeScreenFourSensorConfiguration(), new FakeRadarBridgeRuntime());
    viewModel.SelectedLogScreenId = "front";
    for (var index = 0; index < 650; index++) viewModel.ReceiveLogForTest($"[front/f1] event {index}");
    for (var index = 0; index < 20; index++) viewModel.ReceiveLogForTest($"[left/l1] event {index}");
    Assert.Equal(500, viewModel.VisibleLogEntries.Count);
    Assert.All(viewModel.VisibleLogEntries, entry => Assert.Contains("[front/", entry, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 2: Run MainViewModel tests and confirm the old flat model fails**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~MainViewModelTests"
```

Expected: compilation fails on screen/sensor collections and targeted commands.

- [ ] **Step 3: Implement focused item view models**

`ScreenItemViewModel` exposes `ScreenId`, read-only Unity name/order/associated/primary state, editable resolution mode/width/height, fusion/tracking/interaction properties, `ObservableCollection<SensorItemViewModel> Sensors`, and derived `EffectiveResolutionText`, `OnlineSensorCount`, `HasValidationErrors`.

`SensorItemViewModel` exposes `SensorId`, display name, enabled/source/model/IP/port/NIC, transform/range/region/mask/cluster/calibration/output rectangle properties, runtime state/frequency/error counters, and `HasValidationErrors`. Every setter changes only its owned configuration object and raises derived property notifications.

- [ ] **Step 4: Reduce MainViewModel to orchestration**

```csharp
public ObservableCollection<ScreenItemViewModel> Screens { get; } = [];
public ScreenItemViewModel? SelectedScreen { get; set; }
public SensorItemViewModel? SelectedSensor { get; set; }
public ObservableCollection<string> VisibleLogEntries { get; } = [];
public string SelectedLogScreenId { get; set; } = "*";
public string SelectedLogSensorId { get; set; } = "*";
public ICommand AddSensorCommand { get; }
public ICommand DeleteSensorCommand { get; }
public ICommand DeleteOrphanedScreenConfigurationCommand { get; }
public ICommand RestoreUnityResolutionCommand { get; }
public ICommand ConnectSensorCommand { get; }
public ICommand DisconnectSensorCommand { get; }
public ICommand ConnectScreenCommand { get; }
public ICommand DisconnectScreenCommand { get; }
public ICommand ConnectAllCommand { get; }
public ICommand DisconnectAllCommand { get; }
public ICommand StartAllSimulationCommand { get; }
public ICommand StartReplayCommand { get; }
public ICommand PauseReplayCommand { get; }
public ICommand ResumeReplayCommand { get; }
public ICommand StepReplayCommand { get; }
public ICommand StopReplayCommand { get; }
public ICommand SaveConfigurationCommand { get; }
```

Selecting a screen selects its first sensor or `null`; deleting a running sensor first awaits `DisconnectSensorAsync`; adding chooses the first collision-free `sensor-N`. Active/associated Unity screens have no add/delete command. `DeleteOrphanedScreenConfigurationCommand` is enabled only for an unassociated screen retained after Unity removed its ID and permanently removes that saved configuration after confirmation. `RestoreUnityResolutionCommand` sets `ResolutionMode=FollowUnityDefault`. Replay commands route the selected file/speed/loop only to the selected Replay sensor. Save calls `ConfigurationValidator`; when valid it atomically writes with `RadarConfigurationStore.SaveAsync` and then awaits `IRadarBridgeRuntime.ApplyConfigurationAsync`, which compares screen/sensor configuration fingerprints, emits required Up/zero frames, rebuilds affected mappers/trackers and restarts only affected running sensor pipelines.

- [ ] **Step 5: Route snapshots and logs on the captured UI SynchronizationContext**

Sensor snapshots update only the matching `SensorItemViewModel`; screen snapshots update screen counts and selected screen output; no worker-thread event mutates an ObservableCollection. Store 500 raw log entries in a queue, rebuild `VisibleLogEntries` after filter changes, and preserve warning/error entries regardless of Move-log throttling.

- [ ] **Step 6: Run ViewModel and binding tests**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~MainViewModelTests|FullyQualifiedName~MainWindowBindingTests"
```

Expected: command routing, selection changes, validation, thread dispatch and the 500-entry bound pass.

- [ ] **Step 7: Commit the view models**

```powershell
git add src/Radar.Bridge.Wpf/ViewModels tests/Radar.Bridge.Wpf.Tests
git commit -m "feat: add screen and sensor configuration view models"
```

### Task 7: Rebuild the Bridge UI around screen and sensor scopes

**Files:**
- Create: `src/Radar.Bridge.Wpf/Controls/RadarScreenFusionView.cs`
- Modify: `src/Radar.Bridge.Wpf/Controls/RadarPointCloudView.cs`
- Modify: `src/Radar.Bridge.Wpf/MainWindow.xaml`
- Modify: `src/Radar.Bridge.Wpf/MainWindow.xaml.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/RadarVisualizationLayoutTests.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/ThemeContrastTests.cs`
- Test: `tests/Radar.Bridge.Wpf.Tests/RadarWindowDpiTests.cs`

**Interfaces:**
- Consumes: selected sensor snapshot, selected screen snapshot, item view models and existing dark theme resources.
- Produces: left screen/sensor navigation, center raw/fused views, right scoped parameters, bottom tagged logs without WPF dirty-region artifacts.

- [ ] **Step 1: Write failing XAML structure and fusion-view rendering tests**

```csharp
[Fact]
public void MainWindow_ContainsScreenSensorListsAndScopedParameterTabs()
{
    var xaml = File.ReadAllText(ProjectPath("src/Radar.Bridge.Wpf/MainWindow.xaml"));
    Assert.Contains("ItemsSource=\"{Binding Screens}\"", xaml);
    Assert.Contains("ItemsSource=\"{Binding SelectedScreen.Sensors}\"", xaml);
    Assert.Contains("Header=\"屏幕参数\"", xaml);
    Assert.Contains("Header=\"雷达参数\"", xaml);
    Assert.Contains("controls:RadarScreenFusionView", xaml);
}

[Fact]
public void FusionView_UsesScreenSpaceAndSmallStableMarkers()
{
    var source = File.ReadAllText(ProjectPath("src/Radar.Bridge.Wpf/Controls/RadarScreenFusionView.cs"));
    Assert.Contains("MarkerRadius = 2.5", source);
    Assert.Contains("SnapsToDevicePixels = true", source);
    Assert.DoesNotContain("DispatcherPriority.Render", source);
}
```

- [ ] **Step 2: Run WPF layout/theme tests and verify the old layout fails**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarVisualizationLayoutTests|FullyQualifiedName~ThemeContrastTests|FullyQualifiedName~RadarWindowDpiTests"
```

Expected: layout assertions fail because the screen/sensor lists and fusion view are absent.

- [ ] **Step 3: Implement the screen-space fusion control**

The control has dependency properties for `RadarScreenRuntimeSnapshot Snapshot`, `IReadOnlyList<RadarSensorConfiguration> Sensors`, and selected sensor ID. It draws in this order: dark background, full-screen grid, per-sensor `OutputRectPixels` outlines using a stable color derived from `SensorId`, mapped detections as radius 2.5 circles, fused targets as radius 6 rings, Pointers as radius 8 phase-colored rings and ID labels. Convert pixels with:

```csharp
private Point ToView(float pixelX, float pixelY)
{
    var x = pixelX / Math.Max(1, Snapshot!.Screen.WidthPixels) * ActualWidth;
    var y = ActualHeight - pixelY / Math.Max(1, Snapshot.Screen.HeightPixels) * ActualHeight;
    return new Point(x, y);
}
```

Update visuals only on a new screen snapshot or configuration change; do not use a render-priority timer. Reuse frozen brushes/pens and `FormattedText` caching to prevent allocations and flicker.

- [ ] **Step 4: Keep the physical raw-point view stable and scoped**

`RadarPointCloudView` receives only the selected sensor snapshot. Keep the 220 ms persistence buffer and 50 ms normal-priority expiration timer, draw raw dots at radius 1.2 and valid dots at radius 1.8, and remove screen-output targets/rectangles from this control. Region vertex dragging still edits the selected sensor's physical active polygon.

- [ ] **Step 5: Replace MainWindow with the approved three-column workspace**

Use outer rows `Header / WorkArea / Logs / Footer`. WorkArea columns are `280 / 12 / * / 12 / 330`; center has two equal panels titled `区域 1 · 原始雷达数据` and `区域 2 · 屏幕融合输出`; right uses a `TabControl` with `屏幕参数` and `雷达参数`. Bind screen resolution, fusion, tracking and interaction only in the screen tab; bind connection/model/transform/range/region/mask/cluster/calibration/output rectangle only in the sensor tab. Disable sensor controls when no sensor is selected and show a clear empty state.

- [ ] **Step 6: Preserve the projection-computer rendering safeguards**

Keep `RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly` before any WPF window is created, `UseLayoutRounding=true`, `SnapsToDevicePixels=true`, opaque backgrounds for the root/cards/log surface, no blur/drop-shadow effects, no transparent overlapping ScrollViewers, and no read-only TwoWay bindings. Add theme tests requiring at least a 4.5:1 contrast ratio for all normal text brushes against their panel backgrounds.

- [ ] **Step 7: Run all WPF tests and launch a manual simulation smoke check**

```powershell
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
dotnet run --project src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj -c Release -- --profile config/default-profile.json
```

Expected: all WPF tests pass; the window shows screen/sensor lists, two readable center views and scoped parameters; controls remain visible and sharp while clicking/resizing. Close the manual process after checking.

- [ ] **Step 8: Commit the Bridge UI**

```powershell
git add src/Radar.Bridge.Wpf tests/Radar.Bridge.Wpf.Tests
git commit -m "feat: add screen-centric RadarBridge workspace"
```

### Task 8: Add arbitrary screen topology to Unity settings and build validation

**Files:**
- Create: `UnityPackage/com.blaze.radar/Runtime/RadarScreenDefinition.cs`
- Create: `UnityPackage/com.blaze.radar/Runtime/RadarScreenTopologyValidator.cs`
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarRuntimeSettings.cs`
- Modify: `UnityPackage/com.blaze.radar/Editor/RadarSettingsProvider.cs`
- Modify: `UnityPackage/com.blaze.radar/Editor/RadarBuildProcessor.cs`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarScreenTopologyValidatorTests.cs`
- Test: `tests/Radar.Unity.Compatibility.Tests/PackageIdentityTests.cs`

**Interfaces:**
- Consumes: Unity serialization, Project Settings provider and existing Bridge copy processor.
- Produces: `RadarScreenDefinition`, `RadarRuntimeSettings.Screens`, `PrimaryScreen`, topology validation shared by Inspector and build.

- [ ] **Step 1: Write failing Unity topology validation tests**

```csharp
[Test]
public void Validate_AcceptsArbitraryOrderedScreenCount()
{
    var screens = new List<RadarScreenDefinition>
    {
        Screen("left", 1920, 1440, false, 0),
        Screen("front", 4096, 1536, true, 1),
        Screen("right", 1920, 1440, false, 2),
        Screen("floor", 3840, 2160, false, 3)
    };
    Assert.That(RadarScreenTopologyValidator.Validate(screens).IsValid, Is.True);
}

[TestCase("", "Screen ID is required")]
[TestCase("Front Wall", "lowercase letters")]
public void Validate_RejectsInvalidId(string id, string expected)
{
    var result = RadarScreenTopologyValidator.Validate(new[] { Screen(id, 1920, 1080, true, 0) });
    Assert.That(result.Errors[0], Does.Contain(expected));
}

[Test]
public void Validate_RequiresExactlyOneEnabledPrimary()
{
    var none = RadarScreenTopologyValidator.Validate(new[] { Screen("main", 1920, 1080, false, 0) });
    var two = RadarScreenTopologyValidator.Validate(new[]
    {
        Screen("a", 1920, 1080, true, 0), Screen("b", 1920, 1080, true, 1)
    });
    Assert.That(none.IsValid, Is.False);
    Assert.That(two.IsValid, Is.False);
}
```

- [ ] **Step 2: Add serializable topology types with one-screen backward defaults**

```csharp
[Serializable]
public sealed class RadarScreenDefinition
{
    [SerializeField] private string screenId = "main";
    [SerializeField] private string displayName = "Main";
    [SerializeField, Min(1)] private int defaultWidthPixels = 1920;
    [SerializeField, Min(1)] private int defaultHeightPixels = 1080;
    [SerializeField] private bool enabled = true;
    [SerializeField] private bool isPrimary = true;
    [SerializeField] private int order;

    public string ScreenId => screenId;
    public string DisplayName => displayName;
    public int DefaultWidthPixels => Mathf.Max(1, defaultWidthPixels);
    public int DefaultHeightPixels => Mathf.Max(1, defaultHeightPixels);
    public bool Enabled => enabled;
    public bool IsPrimary => isPrimary;
    public int Order => order;

    public RadarScreenDefinition() { }

    public RadarScreenDefinition(string id, string name, int width, int height, bool isEnabled, bool primary, int sortOrder)
    {
        screenId = id;
        displayName = name;
        defaultWidthPixels = width;
        defaultHeightPixels = height;
        enabled = isEnabled;
        isPrimary = primary;
        order = sortOrder;
    }
}

public sealed class RadarScreenTopologyValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new List<string>();
}
```

Validator rules must match Bridge: enabled ID regex `^[a-z0-9_-]{1,64}$`, ordinal-ignore-case uniqueness, dimensions 1–32768, and exactly one enabled primary. Disabled definitions remain serialized but are omitted from Hello.

- [ ] **Step 3: Extend settings while preserving existing serialized fields**

```csharp
[Header("Screens")]
[SerializeField] private List<RadarScreenDefinition> screens = new List<RadarScreenDefinition>();

public IReadOnlyList<RadarScreenDefinition> Screens
{
    get
    {
        EnsureDefaultScreen();
        return screens;
    }
}

public RadarScreenDefinition PrimaryScreen
{
    get
    {
        EnsureDefaultScreen();
        return screens.Find(value => value.Enabled && value.IsPrimary);
    }
}

private void EnsureDefaultScreen()
{
    if (screens == null) screens = new List<RadarScreenDefinition>();
    if (screens.Count == 0) screens.Add(new RadarScreenDefinition());
}
```

Do not rename existing Bridge/connection/input serialized fields, so projects upgrading from 1.1.5 retain their values.

- [ ] **Step 4: Replace the generic Settings iterator with a reorderable screen editor**

Render existing settings normally, then a `UnityEditorInternal.ReorderableList` for `screens` with Add, Duplicate, Remove and drag sorting. On duplicate, generate collision-free IDs (`<id>-copy`, `<id>-copy-2`). Display all validator errors in `HelpBox(MessageType.Error)` and expose a `Set Primary` action that atomically clears other primary flags.

- [ ] **Step 5: Fail Player builds before copying Bridge when topology is invalid**

Make `RadarBuildProcessor` implement both `IPreprocessBuildWithReport` and `IPostprocessBuildWithReport`:

```csharp
public void OnPreprocessBuild(BuildReport report)
{
    var settings = RadarRuntimeSettings.LoadOrCreateRuntimeDefaults();
    var validation = RadarScreenTopologyValidator.Validate(settings.Screens);
    if (!validation.IsValid)
        throw new BuildFailedException("Blaze Radar screen topology is invalid:\n" + string.Join("\n", validation.Errors));
}
```

Keep the existing post-build resolved-package check, version marker check, full directory replacement and executable SHA-256 comparison.

- [ ] **Step 6: Run Unity topology tests and .NET package identity checks**

At this stage run the .NET compatibility check immediately:

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~PackageIdentityTests"
```

The Unity test itself is run through `scripts/test-unity-package.ps1` after Task 13 creates that runner. Expected final result: all topology cases pass and invalid settings throw `BuildFailedException` before postprocessing.

- [ ] **Step 7: Commit Unity topology settings**

```powershell
git add UnityPackage/com.blaze.radar/Runtime UnityPackage/com.blaze.radar/Editor UnityPackage/com.blaze.radar/Tests tests/Radar.Unity.Compatibility.Tests
git commit -m "feat: configure arbitrary radar screens in Unity"
```

### Task 9: Upgrade the Unity client and dispatcher to IPC v2 public events

**Files:**
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarMessageModels.cs`
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarPipeClient.cs`
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarFrameDispatcher.cs`
- Test: `tests/Radar.Unity.Compatibility.Tests/RadarPipeClientTests.cs`
- Test: `tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarFrameDispatcherTests.cs`

**Interfaces:**
- Consumes: `RadarRuntimeSettings.Screens`, IPC v2 DTO shape and `LatestValueBuffer<T>`.
- Produces: `ScreenPointerReceived`, `ScreenFrameReceived`, `LatestScreenFrames`, and legacy primary-screen `PointerFrameReceived`.

- [ ] **Step 1: Write failing JSON/client and dispatcher event tests**

```csharp
[Fact]
public async Task Client_SendsAllEnabledScreensAndConsumesPointerBatch()
{
    using var server = new FakePipeServer();
    using var client = new RadarPipeClient(server.PipeName, 100, 100);
    client.Start(Hello("left", "front", "right"));
    var hello = await server.ReadHelloAsync();
    Assert.Equal(3, hello.screens.Count);
    await server.SendBatchAsync(Batch(Frame("front", Pointer(7, 0.5f, 0.25f, 2048, 384))));
    Assert.True(SpinWait.SpinUntil(() => client.TryConsumeLatestBatch(out _), 2000));
}
```

```csharp
[UnityTest]
public IEnumerator Dispatcher_FiresPerScreenAndLegacyPrimaryEventsOnMainThread()
{
    var dispatcher = CreateDispatcherForTest(primaryScreenId: "front");
    var screenFrames = new List<string>();
    var points = new List<string>();
    var legacyCount = 0;
    dispatcher.ScreenFrameReceived += frame => screenFrames.Add(frame.screen.screenId);
    dispatcher.ScreenPointerReceived += (screen, pointer) => points.Add(screen.screenId + ":" + pointer.pointerId);
    dispatcher.PointerFrameReceived += _ => legacyCount++;
    dispatcher.InjectBatchForTest(Batch(Frame("left"), Frame("front", Pointer(7))));
    yield return null;
    CollectionAssert.AreEqual(new[] { "left", "front" }, screenFrames);
    CollectionAssert.AreEqual(new[] { "front:7" }, points);
    Assert.AreEqual(1, legacyCount);
}
```

- [ ] **Step 2: Run compatibility tests and verify IPC v1 assumptions fail**

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --filter "FullyQualifiedName~RadarPipeClientTests"
```

Expected: tests fail because models use protocol 1, Hello has one resolution, and the client buffers one old frame.

- [ ] **Step 3: Mirror the v2 JSON models exactly in `Blaze.Radar`**

```csharp
[Serializable] public sealed class RadarScreenDefinitionPayload
{
    public string screenId; public string name; public int defaultWidthPixels; public int defaultHeightPixels; public bool isPrimary; public int order;
}
[Serializable] public sealed class RadarHelloPayload
{
    public int unityProcessId; public string unityVersion; public List<RadarScreenDefinitionPayload> screens = new List<RadarScreenDefinitionPayload>();
}
[Serializable] public sealed class RadarScreenInfo
{
    public string screenId; public string name; public int widthPixels; public int heightPixels; public bool isPrimary; public int order;
}
[Serializable] public sealed class RadarScreenPointer
{
    public int pointerId; public RadarPointerPhase phase; public float normalizedX; public float normalizedY;
    public float pixelX; public float pixelY; public float confidence; public long timestampUnixMilliseconds;
}
[Serializable] public sealed class RadarScreenPointerFrame
{
    public RadarScreenInfo screen; public long sequence; public long timestampUnixMilliseconds;
    public List<RadarScreenPointer> pointers = new List<RadarScreenPointer>();
}
[Serializable] public sealed class RadarPointerBatchPayload
{
    public List<RadarScreenPointerFrame> screens = new List<RadarScreenPointerFrame>();
}
```

Add enum value `PointerBatch = 11`; set `RadarIpcProtocol.Version = 2`; Ack mirrors `bridgeVersion`, `protocolVersion`, `connected`, `capability`, `screens`.

- [ ] **Step 4: Buffer only the latest complete batch and reject v1 explicitly**

Replace `_latestFrame` with `LatestValueBuffer<RadarPointerBatchPayload> _latestBatch`. `TryConsumeLatestBatch` returns one batch. In `HandleEnvelope`, only `PointerBatch` publishes data; receiving `PointerFrame` reports `RadarBridge sent legacy PointerFrame on IPC v2.`. Protocol mismatch remains an error and forces reconnect. `DroppedBatchCount` reports the buffer's dropped count.

- [ ] **Step 5: Build Hello from enabled ordered settings and dispatch safely on Unity main thread**

`RadarFrameDispatcher.Start` maps enabled settings to Hello payload, ordered by `Order` then `ScreenId`. In `Update`, for each screen frame:

```csharp
_latestScreenFrames[frame.screen.screenId] = frame;
InvokeSafely(ScreenFrameReceived, frame);
for (var index = 0; index < frame.pointers.Count; index++)
    InvokeSafely(ScreenPointerReceived, frame.screen, frame.pointers[index]);
if (frame.screen.screenId == settings.PrimaryScreen.ScreenId)
    InvokeSafely(PointerFrameReceived, ToLegacyFrame(frame));
```

Expose exact public API:

```csharp
public event Action<RadarScreenInfo, RadarScreenPointer> ScreenPointerReceived;
public event Action<RadarScreenPointerFrame> ScreenFrameReceived;
public event Action<RadarPointerFrameMessage> PointerFrameReceived;
public IReadOnlyDictionary<string, RadarScreenPointerFrame> LatestScreenFrames => _latestScreenFrames;
public void Connect();
public Task DisconnectAsync();
```

Each subscriber is invoked inside its own try/catch. The legacy adapter copies pointer ID/phase/normalized/confidence/timestamp from only the primary frame.
`Start` calls `Connect()` when `autoConnect` is true; `DisconnectAsync()` stops the client without destroying the Dispatcher so a Sample can switch between local simulation and IPC safely.

- [ ] **Step 6: Link all new pure message files into compatibility tests and run them**

Update the compatibility `.csproj` Compile links if a new pure C# runtime file is required, then run:

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release
```

Expected: v2 Hello, Ack, PointerBatch, reconnect, shutdown, malformed JSON and latest-batch drop tests all pass.

- [ ] **Step 7: Commit Unity IPC v2**

```powershell
git add UnityPackage/com.blaze.radar/Runtime UnityPackage/com.blaze.radar/Tests tests/Radar.Unity.Compatibility.Tests
git commit -m "feat: dispatch multi-screen radar frames in Unity"
```

### Task 10: Route screen points through EventSystem and Cameras

**Files:**
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarInputModule.cs`
- Modify: `UnityPackage/com.blaze.radar/Runtime/RadarPointerState.cs`
- Create: `UnityPackage/com.blaze.radar/Runtime/RadarScreenCameraRouter.cs`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarInputModulePlayModeTests.cs`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarScreenCameraRouterTests.cs`

**Interfaces:**
- Consumes: screen-aware Dispatcher events and `RadarScreenInfo`/`RadarScreenPointer`.
- Produces: optional `RadarInputModule.ScreenId`, native UGUI event flow, `TryGetCamera`, `TryMapToCameraPixel`, `TryCreateRay`, `TryRaycast`.

- [ ] **Step 1: Write failing screen filter, camera pixelRect and RenderTexture tests**

```csharp
[UnityTest]
public IEnumerator InputModule_IgnoresOtherScreensAndClicksSelectedScreenButton()
{
    var fixture = CreateEventSystemFixture(screenId: "front");
    fixture.Module.InjectScreenFrame(Frame("left", Down(1, 0.5f, 0.5f)));
    fixture.Module.InjectScreenFrame(Frame("front", Down(2, 0.5f, 0.5f)));
    fixture.Module.InjectScreenFrame(Frame("front", Up(2, 0.5f, 0.5f)));
    yield return null;
    Assert.AreEqual(1, fixture.ButtonClickCount);
    Assert.False(fixture.Module.HasPointerForTest(1));
}

[Test]
public void Router_MapsLogicalPixelIntoCameraPixelRect()
{
    var camera = CreateCamera(new Rect(100, 50, 800, 600));
    var router = CreateRouter("front", camera);
    Assert.True(router.TryMapToCameraPixel(Screen("front", 4000, 2000), Pointer(2000, 500), out var pixel));
    Assert.AreEqual(new Vector2(500, 200), pixel);
}

[UnityTest]
public IEnumerator Router_RenderTextureRay_HitsBoundWall()
{
    var texture = new RenderTexture(1024, 512, 16);
    var fixture = CreateWallCameraRouter("right", texture);
    Assert.True(fixture.Router.TryRaycast(Screen("right", 1920, 1440), Pointer(960, 720), out var hit));
    Assert.AreEqual(fixture.Wall, hit.collider.gameObject);
    texture.Release();
    yield return null;
}
```

- [ ] **Step 2: Update RadarInputModule to subscribe to one screen frame stream**

Add serialized `screenId`. Empty value resolves to `settings.PrimaryScreen.ScreenId`. Subscribe to `ScreenFrameReceived`; ignore nonmatching frames. Convert each pointer to EventSystem position with normalized coordinates and current player surface:

```csharp
var eventPosition = new Vector2(
    Mathf.Clamp01(pointer.normalizedX) * Screen.width,
    Mathf.Clamp01(pointer.normalizedY) * Screen.height);
ProcessPointer(pointer.pointerId, eventPosition, pointer.phase, Vector2.zero);
```

Keep the exact native sequence already implemented: `eventSystem.RaycastAll`, `HandlePointerExitAndEnter`, Down/Click/BeginDrag/Drag/EndDrag/Drop/Up and Scroll. Key pointer dictionaries by Pointer ID because one module consumes one screen. Mouse debug remains pointer `-1` and follows the same event path.

- [ ] **Step 3: Implement Camera bindings and coordinate conversion**

```csharp
[Serializable]
public sealed class RadarScreenCameraBinding
{
    public string screenId;
    public Camera camera;
    public LayerMask raycastLayerMask = ~0;
    public float maximumRayDistance = 1000f;
}

public bool TryMapToCameraPixel(RadarScreenInfo screen, RadarScreenPointer pointer, out Vector2 pixel)
{
    pixel = default;
    if (!TryGetBinding(screen.screenId, out var binding) || screen.widthPixels <= 0 || screen.heightPixels <= 0) return false;
    var targetWidth = binding.camera.targetTexture != null ? binding.camera.targetTexture.width : binding.camera.pixelRect.width;
    var targetHeight = binding.camera.targetTexture != null ? binding.camera.targetTexture.height : binding.camera.pixelRect.height;
    var originX = binding.camera.targetTexture != null ? 0f : binding.camera.pixelRect.x;
    var originY = binding.camera.targetTexture != null ? 0f : binding.camera.pixelRect.y;
    pixel = new Vector2(
        originX + pointer.pixelX / screen.widthPixels * targetWidth,
        originY + pointer.pixelY / screen.heightPixels * targetHeight);
    return !float.IsNaN(pixel.x) && !float.IsInfinity(pixel.x) &&
           !float.IsNaN(pixel.y) && !float.IsInfinity(pixel.y);
}
```

`TryCreateRay` calls `camera.ScreenPointToRay` with the mapped point. `TryRaycast` uses the binding's maximum distance and layer mask. Reject duplicate/empty binding IDs, null Cameras, nonpositive distances and unknown screens with one throttled warning per error key.

- [ ] **Step 4: Run Unity PlayMode tests**

After Task 13 adds the runner, execute:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform PlayMode
```

Expected: existing Button/Toggle/Slider/2D/3D/drag tests plus screen filtering, pixelRect, independent target display metadata and RenderTexture ray tests pass.

- [ ] **Step 5: Commit EventSystem and Camera routing**

```powershell
git add UnityPackage/com.blaze.radar/Runtime UnityPackage/com.blaze.radar/Tests
git commit -m "feat: route radar screens through Unity cameras"
```

### Task 11: Upgrade Basic Interaction as the primary-screen EventSystem verifier

**Files:**
- Modify: `UnityPackage/com.blaze.radar/Samples~/BasicInteraction/RadarDemoLogger.cs`
- Modify: `UnityPackage/com.blaze.radar/Samples~/BasicInteraction/BasicInteractionPresenter.cs`
- Modify: `UnityPackage/com.blaze.radar/Samples~/BasicInteraction/BasicInteraction.unity`
- Modify: `UnityPackage/com.blaze.radar/Samples~/BasicInteraction/README.md`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarInputModulePlayModeTests.cs`

**Interfaces:**
- Consumes: primary-screen compatibility event, existing `RadarInputModule`, native UGUI/2D/3D probes and mouse debug mode.
- Produces: clear primary-screen status, detailed bounded frame/pointer/EventSystem logs and an immediately testable sample scene.

- [ ] **Step 1: Extend the existing PlayMode fixture with all Basic Interaction event paths**

```csharp
[UnityTest]
public IEnumerator PrimaryScreenFrame_DrivesButtonToggleSliderAndDragInOrder()
{
    var fixture = CreateBasicInteractionFixture();
    fixture.Inject("main", Down(10, fixture.ButtonCenter));
    fixture.Inject("main", Up(10, fixture.ButtonCenter));
    fixture.Inject("main", Down(11, fixture.ToggleCenter));
    fixture.Inject("main", Up(11, fixture.ToggleCenter));
    fixture.Inject("main", Down(12, fixture.SliderStart));
    fixture.Inject("main", Move(12, fixture.SliderEnd));
    fixture.Inject("main", Up(12, fixture.SliderEnd));
    yield return null;
    CollectionAssert.IsSubsetOf(
        new[] { "button.click", "toggle.changed", "slider.changed", "drag.begin", "drag.end" },
        fixture.Events);
}
```

- [ ] **Step 2: Keep the existing scene wiring and add screen-aware status**

Do not replace `EventSystem`, `GraphicRaycaster`, `PhysicsRaycaster`, `Physics2DRaycaster`, UI controls or probe scripts. Bind the Dispatcher to the configured primary screen and display:

```text
IPC CONNECTED / DISCONNECTED
PRIMARY: main · Main · 1920×1080
FRAME: sequence · age ms · pointer count · dropped batches
INPUT: RadarOnly / RadarAndMouseDebug
```

Use opaque dark panels and light text; all normal text must meet the same 4.5:1 contrast rule as Bridge.

- [ ] **Step 3: Subscribe to both the new frame event and native EventSystem probes**

`RadarDemoLogger` subscribes to `ScreenFrameReceived`, filters the primary ID, and keeps `PointerFrameReceived` only as a visible compatibility counter. Each pointer detail line uses:

```csharp
builder.Append('[').Append(frame.screen.screenId).Append("/P").Append(pointer.pointerId).Append("] ")
    .Append(pointer.phase)
    .Append(" normalized=(").Append(pointer.normalizedX.ToString("0.000", CultureInfo.InvariantCulture))
    .Append(", ").Append(pointer.normalizedY.ToString("0.000", CultureInfo.InvariantCulture)).Append(')')
    .Append(" pixel=(").Append(pointer.pixelX.ToString("0.0", CultureInfo.InvariantCulture))
    .Append(", ").Append(pointer.pixelY.ToString("0.0", CultureInfo.InvariantCulture)).Append(')')
    .Append(" confidence=").Append(pointer.confidence.ToString("0.00", CultureInfo.InvariantCulture));
```

Keep at most 200 frame lines and 300 EventSystem lines. Log every Down/Up/error immediately; throttle consecutive Move entries for the same pointer to 10 Hz while always updating the live position panel.

- [ ] **Step 4: Document two exact test paths in the Sample README**

Path A: set `RadarAndMouseDebug`, enter Play Mode, click/drag every UGUI/2D/3D target and confirm native event logs. Path B: start embedded Bridge Simulation for `main`, disable mouse debug, confirm frame/point logs and the same native events. Include the expected screen ID, phase sequence and where to find `Player.log`.

- [ ] **Step 5: Run PlayMode tests and manually inspect the sample**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform PlayMode
```

Expected: native EventSystem tests pass. In the editor, import Basic Interaction from Package Manager, open `BasicInteraction.unity`, enter Play Mode and verify all panels are readable at 1920×1080 and ultrawide aspect ratios.

- [ ] **Step 6: Commit the primary-screen sample**

```powershell
git add UnityPackage/com.blaze.radar/Samples~ UnityPackage/com.blaze.radar/Tests
git commit -m "feat: expand primary screen interaction diagnostics"
```

### Task 12: Add the Multi-Screen Camera Routing sample

**Files:**
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/Blaze.Radar.Sample.MultiScreenCameraRouting.asmdef`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/MultiScreenCameraRouting.unity`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/MultiScreenCameraRoutingPresenter.cs`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/RadarLocalScreenSimulator.cs`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/RadarWorldPointerVisualizer.cs`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/RadarSampleLogPanel.cs`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/RadarPointerParticle.prefab`
- Create: `UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting/README.md`
- Modify: `UnityPackage/com.blaze.radar/package.json`
- Test: `UnityPackage/com.blaze.radar/Tests/Runtime/RadarScreenCameraRouterTests.cs`

**Interfaces:**
- Consumes: Dispatcher screen delegates, `RadarScreenCameraRouter`, Camera/Physics raycast and the SDK message types.
- Produces: LEFT/FRONT/RIGHT reference CAVE, local simulation mode, full IPC mode, pooled world particles and detailed screen/ray logs.

- [ ] **Step 1: Add the sample manifest entry and compile boundary**

```json
{
  "displayName": "Multi-Screen Camera Routing",
  "description": "Three-screen Camera routing, world raycast particles and detailed multi-radar diagnostics.",
  "path": "Samples~/MultiScreenCameraRouting"
}
```

The sample asmdef references only `Blaze.Radar.Runtime`, `Unity.ugui`, and built-in Unity modules; it must not reference Editor assemblies.

- [ ] **Step 2: Implement a deterministic local screen simulator**

```csharp
public sealed class RadarLocalScreenSimulator : MonoBehaviour
{
    public event Action<RadarPointerBatchPayload> BatchGenerated;
    [SerializeField, Min(1f)] private float framesPerSecond = 30f;

    internal RadarPointerBatchPayload BuildBatch(float time, long sequence)
    {
        return new RadarPointerBatchPayload
        {
            screens = new List<RadarScreenPointerFrame>
            {
                MakeFrame("left", "LEFT", 1920, 1440, false, 0, Ellipse(time, 1, 0.5f, 0.5f)),
                MakeFrame("front", "FRONT", 4096, 1536, true, 1, CrossingPair(time)),
                MakeFrame("right", "RIGHT", 1920, 1440, false, 2, Ellipse(time + 1.2f, 1, 0.5f, 0.5f))
            }
        };
    }
}
```

Use IDs scoped per screen; emit Down on first frame, Move while active, Up exactly once before removing a trajectory. The FRONT pair crosses and remains two Pointer IDs, validating multi-pointer visualization without Bridge.

- [ ] **Step 3: Implement pooled `(ScreenId, PointerId)` world effects**

```csharp
private readonly Dictionary<PointerKey, ParticleSystem> _active = new Dictionary<PointerKey, ParticleSystem>();
private readonly Stack<ParticleSystem> _pool = new Stack<ParticleSystem>();

private void OnScreenPointer(RadarScreenInfo screen, RadarScreenPointer pointer)
{
    var key = new PointerKey(screen.screenId, pointer.pointerId);
    if (pointer.phase == RadarPointerPhase.Up)
    {
        Release(key);
        return;
    }
    if (!_router.TryRaycast(screen, pointer, out var hit))
    {
        _logs.AddMiss(screen, pointer);
        return;
    }
    var particle = GetOrCreate(key);
    particle.transform.position = hit.point + hit.normal * 0.01f;
    if (!particle.isPlaying) particle.Play();
    _logs.AddHit(screen, pointer, hit);
}
```

Prewarm a pool of 16 particle systems, cap growth at 64, color by stable ScreenId color and intensify on Down. Release on Up, screen zero-frame timeout or component disable.

- [ ] **Step 4: Author the three-wall CAVE scene with explicit bindings**

Scene hierarchy:

```text
MultiScreenCameraRouting
├── Runtime
│   ├── RadarFrameDispatcher
│   ├── RadarScreenCameraRouter
│   ├── RadarLocalScreenSimulator
│   ├── MultiScreenCameraRoutingPresenter
│   └── RadarWorldPointerVisualizer
├── CAVE
│   ├── LeftWall + BoxCollider + LeftCamera
│   ├── FrontWall + BoxCollider + FrontCamera
│   └── RightWall + BoxCollider + RightCamera
├── PointerParticlePool
└── Canvas
    ├── Header + IPC/LOCAL status
    ├── Left/Front/Right point overlays
    ├── Mode controls
    └── Scrollable bounded log panel
```

Bind logical IDs `left`, `front`, `right`; default logical resolutions are 1920×1440, 4096×1536, 1920×1440. Cameras must visibly frame the corresponding wall, and walls use three distinct dark-blue materials with high-contrast grid labels.

- [ ] **Step 5: Make data source switching explicit and exclusive**

`MultiScreenCameraRoutingPresenter` has enum `LocalSimulation`/`BridgeIpc`. The scene serializes Dispatcher `autoConnect=false`. Local mode consumes simulator batches through the same presenter handler used by dispatcher events. IPC mode stops simulation, calls `RadarFrameDispatcher.Connect()` and shows Bridge version/protocol; returning to local mode awaits `DisconnectAsync()`. Switching modes emits Up/recycles all active effects before enabling the new source.

- [ ] **Step 6: Add detailed but bounded sample logs**

Each hit line contains:

```text
[FRONT/P7] Move logical=(2048.0,768.0) normalized=(0.500,0.500) camera=(960.0,540.0) ray=(origin)->(direction) hit=FrontWall world=(0.00,1.50,4.00)
```

Maintain 300 lines, throttle Move to 10 Hz per key, update an unthrottled live point table, and show frame age, batch sequence, dropped batches and per-screen Pointer count.

- [ ] **Step 7: Compile and manually verify both modes**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform PlayMode -IncludeSamples
```

Expected: sample scripts compile; local mode animates all three walls with distinct particles and logs. IPC mode, when Bridge Simulation is running for three screens/four sensors, shows Bridge points at the same wall positions without local simulator events.

- [ ] **Step 8: Commit the multi-screen sample**

```powershell
git add UnityPackage/com.blaze.radar/Samples~/MultiScreenCameraRouting UnityPackage/com.blaze.radar/package.json UnityPackage/com.blaze.radar/Tests
git commit -m "feat: add multi-screen camera routing sample"
```

### Task 13: Add repeatable Unity and end-to-end verification

**Files:**
- Create: `scripts/test-unity-package.ps1`
- Create: `tests/Radar.EndToEnd.Tests/Radar.EndToEnd.Tests.csproj`
- Create: `tests/Radar.EndToEnd.Tests/MultiScreenRadarEndToEndTests.cs`
- Modify: `RadarControl.sln`
- Create: `tests/Radar.Bridge.Wpf.Tests/RuntimeLoggingTests.cs`
- Modify: `scripts/test.ps1`

**Interfaces:**
- Consumes: published package sources, Unity 2021.3 executable, Coordinator fake sensor factory and real Named Pipe framing.
- Produces: one command for .NET suite, one command for Unity EditMode/PlayMode, and a real IPC three-screen/four-sensor acceptance test.

- [ ] **Step 1: Create a Windows-compatible end-to-end test project**

Use `net8.0-windows`, xUnit 2.5.3, and project references to Contracts, Configuration, Processing, IPC and Bridge.Wpf. Add it to `RadarControl.sln` with:

```powershell
dotnet sln RadarControl.sln add tests/Radar.EndToEnd.Tests/Radar.EndToEnd.Tests.csproj
```

- [ ] **Step 2: Write a failing real-pipe three-screen/four-sensor test**

```csharp
[Fact]
public async Task ThreeScreensFourSensors_PublishMergedFrontPointerAndIsolateSensorFailure()
{
    var pipeName = "RadarControl.E2E." + Guid.NewGuid().ToString("N");
    var factory = new ControlledSensorPipelineFactory();
    await using var coordinator = CreateCoordinator(pipeName, ThreeScreenFourSensorConfiguration(), factory);
    await coordinator.StartInfrastructureAsync();
    await using var client = await ConnectV2ClientAsync(pipeName, ThreeScreenHello());
    factory.Publish("front", "f1", Detection(1900, 700));
    factory.Publish("front", "f2", Detection(1940, 710));
    factory.Fail("left", "l1", new IOException("simulated disconnect"));

    var batch = await ReadNextBatchWithPointersAsync(client, TimeSpan.FromSeconds(2));

    Assert.Equal(3, batch.Screens.Count);
    Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
    Assert.Empty(batch.Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
    Assert.Equal(RadarSensorRuntimeState.Running, factory.Get("right", "r1").State);
}
```

Add a second test for hot resolution/output-rectangle changes: read active Down/Move, apply change, assert one Up, then one zero frame, then new IDs only after fresh detections.

- [ ] **Step 3: Add logging assertions for required field diagnostics**

Tests must assert exact tags and metrics:

```csharp
Assert.Contains(logs, value => value.Contains("[front/f1]") && value.Contains("raw=") && value.Contains("valid=") && value.Contains("crc="));
Assert.Contains(logs, value => value.Contains("[front/FUSION]") && value.Contains("groups=") && value.Contains("pointers="));
Assert.Contains(logs, value => value.Contains("[IPC]") && value.Contains("batch=") && value.Contains("latencyMs="));
```

Continuous Move logging is limited to 10 Hz per `(ScreenId, PointerId)`; connection, error, Down, Up and configuration logs are never throttled.

- [ ] **Step 4: Implement a self-contained Unity package test runner**

`scripts/test-unity-package.ps1` accepts `-UnityEditor`, `-TestPlatform EditMode|PlayMode|All`, and `-IncludeSamples`. If `-UnityEditor` is empty, resolve the highest installed `C:\Program Files\Unity\Hub\Editor\2021.3.*\Editor\Unity.exe`; reject other major/minor versions. Recreate only `tmp/unity-package-tests`, write a manifest containing:

```json
{
  "dependencies": {
    "com.blaze.radar": "file:../../../UnityPackage/com.blaze.radar",
    "com.unity.test-framework": "1.1.33",
    "com.unity.ugui": "1.0.0",
    "com.unity.nuget.newtonsoft-json": "3.0.2"
  }
}
```

Write `ProjectSettings/ProjectVersion.txt` with the resolved 2021.3 editor version. When `-IncludeSamples` is set, copy both sample folders into `Assets/Samples/Blaze Radar SDK/1.2.0/`. Invoke Unity separately for EditMode and PlayMode with `-batchmode -nographics -runTests -testResults <absolute xml> -logFile <absolute log>`, check exit code, XML failures and `error CS` in the log.

- [ ] **Step 5: Make the repository test script run every .NET project including E2E**

Keep `scripts/test.ps1` as the fast default:

```powershell
dotnet test RadarControl.sln -c $Configuration --logger "console;verbosity=minimal"
```

Add optional switches `-Unity`, `-UnityEditor`, and `-IncludeSamples`; when `-Unity` is present, call `scripts/test-unity-package.ps1 -TestPlatform All` after .NET succeeds.

- [ ] **Step 6: Run the complete automated matrix**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform All -IncludeSamples
```

Expected: all .NET projects pass; Unity EditMode and PlayMode XML show zero failures; package and both samples compile under Unity 2021.3; no `error CS` appears in Unity logs.

- [ ] **Step 7: Run a bounded simulation soak test**

Add an E2E theory that runs three screens/four simulated sensors for 30 seconds at 30 Hz, reconnects one front sensor every 5 seconds, and asserts monotonic IPC sequence, no duplicate `(ScreenId, PointerId)` within a frame, bounded managed-memory growth below 64 MiB after warmup, and completion without unobserved task exceptions. This is the automated precursor to the required on-site 8-hour real-hardware acceptance.

- [ ] **Step 8: Commit verification infrastructure**

```powershell
git add RadarControl.sln scripts tests/Radar.EndToEnd.Tests tests/Radar.Bridge.Wpf.Tests
git commit -m "test: verify multi-screen radar end to end"
```

### Task 14: Version, document, publish and verify release 1.2.0

**Files:**
- Modify: `src/Radar.Bridge.Wpf/BridgeVersion.cs`
- Modify: `src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj`
- Modify: `UnityPackage/com.blaze.radar/Runtime/UnitySdkVersion.cs`
- Modify: `UnityPackage/com.blaze.radar/package.json`
- Modify: `UnityPackage/com.blaze.radar/CHANGELOG.md`
- Modify: `UnityPackage/com.blaze.radar/README.md`
- Modify: `UnityPackage/com.blaze.radar/Documentation~/index.md`
- Modify: `README.md`
- Modify: `INSTALL.md`
- Modify: `docs/unity-integration.md`
- Modify: `docs/troubleshooting.md`
- Modify: `docs/version-and-limitations.md`
- Modify: `docs/architecture.md`
- Modify: `docs/protocol.md`
- Modify: `scripts/publish-bridge.ps1`
- Modify: `scripts/test-embedded-bridge.ps1`
- Regenerate: `UnityPackage/com.blaze.radar/Bridge~/win-x64/**`

**Interfaces:**
- Consumes: all tested 1.2.0 code, package path and publish scripts.
- Produces: matching Bridge/package/SDK versions, embedded self-contained executable, installation/upgrade/field-test instructions, release commit and tag.

- [ ] **Step 1: Centralize and align all version values**

```csharp
namespace Yuexin.Radar.Bridge.Wpf;
public static class BridgeVersion { public const string Value = "1.2.0"; }
```

```csharp
namespace Blaze.Radar;
public static class UnitySdkVersion { public const string Value = "1.2.0"; }
```

Set package JSON version and WPF `<Version>` to `1.2.0`; set footer to `Bridge 1.2.0 · IPC 2 · Windows x64`. Update package identity tests so package JSON, SDK constant, Bridge marker and release expectation must all equal `1.2.0`.

- [ ] **Step 2: Write the exact operator and developer documentation**

Document:

1. Git URL `https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.0`;
2. removal of old `1.1.x` Git URL and verification of Package Manager resolved path;
3. arbitrary screen setup in Project Settings, one enabled primary, stable IDs and logical resolutions;
4. Bridge screen selection, sensor add/remove, per-sensor connection/output rectangle and per-screen fusion/tracking parameters;
5. LEFT/FRONT/RIGHT example with FRONT F1/F2 overlap;
6. Basic Interaction and Multi-Screen Camera Routing local/IPC test paths;
7. Camera binding for independent Displays, pixelRect and RenderTexture;
8. tagged Bridge logs and matching `Player.log` fields;
9. protocol v1/v2 incompatibility recovery;
10. WPF disappearing/blurry-controls checks and GPU-independent software rendering;
11. player build Bridge version/SHA verification and stale package-cache cleanup;
12. on-site 8-hour stability checklist for three projectors/four radars.

- [ ] **Step 3: Run clean build and automated tests before publishing**

```powershell
dotnet clean RadarControl.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform All -IncludeSamples
```

Expected: zero build/test failures and zero Unity compile errors.

- [ ] **Step 4: Publish and embed the complete Bridge**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/publish-bridge.ps1 -Runtime win-x64
```

`publish-bridge.ps1` must delete only the validated absolute `artifacts/publish/RadarBridge/win-x64` and package `Bridge~/win-x64` targets, publish self-contained, copy both Schema 2 profiles, write UTF-8 `bridge-version.txt`, embed the entire output directory, and print RadarBridge.exe SHA-256 plus file count. It must reject framework-dependent embedding.

- [ ] **Step 5: Smoke-test the exact embedded executable**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -StartupTimeoutSeconds 20
```

Expected: embedded `RadarBridge.exe` creates a sharp top-level window, version marker is 1.2.0, no startup error dialog appears, a v2 client receives HelloAck, and Bridge exits with code 0 when the short-lived parent process ends.

- [ ] **Step 6: Verify artifact identity and repository cleanliness**

```powershell
$published = (Get-FileHash 'artifacts/publish/RadarBridge/win-x64/RadarBridge.exe' -Algorithm SHA256).Hash
$embedded = (Get-FileHash 'UnityPackage/com.blaze.radar/Bridge~/win-x64/RadarBridge.exe' -Algorithm SHA256).Hash
if ($published -ne $embedded) { throw "Published and embedded RadarBridge.exe hashes differ." }
git diff --check
git status --short
```

Expected: hashes match, diff check is clean, and status contains only the intended source/docs/version/generated Bridge changes.

- [ ] **Step 7: Commit the release payload**

```powershell
git add src UnityPackage README.md INSTALL.md docs scripts tests config RadarControl.sln
git commit -m "release: prepare Blaze Radar SDK 1.2.0"
```

- [ ] **Step 8: Run the final verification against the release commit**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release -NoBuild
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -StartupTimeoutSeconds 20
git status --short
```

Expected: all tests pass without rebuilding source, embedded Bridge smoke passes, and the worktree is clean.

- [ ] **Step 9: Tag and push only the verified release commit**

```powershell
git tag -a v1.2.0 -m "Blaze Radar SDK 1.2.0"
git push origin main
git push origin v1.2.0
```

Expected: GitHub `main` and annotated `v1.2.0` point to the same verified release commit; the documented Unity Git URL resolves package version 1.2.0.

## Final On-Site Acceptance

The automated plan completes software verification. Release acceptance is finalized on the projection computer with three projectors and four real radars:

- Run LEFT L1, FRONT F1+F2 overlap and RIGHT R1 continuously for 8 hours.
- Walk through FRONT overlap repeatedly and confirm one Pointer with stable ID and no duplicate click.
- Disconnect/reconnect each radar and NIC independently; unrelated screens continue updating.
- Change resolution/output rectangles only when operators expect Pointer Up/reset; verify Unity receives the new pixels.
- Exercise buttons, sliders, 2D/3D targets and per-camera world particles in a built Player.
- Confirm Bridge controls never disappear or blur after click, drag, resize, minimize/restore and projector focus changes.
- Archive Bridge logs, `Player.log`, final Schema 2 configuration and all RadarBridge.exe/package hashes with the site acceptance record.
