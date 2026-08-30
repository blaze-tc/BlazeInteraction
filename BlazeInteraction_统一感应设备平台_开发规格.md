# Blaze Interaction 统一感应设备平台开发规格

**文档版本：** V1.0  
**日期：** 2026-08-19  
**目标仓库：** 以现有 `blaze-tc/RadarControl` 为演化基础  
**首版目标：** 保留现有 F10/F20 雷达全部能力，并新增普通摄像头手部追踪 Provider；Unity 侧只导入一个 Package，即可使用不同感应设备。

---

## 0. Codex 执行前必读

本项目不是重新开发一套“摄像头 SDK”，也不是在现有 RadarControl 旁边再增加第二套 Bridge。

最终只允许存在一套统一产品：

```text
Windows：BlazeInteractionBridge.exe   ← 唯一 EXE
Unity：  com.blaze.interaction       ← 唯一主 Package
输入设备：外部 Provider DLL 插件
```

Codex 开始修改代码前，必须先阅读现有 RadarControl 的以下内容：

- `README.md`
- `docs/architecture.md`
- `docs/protocol.md`
- `docs/unity-integration.md`
- `src/Radar.Contracts/`
- `src/Radar.Device/`
- `src/Radar.Protocol/`
- `src/Radar.Processing/`
- `src/Radar.Configuration/`
- `src/Radar.Ipc/`
- `src/Radar.Bridge.Wpf/`
- `UnityPackage/com.blaze.radar/Runtime/`
- `UnityPackage/com.blaze.radar/Editor/`
- `tests/`

**原则：优先重构和复用现有成熟实现，不允许为了改名而重写已经稳定的雷达协议、跟踪、IPC、Build Processor、Launcher、输入模块。**

---

# 1. 项目最终定位

把现有 RadarControl 从“雷达专用桥接程序”升级为：

> **Blaze Interaction：Windows 通用感应设备输入平台 + Unity 统一交互 SDK。**

Unity 游戏层不应该知道当前输入来自：

- F10/F20 激光雷达
- 普通 USB 摄像头手部追踪
- 深度摄像头
- RealSense
- Astra
- Azure Kinect
- LiDAR
- 其他未来感应设备

Unity 只处理统一的 `InteractionPoint`。

```text
Physical Sensor
      ↓
Provider Plugin
      ↓
InteractionFrame / InteractionPoint
      ↓
BlazeInteractionBridge.exe
      ↓ Named Pipe
com.blaze.interaction
      ↓
Unity Game / EventSystem
```

---

# 2. 已确认的不可变架构决策

| 决策项 | 定稿 |
|---|---|
| Windows 可执行程序 | 只有一个 `BlazeInteractionBridge.exe` |
| Provider 形式 | 真正的外部 DLL/目录插件 |
| V1 激活模式 | 同一时间只激活一种 Provider |
| 未来扩展 | 架构必须支持多个 Provider 同时运行 |
| Unity Package | 只有 `com.blaze.interaction` |
| Unity 输入抽象 | `InteractionPoint + Extensions` |
| Radar 兼容 | 新架构 + `Blaze.Radar` 兼容层 |
| Provider UI | Provider 自己提供配置页面，Bridge 只提供 UI Host |
| IPC | 继续使用 Named Pipe + 长度前缀 JSON |
| 多屏能力 | 必须保留现有 RadarControl 多屏/多 Camera 路由能力 |
| Camera AI | 必须进程内运行，不允许 Python Worker / 第二个 EXE |
| 平台 | Windows 10/11 x64，.NET 8，Unity 2021.3+ |

以下方案明确禁止：

- `RadarBridge.exe + HandTrackingBridge.exe` 两套 EXE
- `ProviderHost.exe`
- `HandTrackingWorker.exe`
- Unity 直接打开摄像头并运行 AI
- Unity 直接连接雷达
- 主 Bridge 根据设备类型写大量 `if (Radar) / if (Camera)` 业务代码
- 新增设备时必须修改 Unity 游戏逻辑

---

# 3. 总体架构

```text
┌────────────────────────────────────────────────────────────┐
│                    Unity Application                       │
│                                                            │
│                com.blaze.interaction                       │
│    InteractionManager / InteractionInputModule / Router    │
└───────────────────────────┬────────────────────────────────┘
                            │ Named Pipe IPC
                            ▼
┌────────────────────────────────────────────────────────────┐
│              BlazeInteractionBridge.exe                    │
│                                                            │
│  ProviderCatalog                                           │
│  ProviderManager                                           │
│  InteractionRuntime                                        │
│  SurfaceTopology                                           │
│  IPC Server                                                │
│  WPF Shell                                                 │
│  Configuration / Logging / Diagnostics                     │
│                                                            │
│       ┌─────────────────┐    ┌─────────────────────┐       │
│       │ Radar Provider  │    │ CameraHand Provider │       │
│       │ external DLL    │    │ external DLL        │       │
│       └────────┬────────┘    └──────────┬──────────┘       │
└────────────────┼─────────────────────────┼──────────────────┘
                 │                         │
                 ▼                         ▼
             F10/F20                   USB Camera
```

V1：ProviderManager 只允许一个 Active Provider。

未来：

```text
Bridge
 ├─ Radar Provider
 ├─ CameraHand Provider
 └─ Depth Provider
        ↓
  Routing / Optional Fusion
        ↓
 InteractionFrame
```

