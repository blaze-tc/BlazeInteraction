# CameraVision Gate B0 仓库差距分析

## 结论

当前 Provider API 1 足以承载 CameraVision Gate B0。CameraVision 可以作为第二个标准 Provider 被现有 Bridge 发现、加载和托管，并通过现有 `InteractionFrame`、Interaction IPC 1 和 `com.blaze.interaction` 把点位送入 Unity。B0 不需要新增 Camera 专属 IPC 消息、Named Pipe、Bridge、Launcher、Unity Package 或业务 EXE，也不需要修改 Radar 算法链。

开始分析时的完整 Release 基线发现一个发生在 CameraVision 改动之前的 Interaction Pipe 关闭竞态：`ReconnectedChangedTopology_DiscardsOldFramePublishedDuringHelloRestart` 在服务停止和写循环并发时可能让 `ObjectDisposedException` 逃逸。它不是 CameraVision 架构缺口，但属于 Gate A 生产链路回归；必须先通过测试先行的最小修复恢复全绿，之后才能实施 B0。

## 当前 Gate A 生产链路

```text
RadarControl 既有设备、配置、标定、过滤、Fusion、Tracking、Touch/Dwell
  -> Blaze.Provider.Radar (Provider API 1)
  -> ProviderManager
  -> BlazeInteractionBridge.exe
  -> Interaction IPC 1 / InteractionFrame
  -> com.blaze.interaction / InteractionManager
  -> InteractionFrameDispatcher / InteractionInputModule / InteractionCameraRouter
  -> Unity UGUI / Physics2D / Physics3D
```

发布结果由 `scripts/publish-interaction-bridge.ps1` 生成并嵌入 `UnityPackage/com.blaze.interaction/Bridge~/win-x64`。现有发布测试要求最终树只有 `BlazeInteractionBridge.exe` 一个业务 EXE，并以 Radar simulation 做真实 IPC smoke。

## Provider API 1 的真实接口和生命周期

- `IInteractionProviderPlugin` 暴露 `Descriptor`、`CreateProvider(ProviderCreateContext)` 和可空的 `SettingsViewFactory`。
- `IInteractionProvider` 暴露 `ProviderInstanceId`、`Status`、`FrameReceived`、`StatusChanged`，并实现 `InitializeAsync`、`StartAsync`、`StopAsync`、`DisposeAsync`。
- 状态模型为 `Created -> Initializing -> Ready -> Starting -> Running -> Stopping -> Stopped`，失败使用 `Faulted`。
- `ProviderInitializationContext` 提供 Unity Hello 建立的不可变 Surface topology 和通用服务容器。
- manifest 为小于等于 64 KiB 的 `provider.json`，必需字段为 `id`、`displayName`、`version`、`providerApiVersion`、`entryAssembly`、`entryType`、`category` 和 `capabilities`。当前只接受 `providerApiVersion: 1`；manifest 与插件 `Descriptor` 必须精确一致。
- `ProviderCatalog` 扫描 `Providers/*/provider.json`；`ProviderLoader` 使用可卸载 `AssemblyLoadContext` 加载插件并共享 Contracts/Abstractions；`BridgeHost` 注册实例工厂，`ProviderManager` 串行执行初始化、启动、停止、取消遗留点位和释放。

## Interaction 数据、Surface 和坐标

- `InteractionPoint` 包含稳定点 ID、Surface/Provider/Instance/Source 标识、`Hover/Down/Move/Up/Cancel` 阶段、归一化坐标、逻辑像素坐标、置信度、Unix 毫秒时间戳和可选扩展。
- 归一化坐标必须位于 `[0, 1]`，逻辑像素坐标必须为有限值；置信度必须位于 `[0, 1]`。
- `InteractionFrame` 包含 Provider/Instance/Surface、严格递增的 per-Surface sequence、时间戳和不可空点位快照。
- `InteractionSurface` 由 Unity Hello 提供 `SurfaceId`、名称、正数逻辑宽高、Primary 标志和 Order。B0 不建立 Camera 专属坐标系；Fake 点直接使用现有 Surface normalized 坐标，并按逻辑宽高计算 pixel 坐标。

