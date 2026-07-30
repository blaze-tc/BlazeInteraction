# VRCave 多屏幕、多雷达融合设计

- 日期：2026-07-21
- 目标版本：Blaze Radar SDK 1.2.0
- IPC 协议：v2
- 状态：设计章节已确认，等待书面设计终审

## 1. 背景

RadarControl 1.1.5 采用单雷达、单处理管线和单 Unity 屏幕模型。VRCave 现场可能同时存在多块比例、分辨率和安装方向不同的投影墙，每块墙可能由一个或多个 FaseLase F10/F20 雷达覆盖。例如：

- 正面墙比例为 8:3，使用两个雷达，覆盖区域可以拼接且可以重叠。
- 左右墙比例为 4:3，各使用一个雷达。
- Unity 使用不同 Camera 渲染不同墙面；现场既可能使用多个独立 Display，也可能使用一块超宽桌面或 RenderTexture。

本设计把屏幕作为数据聚合边界：雷达先独立完成解析和物理区域处理，再映射到所属屏幕的像素区域；同屏雷达在 Bridge 内完成重叠去重、跟踪和 Pointer 生命周期，Unity 只接收最终屏幕数据。

## 2. 目标

1. Unity 编辑器可以手动配置任意数量的逻辑屏幕，不写死三屏。
2. Unity 是屏幕 ID、名称、顺序和默认分辨率的唯一来源。
3. 一个 RadarBridge 进程统一管理所有屏幕和雷达。
4. 每个屏幕可以配置任意数量雷达，雷达可以覆盖独立区域或重叠区域。
5. 每个雷达保留独立连接、变换、过滤、区域、屏蔽、标定和显示参数。
6. 每个屏幕保留独立分辨率、融合、跟踪、平滑和交互参数。
7. 同一个人在同屏雷达重叠区只输出一个目标；跨雷达移动时保持 Pointer ID 稳定。
8. Bridge 同时输出屏幕内归一化坐标和屏幕内像素坐标。
9. Unity SDK 提供单点事件和单屏整帧事件，并提供屏幕到 Camera 的路由和射线辅助方法。
10. BasicInteraction 验证旧主屏 EventSystem 路径；新增 Multi-Screen Camera Routing Sample 验证多屏事件、Camera 射线、世界坐标和粒子。
11. Schema 1 单屏配置自动迁移，现有单屏 Unity API 保持可用。

## 3. 非目标

- 打包后的 Unity Player 不支持通过外部 JSON 动态增删屏幕。屏幕拓扑在 Unity 编辑器中配置并随 Player 构建固化。
- 1.2.0 不实现自动联合标定；现场通过每雷达物理四角和输出像素矩形完成映射。
- 1.2.0 不实现跨屏人员身份连续跟踪。Pointer ID 只要求在同一屏幕内稳定。
- 1.2.0 不实现多雷达同步录制容器。录制和回放仍以选中的单个雷达为单位，现有 `.radarrec` 可继续使用。
- Unity 不承担雷达重叠去重和融合算法。

## 4. 已确认的核心决策

### 4.1 屏幕拓扑所有权

Unity `RadarRuntimeSettings` 中的屏幕列表是唯一权威来源。每个屏幕定义包含：

- 稳定 `ScreenId`，建议格式为小写字母、数字、短横线或下划线，长度 1–64。
- 显示名称。
- 编辑器默认逻辑宽度和高度。
- 是否启用。
- 是否为主屏。
- 排列顺序。

Unity Inspector 支持新增、复制、删除和排序任意数量屏幕。必须且只能有一个启用的主屏。Bridge 不允许增删 Unity 屏幕，只能为同步到的屏幕配置有效分辨率、雷达和处理参数。

### 4.2 Camera 路由

采用屏幕—Camera 路由层。SDK 始终提供屏幕本地坐标，场景中的 `RadarScreenCameraRouter` 决定屏幕对应：

- 独立 Unity Display 的 Camera；
- 一块超宽窗口中的 Camera `pixelRect`；
- 输出到 RenderTexture 的 Camera。