**协议、配置 Schema 和内部数据结构从 V1 开始就必须使用 `providerId/providerInstanceId/sourceId`，禁止设计成单 Provider 专用字段。**

---

# 4. Solution 建议结构

目标结构：

```text
RadarControl/                         # 可继续沿用现有仓库名，后续再决定是否重命名仓库
│
├─ BlazeInteraction.sln
│
├─ src/
│  ├─ Blaze.Interaction.Contracts/
│  ├─ Blaze.Interaction.Provider.Abstractions/
│  ├─ Blaze.Interaction.Configuration/
│  ├─ Blaze.Interaction.Ipc/
│  ├─ Blaze.Interaction.Runtime/
│  └─ Blaze.Interaction.Bridge.Wpf/
│
├─ providers/
│  ├─ Radar/
│  │  ├─ Blaze.Provider.Radar/
│  │  ├─ Radar.Contracts/              # V1 可先复用原项目，避免无意义重写
│  │  ├─ Radar.Device/
│  │  ├─ Radar.Protocol/
│  │  ├─ Radar.Processing/
│  │  └─ Radar.Configuration/
│  │
│  └─ CameraHand/
│     ├─ Blaze.Provider.CameraHand/
│     ├─ Blaze.Provider.CameraHand.Inference/
│     └─ native/
│
├─ UnityPackage/
│  └─ com.blaze.interaction/
│
├─ tests/
│  ├─ Blaze.Interaction.*.Tests/
│  ├─ Blaze.Provider.Radar.Tests/
│  ├─ Blaze.Provider.CameraHand.Tests/
│  └─ UnityShared/
│
├─ config/
├─ docs/
└─ scripts/
```

## 4.1 重构原则

V1 不要求一次性把现有 `Radar.*` 所有程序集和 namespace 全部重命名。

优先方式：

1. 建立新的 Interaction Core。
2. 给现有 Radar Runtime 增加一个 Adapter/Provider 外壳。
3. 证明雷达功能完全不回归。
4. 再逐步整理内部命名。

禁止一次 PR 同时做：

- 大规模 namespace rename
- 协议重写
- Provider 抽象
- Unity Package 重写
- 摄像头 Provider

必须分阶段。

---

# 5. Provider 插件规范

## 5.1 Provider 目录

发布后的 Bridge 目录：

```text
BlazeInteraction/
│
├─ BlazeInteractionBridge.exe
├─ *.dll
├─ config/
│
└─ Providers/
   ├─ Radar/
   │  ├─ provider.json
   │  ├─ Blaze.Provider.Radar.dll
   │  ├─ Radar.*.dll
   │  ├─ native/
   │  └─ profiles/
   │
   └─ CameraHand/
      ├─ provider.json
      ├─ Blaze.Provider.CameraHand.dll
      ├─ native/
      └─ models/
```

**允许 DLL、Native DLL、模型和资源文件很多，但最终只能有一个 EXE。**

## 5.2 Provider Manifest

`provider.json`：

```json
{
  "id": "blaze.camera-hand",
  "displayName": "摄像头手部追踪",
  "version": "1.0.0",
  "providerApiVersion": 1,
  "entryAssembly": "Blaze.Provider.CameraHand.dll",
  "entryType": "Blaze.Provider.CameraHand.CameraHandPlugin",
  "category": "Camera",
  "capabilities": [
    "interaction-point",
    "preview",
    "roi",
    "hand",
    "hand-landmarks"
  ]
}
```

Radar：

```json
{
  "id": "blaze.radar.f10f20",
  "displayName": "F10 / F20 激光雷达",
  "version": "1.0.0",
  "providerApiVersion": 1,
  "entryAssembly": "Blaze.Provider.Radar.dll",
  "entryType": "Blaze.Provider.Radar.RadarPlugin",
  "category": "Radar",
  "capabilities": [
    "interaction-point",
    "preview",
    "multi-sensor",
    "calibration",
    "multi-surface"
  ]
}
```

## 5.3 插件加载

必须使用独立 `AssemblyLoadContext` + `AssemblyDependencyResolver` 加载每个 Provider 目录。

关键要求：

- `Blaze.Interaction.Provider.Abstractions.dll` 必须由默认上下文共享，不能让每个插件加载自己的副本。
- `Blaze.Interaction.Contracts.dll` 同样作为共享契约程序集。
- Provider 自己的第三方托管 DLL 从自己的目录解析。
- Native DLL 从 Provider 自己的 `native/win-x64/` 等目录解析。
- Provider API 主版本不兼容时拒绝加载，并在 UI 中显示原因。
- 单个 Provider 加载失败不能阻止 Bridge 启动。

注意：AssemblyLoadContext 只能提供依赖隔离，不是进程级可靠性隔离。Native DLL 发生进程级崩溃时仍可能带崩 Bridge；这是“唯一 EXE”约束下可接受的已知限制。

---

# 6. Provider API

Provider API 必须小而稳定，不能把雷达或摄像头专用概念放进 Core。

建议核心接口：

```csharp
public interface IInteractionProviderPlugin
{
    ProviderDescriptor Descriptor { get; }

    IInteractionProvider CreateProvider(
        ProviderCreateContext context);

    IProviderSettingsViewFactory? SettingsViewFactory { get; }
}
```