## Provider 到 Bridge 与 IPC 的发布方式

Provider 通过 `FrameReceived` 发布标准 `InteractionFrame`。`ProviderManager` 验证 Provider 身份、Surface、sequence 和点位 contract，然后 Bridge 使用唯一 `InteractionPipeServer` 转发。Interaction IPC 1 现有消息只有 Hello、HelloAck、InteractionFrame、Status、ProviderChanged、Ping、Pong、Shutdown 和 Error；CameraVision B0 只使用 `InteractionFrame`，不增加消息类型。

视觉帧沿用 latest-only 语义，Down/Up/Cancel 等生命周期帧沿用可靠发送语义。Provider 切换或断线由现有 ProviderManager/Unity Dispatcher 取消活动点位。

## Radar Provider 和 Unity 消费语义

- Radar Provider 把既有融合后的 PointerBatch 按 Surface 转成一个标准 `InteractionFrame`，保留 `radar-fused-output` Source 和 Radar extension。
- Radar Touch 首帧生成 Down，持续目标生成 Move，达到丢失帧阈值生成 Up；Dwell 在稳定停留期间生成 Hover，达到时长后生成 Down/Up；HoverOnly 只生成 Hover。上述语义由 Radar 自己负责，CameraVision B0 不复用或修改 Radar 状态机。
- Unity `InteractionPipeClient` 只连接一个 Interaction Pipe，发送 Surface topology Hello，并消费标准 HelloAck、InteractionFrame、Status 和 ProviderChanged。
- `InteractionManager` 在主线程取 latest frame；`InteractionFrameDispatcher` 以 `(ProviderInstanceId, SurfaceId, PointId)` 维护点位，Up/Cancel 删除点位，Provider 切换和断线统一 Cancel。
- `InteractionInputModule`、`InteractionCameraRouter` 和 BasicInteraction 示例都按标准 Surface/Point 工作，不依赖 Radar 网络或设备 API。BasicInteraction 当前显示连接、活动 Provider 和点位；B0 只需让它明确显示 CameraVision fake cursor/位置，不需要 Camera 专属客户端。

## CameraVision 的接入点

```text
Windows RGB Camera
  -> CameraCaptureService
  -> LatestFrameSlot(capacity = 1)
  -> Mirror/Rotation transform
  -> FakeVisualDetector
  -> CameraVisionProvider
  -> InteractionPoint / InteractionFrame
  -> 现有 ProviderManager / Bridge / IPC1 / Unity
```

Provider 目录和发布目录分别使用：

```text
providers/CameraVision/Blaze.Provider.CameraVision/
Providers/CameraVision/
```

插件标识采用 `blaze.camera.vision`，默认实例为 `camera-vision-main`。B0 使用 `--provider blaze.camera.vision` 走 Bridge 已有的首选 Provider 选择入口；未指定时仍保持 Radar 为默认 Provider，避免改变现有部署行为。

## 可直接复用的模块

- `Blaze.Interaction.Contracts`：Surface、Point、Frame 和 phase contract。
- `Blaze.Interaction.Provider.Abstractions`：Provider API 1、生命周期和初始化上下文。
- `Blaze.Interaction.Runtime`：manifest 校验、目录发现、插件加载、ProviderManager、sequence/point 验证和切换清理。
- `Blaze.Interaction.Ipc`：唯一 IPC1 编解码、latest-only 帧策略、可靠生命周期帧和客户端鉴权。
- `Blaze.Interaction.Bridge.Wpf`：唯一业务进程、Provider 托管、`--provider` 选择和发布入口。
- `com.blaze.interaction`：一个 Pipe 客户端、标准点位 Dispatcher、EventSystem、Surface Camera 路由、Bridge launcher 和 BasicInteraction 示例。
- 现有 Radar/Interaction/Release/Unity 测试结构和 publish/embedded smoke 脚本。

## Gate B0 需要新增的模块