同一 SDK 不绑定现场投影拼接方式。

### 4.3 进程模型

采用一个 RadarBridge 进程。Bridge 内部按屏幕组织多个雷达管线，并通过一条 Named Pipe 向一个 Unity Player 发送多屏批次。不会为每块屏幕或每个雷达启动独立 Bridge 进程。

### 4.4 雷达输出映射

每个雷达先使用现有物理四角区域映射到 0–1 局部坐标，再映射到所属屏幕的可配置像素矩形：

```text
OutputRect = X, Y, Width, Height
PixelX = X + LocalNormalizedX * Width
PixelY = Y + LocalNormalizedY * Height
```

`OutputRect` 的 `X`、`Y` 使用屏幕左下角为原点，X 向右、Y 向上。输出矩形必须完全位于屏幕逻辑分辨率内。不同雷达的矩形允许重叠。

## 5. 系统架构

### 5.1 Unity 模块

1. `RadarRuntimeSettings`
   - 保存屏幕拓扑、主屏和 Bridge/IPC 启动设置。
   - 不保存场景 Camera 引用。

2. `RadarFrameDispatcher`
   - 维护一个 PipeClient。
   - 在 Unity 主线程消费多屏批次。
   - 保存每个屏幕的最新帧。
   - 派发单屏整帧、单点和旧版主屏事件。

3. `RadarInputModule`
   - 默认消费主屏数据，保持 BasicInteraction 行为。
   - 增加可选 `ScreenId` 过滤，用于指定逻辑屏幕。

4. `RadarScreenCameraRouter`
   - 在场景中显式配置 `ScreenId → Camera`。
   - 把逻辑屏幕像素转换到 Camera 实际像素区域。
   - 提供射线和 Physics.Raycast 辅助方法。

### 5.2 Bridge 模块

1. `RadarBridgeCoordinator`
   - 管理屏幕同步、传感器生命周期、配置保存、IPC 输出和全局关闭。

2. `RadarSensorPipeline`
   - 每个雷达一个实例。
   - 管理 Real、Simulation 或 Replay 数据源。
   - 执行解析、变换、过滤、聚类和屏幕像素映射。

3. `RadarScreenFusionEngine`
   - 每个屏幕一个实例。
   - 维护各雷达的最新检测结果。
   - 执行过期淘汰、跨雷达去重、屏幕级跟踪和平滑。

4. `MultiScreenIpcPublisher`
   - 按固定频率创建包含所有启用屏幕的批次。
   - 即使屏幕没有 Pointer，也发送零点帧。

5. `MainViewModel`
   - 只负责选中屏幕、选中雷达和 UI 命令。
   - 不直接承载传感器连接或融合算法。

## 6. 配置模型

Bridge 配置升级为 Schema 2：

```text
RadarAppConfiguration
├── SchemaVersion = 2
├── Ipc
└── Screens[]
    ├── ScreenId
    ├── ResolutionMode = FollowUnityDefault | Override
    ├── WidthPixels / HeightPixels
    ├── Fusion
    ├── Tracking
    ├── Interaction
    └── Sensors[]
        ├── SensorId / DisplayName / Enabled
        ├── SourceMode = Real | Simulation | Replay
        ├── Device
        ├── Transform
        ├── Range / ActivePolygon / MaskedPolygons / EdgeDeadZones
        ├── VisualizationRangeMeters
        ├── Clustering
        ├── Calibration
        └── OutputRectPixels
```

屏幕名称、启用状态和排列顺序来自 Unity，只读显示。分辨率默认跟随 Unity；用户在 Bridge 手动编辑后切换为 `Override`。Bridge 提供“恢复 Unity 默认分辨率”命令。

Unity 删除屏幕后，Bridge 保留其传感器配置但将其标记为未关联，不连接、不处理、不输出。以后以相同 `ScreenId` 重新加入时自动恢复。用户可以在 Bridge 中手动永久清理未关联配置。

### 6.1 Schema 1 迁移

首次加载 Schema 1 时：