```csharp
public interface IInteractionProvider : IAsyncDisposable
{
    string ProviderInstanceId { get; }

    ProviderRuntimeStatus Status { get; }

    event EventHandler<InteractionFrameEventArgs>? FrameReceived;
    event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;

    Task InitializeAsync(
        ProviderInitializationContext context,
        CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
```

Provider 输出 `InteractionFrame`，Bridge 不轮询具体硬件。

Provider 不允许直接：

- 建立 Unity Named Pipe
- 启动 Unity EventSystem
- 修改 Unity Package
- 操作其他 Provider 的状态
- 直接写主程序全局配置

---

# 7. Provider 配置 UI

雷达和摄像头配置差异很大，所以 Bridge 主窗口不能硬编码设备配置。

主窗口只负责：

```text
Provider Selector
Provider Status
Provider Settings Host   ← 插件页面挂在这里
Surface / Unity Global Settings
Logs / Diagnostics
```

Provider 可选实现：

```csharp
public interface IProviderSettingsViewFactory
{
    FrameworkElement CreateView(
        IInteractionProvider provider,
        IProviderSettingsContext context);
}
```

Radar Provider 页面继续承载现有：

- F10 / F20
- IP / Port / NIC
- Sensor 列表
- transform / filter
- OutputRect
- calibration
- fusion / tracking
- touch / dwell
- Preview

CameraHand Provider 页面包含：

- 摄像头选择
- 分辨率
- FPS
- Mirror / Rotate
- Recognition ROI
- 最大手数
- Tracking Point
- Detection / Tracking Confidence
- Smoothing
- Preview

Provider 页面不能绕过配置服务直接写任意文件。

---

# 8. Surface：保留并泛化现有多屏能力

现有 RadarControl 已经具备多逻辑屏幕、多 Camera、Display / pixelRect / RenderTexture 路由能力，新架构不能丢失。

把“Screen”泛化成核心概念 `InteractionSurface`：

```csharp
public sealed record InteractionSurfaceDescriptor
{
    public required string SurfaceId { get; init; }
    public required string Name { get; init; }
    public required int LogicalWidth { get; init; }
    public required int LogicalHeight { get; init; }
    public required bool IsPrimary { get; init; }
    public required int Order { get; init; }
}
```

V1 Unity Project Settings 继续由 Unity 声明 Surface topology。

Radar Provider：

```text
LEFT  ← Radar L1
FRONT ← Radar F1 + F2
RIGHT ← Radar R1
```

CameraHand V1：

```text
Camera ROI → 一个指定 Surface
```

未来支持：

```text
Camera 1 → LEFT
Camera 2 → FRONT
Camera 3 → RIGHT
```

`InteractionPoint` 必须始终携带 `surfaceId`。

---

# 9. 统一 InteractionPoint

Unity 和 Bridge Core 都只认统一点位：

```csharp
public sealed record InteractionPoint
{
    public required long Id { get; init; }

    public required string SurfaceId { get; init; }

    public required string ProviderId { get; init; }
    public required string ProviderInstanceId { get; init; }
    public required string SourceId { get; init; }

    public required InteractionPhase Phase { get; init; }

    public required Vector2Data NormalizedPosition { get; init; }
    public required Vector2Data PixelPosition { get; init; }

    public required float Confidence { get; init; }
    public required long TimestampUnixMs { get; init; }

    public InteractionExtensions? Extensions { get; init; }
}
```

## 9.1 ID 稳定域

V1 规定：

```text
InteractionPoint Id 的唯一域 = ProviderInstanceId + SurfaceId
```

不同 Provider 不要求 ID 全局连续。

Unity 内部若需要全局 pointer key，使用：

```text
ProviderInstanceId + SurfaceId + Id
```

不要假设 `Id=1` 在所有设备中都是同一个人/手/点。

## 9.2 InteractionPhase

```csharp
public enum InteractionPhase
{
    Hover = 0,
    Down = 1,
    Move = 2,
    Up = 3,
    Cancel = 4
}
```

Radar Provider：继续把现有 Touch / Dwell / pointer 生命周期映射到对应 Phase。

CameraHand V1：只有坐标追踪时主要输出 `Hover/Move`；后续 Pinch Gesture 再产生 `Down/Up`。

---

# 10. Extensions 设计

通用游戏只读取：

```csharp
point.Id
point.Position
point.Phase
point.Confidence
point.SurfaceId
```

特殊游戏可以读取扩展信息。

协议 JSON：

```json
{
  "extensions": {
    "hand": {
      "handedness": "Right",
      "trackingPoint": "PalmCenter"
    },
    "radar": {
      "sensorId": "F1"
    }
  }
}
```

Unity SDK 内置少量已知 Typed Helper：

```csharp
point.TryGetHandExtension(out var hand);
point.TryGetRadarExtension(out var radar);
```

同时必须保留 Raw Extension：

```csharp
IReadOnlyDictionary<string, JToken> Extensions
```

这样未来增加新 Provider 时，只使用统一 Position 的 Unity 项目完全不需要升级 Package；需要使用新设备特殊字段时才增加业务侧解析或后续 SDK typed helper。

---

# 11. InteractionFrame 与 IPC

统一业务帧：

