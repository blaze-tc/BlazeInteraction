# Unity 集成：com.blaze.interaction 1.1.0

## 安装

推荐从 GitHub Release 下载 `com.blaze.interaction-1.1.0.tgz`，在 Package Manager 中选择 **Add package from tarball...**。也可使用固定 Git URL：

```text
https://github.com/blaze-tc/BlazeInteraction.git?path=/UnityPackage/com.blaze.interaction#v1.1.0
```

依赖为 UGUI `1.0.0` 与 Newtonsoft Json `3.0.2`。不要同时安装旧 `com.blaze.radar`。

## Surface topology

在 **Project Settings > Blaze Interaction** 配置 Surface：

- ID 非空且唯一；
- logical width/height 为正；
- 启用 Surface 的 Order 唯一；
- 所有启用项恰好一个 Primary。

Surface 是 Provider 与 Unity 的逻辑像素合同。Radar 可把多台雷达融合到一个 Surface；CameraVision 把标定后的手中心/骨骼点映射到当前 Primary Surface 分辨率。

## 创建 Runtime

执行 **GameObject > Blaze Interaction > Create Runtime**。标准对象包含：

- `InteractionBridgeLauncher`：解析项目级数据目录和 Pipe，探测/启动 Bridge，发送 Hello。
- `InteractionInputModule`：映射 UGUI、Physics2D、Physics3D 事件。
- `InteractionCameraRouter`：Surface → Camera/Display/pixelRect/RenderTexture。
- 一个 EventSystem。

场景只允许一个启用的 Interaction Input Module/EventSystem。

## 项目级配置隔离

Editor 使用 `<Unity项目>/Library/BlazeInteraction`，Player 使用 `Application.persistentDataPath/BlazeInteraction`。Launcher 将该目录通过 `--data-root` 传给 Bridge，并由目录哈希生成项目专属 Pipe 名。

因此不同 Unity 项目、不同打包 Player 的 Provider 选择和配置不会相互覆盖。`InteractionRuntimeSettings.ProfilePath` 为空时使用 Provider 项目级默认配置；相对路径按当前环境根解析。

## Runtime API

`InteractionManager.Instance` 提供：

- `Surfaces`、`Points`、`ActiveProvider`、`IsConnected`、`DroppedFrameCount`；
- `FrameReceived`；
- `PointAdded`、`PointUpdated`、`PointRemoved`；
- `ProviderChanged`、`ConnectionChanged`、`ErrorReceived`。

所有事件在 `Tick()` 所在 Unity 主线程触发。不要从后台 Pipe 线程访问 UnityEngine 对象。

### 通用点位

```csharp
void OnPointUpdated(InteractionPoint point)
{
    Vector2Data center = point.PixelPosition;
    IReadOnlyList<Vector2Data> detail = point.Fp;
}
```

- Radar：center 为聚类/交互中心；`Fp` 为该目标实际扫描点。
- CameraVision：center 为手中心；`Fp` 为 21 个手部骨骼点。

`Fp` 与 `PixelPosition` 使用同一个 Surface logical pixel 空间。不要把 `Fp` 当成独立 Pointer 生命周期。

### Camera 手部扩展

需要关节索引/结构化信息时使用 Runtime 的 hand extension helpers；通用业务只消费 center/`Fp` 即可。当前不区分左右手，一个检测到的手对应一个 `InteractionPoint`。

## Camera 路由

每个 Surface ID 可绑定一个 Camera：物理 Display、同屏 `pixelRect` 或 RenderTexture。Router 先用 Surface logical resolution 归一化，再映射到目标区域。多个 Camera 不应重复消费同一 Surface，除非项目有意镜像。

## Samples

- **Basic Interaction**：Provider-neutral 的 UGUI/2D/3D 交互、中心光标、`Fp` 粒子和 Camera 手骨骼演示。
- **Multi-Surface Routing**：一个 IPC 连接驱动多个 Surface/Camera。

导入 Sample 后得到的是 `Assets/Samples/` 副本；包升级不会自动覆盖，需重新导入。

## Player Build

Build Processor 从当前 Package Manager Resolved Path 校验 `Bridge~/win-x64` 并复制完整目录。它会检查 package/SDK/Bridge/Provider 版本、manifest、入口、Camera 模型/原生库和 SHA-256。

构建输出必须整体包含 `BlazeInteractionBridge/`。只移动 Player EXE 或只复制 Bridge EXE 都不是完整发布。