1. 在原配置同目录保存一次带时间戳的 Schema 1 备份。
2. 创建 `main` 屏幕，使用 Unity 主屏默认分辨率；Unity 尚未连接时使用 1920×1080 临时默认值。
3. 创建 `sensor-1`，完整复制旧版 Device、Transform、Range、Clustering、Calibration 和显示参数。
4. 复制旧 Tracking 和 Interaction 到 `main` 屏幕。
5. 将输出矩形设置为整个 `main` 屏幕。
6. 原子保存 Schema 2 配置。

## 7. 屏幕拓扑同步

Unity Hello v2 携带所有启用屏幕定义。Bridge 收到后按 `ScreenId` 对账：

- 新 Screen ID：创建跟随 Unity 默认分辨率的 Bridge 屏幕配置，雷达列表为空。
- 已存在 Screen ID：更新名称、顺序和 Unity 默认分辨率；Bridge 分辨率 Override 保持不变。
- Unity 已移除 Screen ID：为该屏幕的活动 Pointer 各发送一次 `Up`，随后发送零点帧，标记为未关联并停止所属雷达。
- 重复或非法 Screen ID：拒绝握手并返回明确错误。

Bridge 在 Unity 连接前可以显示上次拓扑，但标记为“等待 Unity 确认”；只有本次 Hello 中存在的屏幕可以连接雷达和输出数据。

## 8. 雷达处理与融合

### 8.1 每雷达管线

```text
TCP / Simulation / Replay
→ 协议解析
→ 安装变换
→ 距离与角度过滤
→ 有效区域与屏蔽区过滤
→ 单雷达聚类
→ 物理四角映射到局部 0–1
→ 映射到屏幕输出像素矩形
→ 发布 ScreenDetection
```

每个雷达使用容量为 1 的最新帧通道，满时丢弃旧帧，防止慢消费者造成延迟累积。一个雷达的异常不得结束其他雷达或其他屏幕的任务。

### 8.2 屏幕融合默认值

- 输出频率：30 Hz。
- 检测过期时间：150 ms。
- 跨雷达融合距离：80 px。
- 屏幕跟踪最大关联距离：160 px。
- 确认帧：2。
- 丢失帧：3。
- 平滑系数：0.5。

所有值按屏幕独立配置并校验。

### 8.3 去重算法

1. 每个输出周期取得各雷达未过期的最新检测。
2. 按 SensorId、X、Y 排序，保证结果可复现。
3. 生成来自不同雷达且距离不超过融合阈值的候选对。
4. 从最近候选开始合并；一个融合组不能包含同一雷达的两个检测。
5. 融合坐标使用组内检测的等权平均值。
6. 未被合并的检测独立保留。
7. 融合结果进入屏幕级 Tracker，再生成 Pointer 阶段。

该规则保证同一雷达检测到的两个近距离人员不会被融合，同时允许两个或更多雷达对同一人员的观察合并。

### 8.4 ID 与坐标规则

- Pointer ID 在一个屏幕内稳定，不承诺跨屏稳定。
- 完整身份键为 `(ScreenId, PointerId)`。
- `NormalizedX = PixelX / WidthPixels`。
- `NormalizedY = PixelY / HeightPixels`。
- 屏幕像素坐标与 Unity `Screen`/`Camera.ScreenPointToRay` 一致：左下角为原点，X 向右、Y 向上；归一化值 0 对应左/下边界，1 对应右/上边界。
- 修改屏幕分辨率、雷达输出矩形、物理标定或屏幕拓扑时，对该屏幕每个活动 Pointer 先且仅发送一次 `Up`，下一批发送零点帧，再重置该屏幕 Tracker。

## 9. IPC v2

### 9.1 Hello

```text
HelloPayload
├── UnityProcessId
├── UnityVersion
└── Screens[]
    ├── ScreenId
    ├── Name
    ├── DefaultWidthPixels
    ├── DefaultHeightPixels
    ├── IsPrimary
    └── Order
```

### 9.2 HelloAck

包含 Bridge 版本、协议 v2、连接状态、拓扑摘要和 `multi-screen` 能力标记。

### 9.3 PointerBatch