```csharp
public sealed record InteractionFrame
{
    public required string ProviderId { get; init; }
    public required string ProviderInstanceId { get; init; }
    public required string SurfaceId { get; init; }
    public required long Sequence { get; init; }
    public required long TimestampUnixMs { get; init; }
    public required IReadOnlyList<InteractionPoint> Points { get; init; }
}
```

建议继续沿用现有 RadarControl 已验证过的：

```text
4-byte little-endian JSON byte length
+
UTF-8 JSON
```

Pipe 建议升级为：

```text
Blaze.InteractionBridge
```

协议主版本建议重新定义为：

```text
Interaction IPC Protocol = 1
```

不要继续把新的通用协议称为 Radar IPC 2。

## 11.1 Hello

Unity → Bridge：

```json
{
  "messageType": "Hello",
  "protocolVersion": 1,
  "unityPid": 12345,
  "unityVersion": "2021.3.45f1",
  "sdkVersion": "1.0.0",
  "surfaces": [
    {
      "surfaceId": "FRONT",
      "name": "Front",
      "logicalWidth": 1920,
      "logicalHeight": 1080,
      "isPrimary": true,
      "order": 0
    }
  ]
}
```

Bridge → Unity：

```json
{
  "messageType": "HelloAck",
  "protocolVersion": 1,
  "bridgeVersion": "1.0.0",
  "activeProvider": {
    "id": "blaze.camera-hand",
    "instanceId": "camera-main"
  },
  "capabilities": [
    "multi-surface",
    "provider-extensions"
  ]
}
```

业务消息：

- `Hello`
- `HelloAck`
- `InteractionFrame`
- `Status`
- `ProviderChanged`
- `Ping`
- `Pong`
- `Shutdown`
- `Error`

切换 Provider 时必须先向 Unity 发 reset/cancel，清理当前所有 Active Point，禁止旧设备留下悬挂 Pointer。

---

# 12. Windows Bridge 主程序职责

`BlazeInteractionBridge.exe` 负责：

1. Provider 扫描与验证
2. Provider 动态加载
3. Active Provider 生命周期
4. Surface topology
5. Provider Frame 接收
6. 通用状态 / diagnostics
7. Unity IPC
8. Profile / 配置
9. WPF 主窗口
10. Unity parent PID 生命周期
11. 日志
12. 发布版本校验

Bridge 不负责：

- 解析 F10/F20 数据包
- 手部 AI 推理
- Camera ROI 算法
- RealSense SDK
- Astra SDK

这些全部属于 Provider。

---

# 13. Windows UI 设计

主界面建议：

```text
┌──────────────── Blaze Interaction Control ─────────────────┐
│ Provider [ 摄像头手部追踪 ▼ ]   Status ● Running           │
├────────────────────────────────────────────────────────────┤
│                                                            │
│                 Provider Settings Host                     │
│                                                            │
│     这里完全由当前 Provider 提供自己的配置和 Preview       │
│                                                            │
├────────────────────────────────────────────────────────────┤
│ Unity / Surface                                             │
│ Unity: Connected    PID: 12345                              │
│ Surface: FRONT 1920×1080                                    │
├────────────────────────────────────────────────────────────┤
│ Diagnostics                                                 │
│ Input FPS | Output FPS | Points | Latency | Dropped         │
├────────────────────────────────────────────────────────────┤
│ Logs                                                        │
└────────────────────────────────────────────────────────────┘
```

V1 切换 Provider：

```text
Stop Current Provider
→ Cancel Active Points
→ Persist Current Provider Profile
→ Load Selected Provider
→ Initialize
→ Start
→ ProviderChanged
```

不允许两个 Provider 同时 Start。

但是 `ProviderManager` 的 API 不能写成 `CurrentRadar` 这种单设备模型；内部使用集合和 `ProviderInstanceId`，为后续多 Provider 做准备。

---

# 14. Radar Provider 迁移方案

这是整个重构最重要的稳定性原则：

> **先让现有 RadarControl 变成一个 Provider，行为完全一致，再增加摄像头。**

现有模块迁移映射：

| 当前 RadarControl | 新架构 |
|---|---|
| `Radar.Device` | Radar Provider 私有设备层，尽量保留 |
| `Radar.Protocol` | Radar Provider 私有协议层，保留 |
| `Radar.Processing` | Radar Provider 私有 Processing，保留 |
| `Radar.Configuration` | Radar Provider 配置/迁移逻辑 |
| `Radar.Contracts` | 雷达内部 DTO；通用输出适配到 Interaction Contracts |
| `Radar.Ipc` | 通用 IPC 部分迁到 `Blaze.Interaction.Ipc`；雷达设备协议不混入 Core |
| `Radar.Bridge.Wpf` | 拆为通用 WPF Shell + Radar Settings View |
| `RadarBridgeLauncher` | 新 `InteractionBridgeLauncher`；旧类由 Compatibility 包装 |
| `RadarFrameDispatcher` | 新 `InteractionFrameDispatcher`；Compatibility 适配 |
| `RadarInputModule` | 新 `InteractionInputModule`；Compatibility 适配 |
| `RadarBuildProcessor` | 新 `InteractionBuildProcessor` |
| `RadarSettingsProvider` | 新 `InteractionSettingsProvider` |

## 14.1 Radar Provider 适配边界

Radar Provider 最终输出：

```text
Existing Radar PointerBatch / internal pointer
              ↓ Adapter
InteractionFrame
              ↓
Bridge Core
```

