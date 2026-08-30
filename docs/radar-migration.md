# 从 com.blaze.radar 迁移到 com.blaze.interaction

Gate A 的目标不是同时维护两套 Unity SDK，而是把 Radar 作为统一平台的第一个 Provider。迁移后只安装 `com.blaze.interaction`。

## 包和进程

| 旧项 | Gate A |
| --- | --- |
| `com.blaze.radar` | `com.blaze.interaction` 1.1.1 |
| `RadarBridge.exe` | `BlazeInteractionBridge.exe` |
| `Yuexin.RadarBridge` / Radar IPC 2 | `Blaze.InteractionBridge` / Interaction IPC 1 |
| 内嵌 Radar 专用 Runtime | Provider-neutral Runtime + 外置 `Providers/Radar` |
| Radar 专用 Pipe Client/Launcher | 唯一 `InteractionPipeClient` / `InteractionBridgeLauncher` |

先从 `Packages/manifest.json` 删除旧包，再移除项目内复制的旧 Radar Runtime/Samples。不要同时安装两个包，也不要让旧 `RadarBridge.exe` 与新 Bridge 竞争设备或混用 Pipe。

## 类型映射

| 旧类型 | 推荐类型 |
| --- | --- |
| `RadarPointerPhase` | `InteractionPhase` |
| `RadarScreenInfo` | `InteractionSurface` |
| `RadarScreenPointer` | `InteractionPoint` |
| `RadarScreenPointerFrame` / `RadarPointerBatchPayload` | 每 Surface 一个 `InteractionFrame` |
| `RadarBridgeLauncher` | `InteractionBridgeLauncher` |
| `RadarInputModule` | `InteractionInputModule` |
| `RadarRuntimeSettings` | `InteractionRuntimeSettings` |
| `RadarFrameDispatcher` | `InteractionManager` / `InteractionFrameDispatcher` |
| `RadarScreenCameraRouter` | `InteractionCameraRouter` |

`RadarBridgeLauncher`、`RadarInputModule`、`RadarRuntimeSettings` 和 `RadarFrameDispatcher` 仍以 `[Obsolete]` wrapper 提供过渡，但它们委托给同一 Interaction runtime。不存在兼容版 `RadarPipeClient`；任何依赖它的业务代码必须改为订阅 `InteractionManager`。

## 事件迁移

推荐直接使用：

```csharp
var manager = InteractionManager.Instance;
manager.FrameReceived += OnFrame;
manager.PointAdded += OnPointAdded;
manager.PointUpdated += OnPointUpdated;
manager.PointRemoved += OnPointRemoved;
manager.ProviderChanged += OnProviderChanged;
manager.ErrorReceived += OnError;
```

兼容 `RadarFrameDispatcher` 会把一个 Interaction frame 映射为一个 `RadarScreenPointerFrame`，保留 frame sequence/timestamp 和所有 points。Screen name、width/height、Primary、Order 来自 `InteractionManager.Surfaces` 的 Hello topology；不会从最大 point 坐标推断分辨率。

## 配置迁移

Unity topology 迁移到 **Project Settings > Blaze Interaction**：Screen 改称 Surface，但稳定 ID、逻辑宽高、Primary 与 Order 语义不变。

F10/F20、IP/NIC、transform、filter、calibration、OutputRect、fusion、tracking、Touch/Dwell 仍由 Radar Provider 使用既有 Schema 2 配置。内嵌 payload 默认位于：

```text
Bridge~/win-x64/Providers/Radar/profiles/radar-default.json
```

不要把 Radar 参数搬进 Unity Runtime，也不要在迁移时改 F10/F20 解码、标定或融合阈值。先用原配置证明 407 项 Radar 回归与 Simulation，再进行现场调参。

## 场景迁移

1. 删除旧 Radar EventSystem/InputModule，只创建一次 Blaze Interaction Runtime。
2. 将旧 Screen ID 原样录入 Surface topology。
3. 把 Camera 绑定迁移到 `InteractionCameraRouter`；保留 `targetDisplay`、`pixelRect`、RenderTexture、LayerMask 和距离。
4. 导入新的 Basic Interaction 与 Multi-Surface Routing Samples，删除旧 Sample 副本。
5. 先运行 Simulation，检查每个 Surface 的 Down/Move/Up/Cancel；再连接真实 Radar Provider。
6. 检查 FRONT F1/F2 overlap 仍只输出一个稳定 point，不出现重复 Click。

## 行为保证与非保证

自动化已证明：Radar 设备/配置/处理/旧桥接 407 项通过；兼容层保留批次、sequence、timestamp 和 topology；Interaction Unity PlayMode 44 项通过。

迁移不等于完成现场验收。真实 F10/F20、三投影四雷达、网卡重连、投影对齐和长时间稳定性仍需在目标硬件上执行。CameraHand 不在本迁移范围内。