```text
PointerBatchPayload
└── Screens[]
    ├── ScreenId / ScreenName
    ├── WidthPixels / HeightPixels
    └── Pointers[]
        ├── PointerId
        ├── Phase
        ├── NormalizedX / NormalizedY
        ├── PixelX / PixelY
        ├── Confidence
        └── TimestampUnixMilliseconds
```

每个批次包含所有启用屏幕。外层 Envelope 的 Sequence 和 Timestamp 是批次序号与时间。零点屏幕保留在批次中。

IPC v1 客户端或服务端与 v2 握手时必须返回版本不兼容错误，不允许静默降级为错误的多屏语义。

## 10. Unity SDK API

### 10.1 数据模型

```csharp
[Serializable]
public sealed class RadarScreenDefinition
{
    public string screenId;
    public string displayName;
    public int defaultWidthPixels;
    public int defaultHeightPixels;
    public bool enabled;
    public bool isPrimary;
}

public sealed class RadarScreenInfo { /* ID、名称、有效分辨率 */ }
public sealed class RadarScreenPointer { /* ID、阶段、归一化/像素坐标、置信度、时间 */ }
public sealed class RadarScreenPointerFrame { /* 批次序号、时间、屏幕、点列表 */ }
```

### 10.2 Dispatcher 事件

```csharp
public event Action<RadarScreenInfo, RadarScreenPointer> ScreenPointerReceived;
public event Action<RadarScreenPointerFrame> ScreenFrameReceived;
public IReadOnlyDictionary<string, RadarScreenPointerFrame> LatestScreenFrames { get; }
```

- 所有事件在 Unity 主线程触发。
- 一个订阅者抛出异常时记录错误，但继续通知其他订阅者。
- `ScreenFrameReceived` 对每块屏幕每批次触发一次，包括零点帧。
- `ScreenPointerReceived` 对该帧中的每个 Pointer 触发。

### 10.3 旧 API 兼容

- 保留现有 `PointerFrameReceived`。
- Dispatcher 把主屏的 v2 Frame 转换为旧 `RadarPointerFrameMessage` 后触发旧事件。
- `RadarInputModule` 默认使用主屏；可选 `ScreenId` 时只消费指定屏幕。
- Schema 1 单屏项目不需要修改现有场景引用。

### 10.4 Camera 路由

`RadarScreenCameraRouter` 使用显式场景绑定：

```text
RadarScreenCameraBinding
├── ScreenId
├── Camera
├── RaycastLayerMask
└── MaximumRayDistance
```

逻辑屏幕像素先换算到 Camera 像素区域：

```text
cameraX = camera.pixelRect.x + pixelX / logicalWidth  * camera.pixelRect.width
cameraY = camera.pixelRect.y + pixelY / logicalHeight * camera.pixelRect.height
```

对于 RenderTexture 使用 Camera 的有效像素宽高。Router 提供：

```csharp
bool TryGetCamera(string screenId, out Camera camera);
bool TryCreateRay(RadarScreenInfo screen, RadarScreenPointer pointer, out Ray ray);
bool TryRaycast(RadarScreenInfo screen, RadarScreenPointer pointer, out RaycastHit hit);
```

用户可以直接订阅 Dispatcher 委托，不强制使用 Router。

## 11. RadarBridge UI

主窗口采用三列工作区：

1. 左列
   - Unity 屏幕列表：名称、分辨率、在线雷达数。
   - 选中屏幕的雷达列表：名称、来源模式、连接状态、频率。
   - 添加/删除雷达、连接全部、断开全部、启动全部模拟。

2. 中列
   - 区域 1：选中雷达的原始点云，仅观察传感器回波。
   - 区域 2：选中屏幕的所有雷达映射点、输出矩形、重叠区、融合目标和 Pointer。
   - 不同雷达使用稳定颜色；融合目标使用独立高亮颜色。

3. 右列
   - 屏幕参数页：有效分辨率、融合距离、过期时间、跟踪、平滑、交互。
   - 雷达参数页：现有连接、型号、变换、范围、区域、屏蔽、聚类、标定、输出矩形。