V1 不要为了统一格式而重新实现：

- F10/F20 解码
- CRC
- 多 Sensor 生命周期
- Fusion
- Tracking
- Calibration
- OutputRect
- Touch/Dwell

只做输出 Adapter 和 UI Host 重构。

## 14.2 Radar 回归门禁

在 CameraHand 开发开始前，必须证明：

- 单雷达工作
- 多雷达工作
- FRONT F1/F2 overlap 仍只输出一个稳定 Pointer
- 多 Surface 正常
- Display / pixelRect / RenderTexture 路由正常
- UGUI / Physics2D / Physics3D 正常
- Radar reconnect 正常
- 现有配置可以迁移
- 长时间运行不劣于当前 1.2.10

如果这些没有通过，不得继续 CameraHand 主功能。

---

# 15. CameraHand Provider V1

## 15.1 目标

普通 USB 摄像头：

```text
Camera
 ↓
Frame Capture
 ↓
Mirror / Rotate
 ↓
ROI
 ↓
Hand Landmark Inference
 ↓
Stable Hand Tracking
 ↓
Tracking Point
 ↓
Smoothing
 ↓
Surface Mapping
 ↓
InteractionFrame
```

V1 功能：

- Camera Device
- Resolution
- FPS
- Preview
- Mirror
- Rotate
- Recognition ROI
- 1~2 Hands
- Left / Right
- 21 landmarks 内部保留
- Palm Center
- Index Tip
- Wrist
- Stable Hand ID
- Smoothing
- 指定一个 Surface
- InteractionPoint 输出

V1 暂不做：

- Pinch Click
- Fist
- Open Palm
- Swipe
- 3D interaction
- Face + Hand identity
- 多 Camera

## 15.2 Camera Capture

摄像头 Capture 必须位于 Provider 内，不允许 Unity 打开 Camera。

实时处理采用 Latest Frame Policy：

```text
Camera frame 100
101
102
103

Inference 尚未处理完 100 时：
下一次直接处理最新 103，允许丢弃 101/102。
```

禁止使用无界队列累积旧视频帧。

## 15.3 Hand Inference Backend

Provider 内部必须有：

```csharp
public interface IHandInferenceBackend : IAsyncDisposable
{
    Task InitializeAsync(...);
    ValueTask<HandInferenceResult> DetectAsync(...);
}
```

V1 首选：**MediaPipe Tasks Hand Landmarker 的进程内 Native/C API 路线**，通过 Provider 自己携带的 native DLL / wrapper DLL 调用；严禁启动 Python、Node 或第二个 EXE。

实现必须固定依赖版本并随 Provider 发布，不得要求客户机器单独安装 Python/MediaPipe。

如果 MediaPipe Windows native 构建在目标环境无法满足稳定发布要求，CameraHand Provider 可以替换为 ONNX/WinML 后端，但必须保持 `IHandInferenceBackend`、Provider API、Interaction IPC 不变。

**算法实现属于 Provider 私有实现，不能影响 Unity SDK 或 Bridge Core。**

## 15.4 Tracking Point

默认 Palm Center：

```text
Wrist(0)
Index MCP(5)
Middle MCP(9)
Ring MCP(13)
Pinky MCP(17)
```

取平均。

Index Tip：landmark 8。  
Wrist：landmark 0。

## 15.5 Stable Hand ID

不能使用 HandLandmarker 输出数组下标作为 ID。

至少结合：

- 上一帧位置
- 预测位置
- 距离
- handedness 作为软约束
- confirm frames
- lost frames

双手 Detection 顺序交换时 ID 不应立即交换。

## 15.6 ROI 到 Surface

Camera normalized 原点：左上。

ROI：

```text
roiX / roiY / roiWidth / roiHeight
```

手部 tracking point 先映射到 ROI normalized，再转换到 Surface normalized / pixel。

ROI 外手不输出。

---

# 16. Unity Package：com.blaze.interaction

新的 `package.json`：

```json
{
  "name": "com.blaze.interaction",
  "version": "1.0.0",
  "displayName": "Blaze Interaction SDK",
  "description": "Unified external-sensor interaction SDK for radar, camera hand tracking and future providers.",
  "unity": "2021.3"
}
```

建议结构：

```text
UnityPackage/com.blaze.interaction/
│
├─ Runtime/
│  ├─ Blaze.Interaction.Runtime.asmdef
│  ├─ InteractionBridgeLauncher.cs
│  ├─ InteractionPipeClient.cs
│  ├─ InteractionFrameDispatcher.cs
│  ├─ InteractionManager.cs
│  ├─ InteractionInputModule.cs
│  ├─ InteractionCameraRouter.cs
│  ├─ InteractionRuntimeSettings.cs
│  └─ Contracts/
│
├─ Compatibility/
│  └─ Radar/
│     ├─ Blaze.Radar.Compatibility.asmdef
│     ├─ RadarBridgeLauncher.cs
│     ├─ RadarFrameDispatcher.cs
│     ├─ RadarInputModule.cs
│     └─ ...
│
├─ Editor/
│  ├─ InteractionSettingsProvider.cs
│  ├─ InteractionBuildProcessor.cs
│  └─ InteractionSceneSetupMenu.cs
│
├─ Bridge~/win-x64/
│  ├─ BlazeInteractionBridge.exe
│  ├─ *.dll
│  └─ Providers/
│
├─ Samples~/
│  ├─ BasicInteraction/
│  ├─ MultiSurfaceRouting/
│  └─ ProviderDiagnostics/
│
└─ Tests/
```