- `Blaze.Provider.CameraVision` Provider 插件、manifest 和 B0 profile。
- 可测试的 Camera device 枚举和 capture backend；Windows 实现保持进程内运行。
- `CameraFrame` 所有权/释放模型和 frame statistics。
- `LatestFrameSlot`，只保存一个最新帧并释放被覆盖帧。
- Mirror X 与 0/90/180/270 旋转变换。
- disconnected/reconnecting 状态和自动重连循环。
- 与 Interaction Core 解耦的 `IVisualDetector`、`VisualDetection` 和 `FakeVisualDetector`。
- CameraVision Provider 单元/生命周期/发现/IPC 集成测试。
- publish layout 与 embedded Bridge 对第二 Provider 的验证，同时保留 Radar simulation smoke。
- Unity 标准 Frame/示例 fake cursor 测试，不增加 Camera 专属网络代码。
- `docs/camera-vision/gate-b0-report.md`。

Windows 摄像头捕获拟使用 Provider-local 的 OpenCvSharp Windows runtime。它支持当前 .NET 8 Windows Provider 的进程内 `VideoCapture`，并可在后续 Gate 复用；B0 只使用 video I/O 和基础变换，不实现颜色、轮廓、Hand、Tracking 或 Homography。所有 native 资源必须由 Provider 停止/释放路径确定性释放。真实 USB 摄像头枚举、分辨率/FPS 协商和重连仍需硬件验证，自动化测试使用注入的 capture backend，不伪造现场结果。

## Provider API 1 充分性判断

Provider API 1 已提供 B0 所需的全部跨边界能力：初始化时接收 Surface topology、标准状态、启停释放、Frame 事件、Bridge 发现/加载和 Provider 选择。Camera 配置、预览帧、设备重连、frame slot、统计和 detector 都是 Provider 内部责任，不应泄漏到 Core。

因此 B0 对 Contracts、Provider API、Interaction IPC 协议和 Unity runtime 的最小核心变更为：**无**。若实现过程中只有 Camera 专属信息才能跨 IPC 才能工作，应视为设计错误并停止，而不是扩展 IPC1。

## 明确不应修改的模块

- `src/Radar.*` 和 `providers/Radar/Blaze.Provider.Radar` 的设备、配置、标定、过滤、Fusion、Tracking、Touch/Dwell、simulation/replay 逻辑。
- `Blaze.Interaction.Contracts` 的 Point/Frame/Surface 结构和 phase 语义。
- `Blaze.Interaction.Provider.Abstractions` 的 Provider API 1。
- `Blaze.Interaction.Runtime` 的 Camera 特判；不得添加 `if CameraVision`。
- `Blaze.Interaction.Ipc` 的协议消息集合和 Pipe 名称。
- Unity 中不得新增 Camera 专属 Pipe client、Launcher、Package 或设备依赖。
- 不得创建 CameraVision Bridge、Camera Worker、Provider Host、Python worker 或第二个业务 EXE。

现有 Interaction Pipe 关闭竞态的修复只允许修改通用 IPC teardown 错误归一化，并以通用回归测试证明；不得夹带 CameraVision 分支。

## B0 测试与发布验证边界

实施按严格 RED/GREEN 顺序分阶段进行：

1. manifest/discovery 和 lifecycle（含至少 50 次 start/stop）。
2. camera enumeration/open/close、不可用、断线和 reconnect 状态。
3. Mirror/Rotation、LatestFrameSlot 和 frame statistics。
4. FakeVisualDetector 到 InteractionFrame。
5. Bridge/IPC1 到 Unity 标准 Dispatcher/BasicInteraction fake cursor。
6. publish/embedded Bridge 双 Provider 布局与 smoke，同时运行全部 Radar/Interaction 回归和 Unity Editor/PlayMode 测试。

完整验证必须包含 solution tests、`scripts/test.ps1`、Unity Package All + Samples、publish、embedded Bridge smoke，并在报告中保留命令和计数。无摄像头硬件的结论必须记录为 `REQUIRES HARDWARE VALIDATION`。

## B1-B6 禁区

B0 不实现 Four-Point Homography、Surface polygon、HSV/颜色检测、Hand backend/landmark、VisualTargetTracker、PositionFilter、Camera 配置 UI、预览 UI、标定 UI、生产 profile switching、性能调优或现场稳定性验收。上述能力只保留接口可演进性，不创建空壳或提前编码。