4. 底部日志
   - 支持按屏幕和雷达过滤。
   - 所有日志带 `[ScreenId/SensorId]` 或 `[ScreenId/FUSION]` 标签。

Bridge 继续使用启动前软件渲染、像素对齐和有界日志，避免投影电脑上的 WPF 脏区问题。

## 12. 数据源、模拟、录制与回放

- 每个雷达配置 `SourceMode`：Real、Simulation 或 Replay。
- Real 雷达可以单独连接/断开，也可以按屏幕或全局连接。
- Simulation 雷达生成可区分的轨迹，允许配置多个模拟雷达输出矩形和重叠区。
- “一键模拟”将所有 Unity 已关联屏幕中的已启用雷达切换为 Simulation，持久化配置并立即启动替换管线。
- “停止模拟”停止所有已关联屏幕中运行或等待切换的 Simulation 管线，并保留其数据源模式。
- 录制和回放作用于当前选中雷达，继续使用现有 `.radarrec` 文件格式。
- 同一屏幕可以同时运行 Real 与 Simulation 雷达，但 UI 和日志必须明确标记，避免现场误把模拟数据当作真实数据。

## 13. Samples

### 13.1 Basic Interaction

- 保留 UGUI、PhysicsRaycaster 和 Physics2DRaycaster 的原生 EventSystem 验证。
- 使用主屏兼容路径。
- 日志增加 ScreenId、屏幕名称、逻辑分辨率和像素坐标。
- 保留鼠标调试模式和详细有界日志。

### 13.2 Multi-Screen Camera Routing

Sample 默认包含 LEFT、FRONT、RIGHT 三个逻辑屏幕，但 SDK 不限制屏幕数量。

- LEFT：1920×1440。
- FRONT：4096×1536。
- RIGHT：1920×1440。
- 三台 Camera 和三块带 Collider 的墙面组成 CAVE 场景。
- `RadarScreenCameraRouter` 显式绑定三个 ScreenId。
- 每个 `(ScreenId, PointerId)` 对应一个池化粒子实例。
- Hover、Down、Move 更新粒子；Up 或丢失停止并回收。
- Router 使用 `ScreenPointToRay` 命中墙面，在世界命中位置显示粒子。
- 每块屏幕有实时点位覆盖层。
- 右侧日志显示屏幕、Pointer、逻辑像素、Camera 像素、Ray 和世界命中。

Sample 提供两种运行方式：

1. 本地 SDK 模拟：不依赖 Bridge，验证委托、Camera 路由、世界坐标和粒子。
2. 完整 IPC：关闭本地模拟，使用 Bridge 多雷达模拟或真实雷达验证完整链路。

## 14. 错误处理与状态隔离

- 单雷达连接、解析、CRC 或重连错误只影响自身。
- 数据超过屏幕 `SensorDataMaxAge` 后不参与融合。
- 雷达断开或过期后，如果同屏其他雷达仍覆盖该目标，则由屏幕级 Tracker 保持 Pointer；否则按 LostFrames 产生一次 Pointer Up。
- 无雷达、无有效点或全部雷达离线时仍发送零点屏幕帧。
- 重复 SensorId、非法 IP/端口、越界输出矩形、无效融合/跟踪参数禁止保存。
- Unity 中空/重复 ScreenId、非法分辨率、零个或多个主屏时 Inspector 显示错误，Player 构建失败。
- 配置使用临时文件加原子替换。
- 分辨率、映射或标定热变更先为旧 Pointer 发送一次 Up，再应用新状态。
- Bridge 父进程监控和 Unity 退出关闭行为保持不变。

## 15. 日志与指标

Bridge 至少记录：

```text
[FRONT/F1] TCP 状态、CRC、丢弃字节、原始点、有效点、候选目标
[FRONT/F2] TCP 状态、CRC、丢弃字节、原始点、有效点、候选目标
[FRONT/FUSION] 输入检测、合并组、输出目标、Pointer ID
[IPC] 批次序号、延迟、各屏 Pointer 数
```

Unity `Player.log` 和 Sample 日志至少记录：