---

# 17. Unity API

新项目主要 API：

```csharp
InteractionManager.Instance.Points
InteractionManager.Instance.IsConnected
InteractionManager.Instance.ActiveProvider
```

事件：

```csharp
PointAdded
PointUpdated
PointRemoved
ProviderChanged
ConnectionChanged
```

典型调用：

```csharp
foreach (var point in InteractionManager.Instance.Points)
{
    Vector2 position = point.PixelPosition;
}
```

游戏逻辑禁止：

```csharp
if (provider == "Radar")
{
    // 一套玩法
}
else if (provider == "Camera")
{
    // 另一套玩法
}
```

除非该游戏明确需要某种 Extensions 特殊能力。

---

# 18. Unity EventSystem

`InteractionInputModule` 是通用输入模块。

来源：

```text
InteractionFrame
      ↓
InteractionFrameDispatcher
      ↓
InteractionInputModule
      ↓
UGUI / Physics2D / Physics3D
```

保持现有 RadarInputModule 已具备的：

- 多指针
- Pointer lifecycle
- UGUI
- 2D
- 3D
- Camera routing
- 多 Surface

Radar Compatibility 中的 `RadarInputModule` 最终应代理或继承新模块，而不是保留第二套完整 EventSystem 实现。

---

# 19. Radar Compatibility

目标：旧项目安装新 Package 后尽量少改代码。

兼容层 namespace：

```text
Blaze.Radar
```

兼容层应该覆盖现有项目最常用类型，例如：

- `RadarBridgeLauncher`
- `RadarFrameDispatcher`
- `RadarInputModule`
- `RadarRuntimeSettings`
- 常用 Pointer DTO / events

实现原则：

```text
旧 Radar API
      ↓ wrapper/adapter
新 Interaction API
```

禁止 Compatibility 再维护一套 Pipe Client 或 Bridge Launcher。

对于无法 100% 无损兼容的 API：

- 使用 `[Obsolete]`
- 给出明确迁移说明
- 不允许静默改变语义

---

# 20. Bridge 生命周期

继续保留现有成熟模式：

```text
Unity Awake
 ↓
Probe Interaction Pipe
 ↓
Existing Bridge?
 ├─ Yes → Reuse
 └─ No  → Process.Start(BlazeInteractionBridge.exe)
                --parent-pid <UnityPID>
                --minimized
```

Unity 自动启动的 Bridge：Unity 退出后退出。

用户手工启动的 Bridge：Unity 断开后继续运行。

Bridge 内切换 Provider 不应该重启 EXE。

---

# 21. Build / 发布

Windows Player Build 后：

```text
<Game>_Data/
└─ BlazeInteraction/
   ├─ BlazeInteractionBridge.exe
   ├─ *.dll
   ├─ config/
   └─ Providers/
      ├─ Radar/
      └─ CameraHand/
```

Build Processor 必须：

1. 从当前 Package Manager Resolved Path 获取 Bridge。
2. 删除旧 Player 目录中的陈旧版本。
3. 完整复制所有 Bridge / Providers 文件。
4. 校验 version marker。
5. 校验 Provider manifest。
6. 校验核心 EXE 和必需 DLL。
7. 输出 SHA-256 诊断信息。

禁止仅复制 EXE。

---

# 22. 配置模型

Global Configuration：

```json
{
  "schemaVersion": 1,
  "activeProviderId": "blaze.camera-hand",
  "activeProviderInstanceId": "camera-main",
  "providers": {
    "blaze.radar.f10f20": {
      "profilePath": "profiles/radar-default.json"
    },
    "blaze.camera-hand": {
      "profilePath": "profiles/camera-default.json"
    }
  }
}
```

具体 Provider Profile 由 Provider 自己定义 Schema。

Core 只保存：

- Provider ID
- instance ID
- enabled/active
- profile location
- 通用 Surface / Unity settings

Core 不理解：

- radar IP
- camera index
- hand confidence
- RealSense depth mode

---

# 23. 日志与诊断

统一标签：

```text
[GLOBAL]
[PROVIDER]
[PROVIDER:blaze.radar.f10f20]
[PROVIDER:blaze.camera-hand]
[IPC]
[UNITY]
[SURFACE:FRONT]
```

每个业务帧诊断至少可以关联：

- Provider ID
- Provider Instance ID
- Surface ID
- Sequence
- Timestamp
- Point Count
- Dropped Count
- Latency

Windows UI 要显示：

- Active Provider
- Provider Version
- Provider Status
- Unity Connected
- Unity PID
- Input FPS
- Output FPS
- Point Count
- Latency
- Dropped Frames

---

# 24. 错误处理

Provider 加载失败：

```text
Bridge 继续运行
Provider 标记 LoadError
UI 显示错误
Unity 可保持 IPC 连接但 Points=[]
```

Provider Runtime 异常（普通托管异常）：

```text
Stop Provider
Clear Active Points
Status=Error
允许用户 Restart
```

Provider Native 崩溃导致整个 EXE 崩溃：这是单 EXE 方案的已知风险，无法通过 AssemblyLoadContext 完全避免。需要依靠：

