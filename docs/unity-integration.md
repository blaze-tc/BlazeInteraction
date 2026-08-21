# Unity 集成：com.blaze.interaction 1.0.0

## 安装

Unity 要求 2021.3 LTS。开发环境可用 Package Manager 的 **Add package from disk...** 选择 `UnityPackage/com.blaze.interaction/package.json`；Git 安装使用：

```text
https://github.com/blaze-tc/BlazeInteraction.git?path=/UnityPackage/com.blaze.interaction
```

正式项目必须固定已审核 tag/commit。安装后 Package Manager 应显示 `Blaze Interaction SDK 1.0.0`。不要同时安装旧 `com.blaze.radar`，也不要在 `Assets/` 保留旧 Radar Runtime 副本。

依赖为 UGUI 1.0.0 与 Newtonsoft Json 3.0.2。

## Surface topology

在 **Project Settings > Blaze Interaction** 配置 Surface：

- `Surface ID` 非空、唯一并长期稳定；
- logical width/height 为正，描述 Provider 与 Unity 共用的逻辑像素空间；
- 启用 Surface 的 `Order` 唯一；
- 所有启用 Surface 中恰好一个 Primary；
- 禁用 Surface 不进入 Hello。

示例 LEFT、FRONT、RIGHT 是三个 Surface。Radar F1/F2 可以同属 FRONT，并在 Radar Provider 内先融合；Unity 只接收 FRONT 的一个融合 frame。

## 创建 Runtime

执行 **GameObject > Blaze Interaction > Create Runtime**。标准场景包含：

- `InteractionBridgeLauncher`：探测/启动 Bridge、发送 Hello、每帧调用 `InteractionManager.Tick()`；
- `InteractionInputModule`：把 point lifecycle 映射为 UGUI、Physics2D、Physics3D 事件；
- `InteractionCameraRouter`：按 Surface ID 选择 Camera，并把 logical pixel 映射到 Camera pixel area；
- 一个 EventSystem。

场景只允许一个启用的 Interaction Input Module/EventSystem。Canvas 使用 `GraphicRaycaster`；需要 2D/3D 命中的 Camera 添加对应 raycaster/collider。

## Bridge 生命周期

默认 Pipe 为 `Blaze.InteractionBridge`。Launcher 先探测现有 Bridge：

- 可连接时复用，不重复启动；
- 不可连接且 `Auto Start` 为真时，从当前 Resolved Package 的 `Bridge~/win-x64/BlazeInteractionBridge.exe` 启动；
- 启动参数包含 Unity PID、Pipe Name 和 minimized；
- `Exit Bridge With Unity` 只关闭本 Launcher 启动并拥有的进程。

Editor 可显式配置 Bridge executable；Player 则从构建输出的 `BlazeInteractionBridge/` 解析。不要把旧 `RadarBridge.exe` 路径填入 Interaction 设置。

## Runtime API

`InteractionManager.Instance` 提供：

- `Connect(HelloPayload)` / `DisconnectAsync()`；
- `Tick()` 主线程排空；
- `Surfaces`、`Points`、`ActiveProvider`、`IsConnected`、`DroppedFrameCount`；
- `FrameReceived`、`PointAdded`、`PointUpdated`、`PointRemoved`；
- `ProviderChanged`、`ConnectionChanged`、`ErrorReceived`。

Manager 的 `Surfaces` 来自本次 Hello，Camera/兼容层应使用它获取名称、逻辑分辨率、Primary 和 Order，不能从 point 坐标反推 topology。

Unity client 会保留 Down/Up/Cancel 的 FIFO 顺序，只合并相邻 Hover/Move/空视觉帧。不要假设每次后台读都对应一次 `Update`，也不要从 `DroppedFrameCount` 推断生命周期边被丢弃。

## Camera 路由

每个 Surface ID 绑定一个 Camera：

- 多物理 Display：设置 Camera `targetDisplay`，Player 启用对应 Display；
- 同一 Display 拼接：设置 Camera `pixelRect`；
- 投影映射或后处理：使用独立 RenderTexture，纹理尺寸/比例匹配 Surface。

`InteractionCameraRouter` 先按 logical resolution 归一化 pixel，再映射到 Camera pixel rect 或 RenderTexture。LayerMask 与最大射线距离由绑定项控制。多个 Camera 不应消费同一个 Surface，除非项目明确处理重复命中。

## Samples

**Basic Interaction**：单 Surface 的 UGUI/2D/3D 基础交互。先用 Radar Simulation 检查 Hover、Down、Move、Up、Cancel 和断线清理。

**Multi-Surface Routing**：一个 Interaction 连接驱动三个 Surface/Camera，验证 `pixelRect`/RenderTexture 路由。示例从 `InteractionManager.Surfaces` 读取逻辑分辨率，不从点坐标构造屏幕。

两个 Sample 各自只包含一个 Runtime/EventSystem。导入 Sample 时不要同时保留旧 Radar Sample 的 EventSystem。

## Player Build

构建处理器从当前 Package Manager Resolved Path 校验并复制完整 `Bridge~/win-x64`：

1. 删除 Player 目标内旧 `BlazeInteractionBridge/`；
2. 校验 package/Bridge/Provider 版本、manifest、入口类型和恰好一个 EXE；
3. 复制全部依赖与 `Providers/Radar/`；
4. 重新计算并验证 SHA-256。

只复制 `BlazeInteractionBridge.exe` 会导致运行失败。发布后使用 `scripts/test-embedded-bridge.ps1` 做真实 Hello/HelloAck 和父进程退出冒烟。

## Radar 兼容层

`Blaze.Radar` namespace 中保留有限 Obsolete wrapper：Launcher/InputModule 继承唯一 Interaction 实现，FrameDispatcher 订阅 `InteractionManager.FrameReceived` 并按一帧一帧批量映射。兼容层没有 `RadarPipeClient`、没有第二个进程启动器，也不提供旧 Radar Editor 配置面板。迁移清单见 [radar-migration.md](radar-migration.md)。