```text
[Blaze Radar/FRONT] frame、Pointer 数、逻辑分辨率、丢弃批次
[Blaze Radar/FRONT/P7] phase、normalized、pixel、cameraPixel、worldHit
```

连续 Move 日志节流，连接、错误、Down、Up 和配置变化立即记录。所有 UI 日志保持有界。

## 16. 验证策略

### 16.1 .NET 单元与集成测试

- Schema 1 到 Schema 2 无损迁移和备份。
- 任意屏幕/雷达数量、ScreenId/SensorId、分辨率和输出矩形验证。
- 每雷达最新帧通道不积压。
- 左右拼接无误融合。
- 重叠区双雷达同目标合并。
- 同雷达近距离双目标不合并。
- 三雷达融合组不包含同源重复检测。
- 过期检测移除。
- 跨雷达交接时 Pointer ID 稳定。
- 单雷达断线、重连、Pointer `Up` 和异常隔离。
- 热变更先为每个活动 Pointer 发送一次 `Up` 并重置 Tracker。
- IPC v2 Hello、HelloAck、PointerBatch、零点屏幕和版本拒绝。
- WPF 屏幕/雷达选择、双点云、参数页、日志过滤和软件渲染。

### 16.2 Unity 兼容与编辑器测试

- Package JSON、版本和嵌入 Bridge 完整性。
- v2 JSON 模型和多屏最新帧缓存。
- 两级事件、主线程派发和监听者异常隔离。
- 主屏旧 `PointerFrameReceived` 转换。
- `RadarInputModule.ScreenId` 过滤。
- Settings Inspector 的新增、复制、删除、排序和验证。
- BuildProcessor 拒绝非法拓扑，并复制当前标签的 Bridge。

### 16.3 Unity PlayMode 测试

- 多屏 Dispatcher 各自收到帧和零点帧。
- Camera pixelRect、独立 Display 和 RenderTexture 坐标换算。
- ScreenPointToRay 命中正确墙面。
- 粒子按 `(ScreenId, PointerId)` 创建、移动、停止和复用。
- BasicInteraction 的 Button、Toggle、Slider、2D 和 3D 事件保持通过。

### 16.4 端到端验收

使用三屏四雷达模拟配置：

- LEFT：L1。
- FRONT：F1 与 F2，输出矩形有重叠。
- RIGHT：R1。

验收结果必须同时看到：

- Bridge 四个雷达独立状态。
- FRONT 重叠目标从两个检测融合为一个 Pointer。
- Unity 每批次收到三块屏幕。
- 三台 Camera 对应墙面产生粒子和日志。
- 停止 F1 后 F2 和其他屏幕继续工作。
- 恢复 F1 后不产生重复 Pointer 或误点击。

真实三投影、四雷达现场还需执行 8 小时稳定性、重叠阈值调优、网卡故障和 Player 全屏输出验收。

## 17. 发布

- SDK 版本：1.2.0。
- Bridge 版本：1.2.0。
- IPC 协议：v2。
- 包名保持 `com.blaze.radar`。
- 命名空间保持 `Blaze.Radar`。
- Named Pipe 名称保持 `Yuexin.RadarBridge`。
- Git 标签：`v1.2.0`。
- Unity Git URL：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.0
```

发布包继续内嵌完整 self-contained `RadarBridge.exe` 目录，并通过版本标记、SHA-256、启动和父进程退出测试。

## 18. 完成标准

只有满足以下条件才可标记 1.2.0 完成：

1. 所有新增和现有自动测试通过，Release 构建 0 错误。
2. Unity 2021.3 实际导入后包版本正确，Console 无编译错误。
3. 两个 Samples 都能运行，并明确区分本地模拟与 IPC 数据。
4. 三屏四雷达模拟端到端验收通过。
5. 内嵌 Bridge 与源码版本一致，Player 构建复制的是当前包路径。
6. 安装、升级、屏幕配置、雷达配置、Camera 路由和现场日志文档完整。
7. GitHub `main` 与 `v1.2.0` 标签指向同一已验证提交。