- 固定依赖版本
- 严格 Provider 测试
- Native API 边界校验
- 资源释放
- 长时间稳定性测试

---

# 25. V1 开发阶段

## M0 - Baseline Freeze

目标：固定当前 RadarControl v1.2.10 行为作为回归基线。

- 跑完整现有测试
- 保存测试报告
- 保存 Package / Bridge version marker
- 验证真实雷达功能

## M1 - Interaction Core

新增：

- Interaction Contracts
- Surface model
- Provider Abstractions
- Provider Catalog
- Provider Loader
- Provider Manager
- Interaction IPC contracts

此阶段不改变雷达行为。

## M2 - Radar Provider Migration

把现有雷达功能放到 Radar Provider 边界内。

验收：现有雷达功能全部通过。

这是最重要 Gate。

## M3 - Unity com.blaze.interaction

建立：

- Interaction Launcher
- Pipe Client
- Dispatcher
- Manager
- InputModule
- Camera Router
- Project Settings
- Build Processor

同时完成 Radar Compatibility。

使用 Radar Provider 跑通完整 Unity Sample。

## M4 - Provider UI Shell

把现有 Radar WPF UI 拆成：

```text
Generic Bridge Shell
+
Radar Provider Settings View
```

Provider Selector 可以切换插件。

## M5 - CameraHand Provider Core

先完成：

```text
Camera
→ Preview
→ Mock/Replay hand result
→ InteractionPoint
→ Unity
```

证明 Provider 机制、ROI、Surface 映射和 Unity 路径正确。

## M6 - CameraHand Inference

接入进程内 Hand Landmarker：

```text
Camera
→ 21 landmarks
→ stable hand ID
→ Palm Center
→ smoothing
→ InteractionPoint
```

## M7 - Packaging & Long Run

- 单 Package 发布
- 两个 Provider 一起打包
- Radar / Camera 切换
- 8 小时稳定性

---

# 26. Codex 实施规则

Codex 必须遵守：

1. 先读本规格，再读现有 RadarControl。
2. 禁止从零重写雷达协议和处理链。
3. 每个 Milestone 单独提交。
4. 大型重构使用测试保护后再移动代码。
5. 优先新增 Adapter，再重命名。
6. 不允许一个提交同时完成 Radar 重构 + CameraHand。
7. 所有 Provider 通过相同 Abstractions。
8. Bridge Core 不能引用 CameraHand/Radar 具体类型。
9. Unity Runtime 不能引用 Provider DLL。
10. Unity 只能通过 Interaction IPC 获取数据。
11. 不允许增加第二个 EXE。
12. 新增 Provider 时不要求修改 Unity Package 的通用 Position 功能。
13. V1 同时只能启动一个 Provider，但内部 API 要允许未来多实例。
14. 所有实时数据通路禁止无界队列。
15. 所有资源必须支持 CancellationToken 和确定性 Dispose。
16. 每个 Milestone 完成后运行相关完整测试。
17. Radar 回归失败时停止继续开发 CameraHand。

---

# 27. 必测用例

## Provider Loader

- 正常 Provider
- 缺 provider.json
- JSON 损坏
- API Version 不匹配
- Entry Assembly 不存在
- Entry Type 不存在
- Provider 抛初始化异常
- 两个 Provider 依赖不同版本的第三方 DLL

## Provider Switch

```text
Radar Running
→ Switch Camera
→ Radar Points Cancel
→ Radar Stop
→ Camera Start
→ ProviderChanged
```

反向同样测试。

## Radar Regression

- 单雷达
- 多雷达
- 多屏
- overlap fusion
- reconnect
- Simulation
- Replay
- Calibration
- UGUI
- 2D/3D
- Multi-Screen Camera Routing

## CameraHand

- 摄像头不存在
- 摄像头拔出
- 摄像头恢复
- 单手
- 双手
- 双手交叉
- Detection 顺序交换
- ROI
- Mirror
- Rotate
- Hand Lost / Reappear
- 高速移动
- 低光环境至少做基本降级测试

## Unity

- Editor Play/Stop 100 次
- Bridge 手工启动
- Unity 自动启动 Bridge
- Unity 异常退出
- Bridge 已运行时复用
- Provider 切换时 Point Cancel
- Build 后路径正确
- 多 Surface 路由

---

# 28. 性能目标

Radar：不得明显劣于当前 1.2.10。

CameraHand 基准：

```text
1280×720 @ 30 FPS
最大 2 Hands
```

目标：

- Camera capture ≥ 25 FPS
- Hand inference ≥ 20 FPS
- Interaction output ≥ 20 Hz
- Unity 主线程不做 AI
- 不允许帧队列持续积压
- 端到端交互延迟目标 < 80 ms
- 理想目标 30~60 ms

性能值必须由目标 Windows 电脑真实测试确认。

---

# 29. V1 验收标准

## 产品层

- [ ] 最终只有一个 `BlazeInteractionBridge.exe`
- [ ] Unity 只需安装 `com.blaze.interaction`
- [ ] Windows 工具可以选择 Radar 或 CameraHand
- [ ] 切换 Provider 不重启 Bridge EXE
- [ ] Provider 是外部 DLL/目录插件
- [ ] 删除某 Provider 目录后主程序仍能启动

## Radar

- [ ] 原有 RadarControl 核心功能无明显回归
- [ ] F10/F20 可用
- [ ] 多 Sensor 可用
- [ ] 多 Surface 可用
- [ ] Fusion / Tracking 可用
- [ ] Calibration 可用
- [ ] Unity UI / 2D / 3D 输入可用

## CameraHand

- [ ] 可以选 USB Camera
- [ ] 可调整 ROI
- [ ] 识别 1~2 手
- [ ] 有稳定 Hand ID
- [ ] 输出 Left / Right
- [ ] 内部拥有 21 Landmarks
- [ ] Unity V1 可获得稳定 Position
- [ ] Camera 不由 Unity 占用
- [ ] 不存在额外 Worker EXE

## Unity

- [ ] Radar 与 Camera 使用同一个 Interaction API
- [ ] `InteractionManager.Instance.Points` 正常
- [ ] `InteractionInputModule` 正常
- [ ] 多 Surface 正常
- [ ] Radar Compatibility 基础 API 可用
- [ ] Build 自动携带 Bridge + Providers

---

# 30. 后续版本预留

## V1.1 Camera Gesture

- Pinch
- Open Palm
- Fist
- Point
- PointerDown / PointerUp
- Drag

## V1.2 更多 Provider

- Astra
- RealSense
- Azure Kinect
- 其他 LiDAR / Radar

## V1.3 Multi Provider

允许：

```text
Radar + CameraHand
Radar + DepthCamera
多个 Camera Provider
```

需要新增：

- provider routing
- priority
- optional fusion
- cross-provider point namespace

V1 不实现，但现在所有数据结构必须允许这种扩展。

---

# 31. 首个 Codex 执行目标

不要直接让 Codex 一次性完成整个项目。

第一轮严格只做到：

```text
现有 RadarControl
      ↓
Interaction Core
      ↓
Radar Provider Adapter
      ↓
Interaction IPC
      ↓
com.blaze.interaction
      ↓
原有雷达 Sample 正常运行
```

定义为：

> **Gate A：Radar on Interaction Architecture**

Gate A 通过后，再做：

> **Gate B：CameraHand Provider Mock/Replay**

最后：

> **Gate C：CameraHand Real Inference**

这样可以把“架构重构风险”和“AI识别风险”彻底拆开。

---

# 32. Codex 开始开发时可直接使用的指令

```text
你正在维护仓库 blaze-tc/RadarControl。

首先完整阅读：
1. BlazeInteraction_统一感应设备平台_开发规格.md
2. README.md
3. docs/architecture.md
4. docs/protocol.md
5. docs/unity-integration.md
6. src/
7. UnityPackage/com.blaze.radar/
8. tests/

本次开发不是新增第二套摄像头程序，而是将现有 RadarControl 演化为统一 Blaze Interaction 平台。

硬性约束：
- Windows 最终只能有一个 BlazeInteractionBridge.exe。
- Provider 必须是真正的外部 DLL/目录插件。
- V1 提供 Radar 和 CameraHand 两个 Provider。
- V1 同时只激活一个 Provider，但架构预留未来多 Provider。
- Unity 最终只导入 com.blaze.interaction。
- Unity 只读取统一 InteractionPoint + Extensions。
- 必须保留现有 RadarControl 多雷达、多屏、融合、标定、UGUI/2D/3D 输入能力。
- 现有 Radar API 提供兼容层，不允许维护第二套 IPC。
- CameraHand 不允许使用 Python Worker、ProviderHost 或任何第二个 EXE。
- CameraHand AI 必须在 BlazeInteractionBridge.exe 进程内通过 Provider DLL/native DLL 运行。

先不要开发 CameraHand。
第一阶段只完成 Gate A：
1. 固定当前 RadarControl 测试基线。
2. 建立 Blaze.Interaction.Contracts 和 Provider.Abstractions。
3. 建立 ProviderCatalog / ProviderLoader / ProviderManager。
4. 把现有 Radar pipeline 包装为 Radar Provider，不重写雷达协议和 Processing。
5. 建立新的 Interaction IPC。
6. 建立 com.blaze.interaction 的最小 Runtime。
7. 让现有雷达 Sample 经 Interaction 架构重新跑通。
8. 添加回归测试。

每个任务先写测试，完成后运行相关完整测试。
任何 Radar 回归失败都必须先解决，不能跳到 CameraHand。
```

---

# 33. 最终交付结构

```text
BlazeInteractionBridge.exe
Providers/
 ├─ Radar/
 └─ CameraHand/

UnityPackage/
 └─ com.blaze.interaction/

Docs/
 ├─ architecture.md
 ├─ provider-development.md
 ├─ protocol.md
 ├─ unity-integration.md
 ├─ radar-migration.md
 ├─ camera-hand.md
 └─ troubleshooting.md
```

版本：

```text
Blaze Interaction 1.0.0
Provider API 1
Interaction IPC 1
```

---

## 结论

项目最终不再以“雷达控制器”作为核心，而是：

```text
Blaze Interaction Bridge
        ↓
Pluggable Sensor Providers
        ↓
Unified InteractionPoint
        ↓
Single Unity Package
```

Radar 是第一个 Provider，CameraHand 是第二个 Provider。

以后新增任何感应设备的正确方式是：

```text
新增 Provider 插件
```

而不是：

```text
再新增一套 EXE
再新增一份 Unity SDK
再修改所有游戏逻辑
```

这条原则必须贯穿整个项目。
