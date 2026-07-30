# Unity 1.2.3 多屏集成

安装固定标签：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.3
```

升级时先移除旧 URL 和旧 Sample。Package Manager 必须显示 Blaze Radar SDK `1.2.3`，Resolved Path 必须是这次解析的 `Library/PackageCache/com.blaze.radar@...`（本地包则应是预期克隆路径）。发现旧路径时关闭 Unity，只清理本项目该包缓存/lock 条目，再解析标签；详见 [INSTALL.md](../INSTALL.md)。

## Project Settings 拓扑

在 **Project Settings > Blaze Radar** 添加任意数量的 Screen：

- Screen ID 必须非空、唯一并长期稳定；逻辑宽高各自独立，可适配不同比例/分辨率。
- 启用屏幕的 Order 必须唯一，并且恰好一个 `Is Primary`。禁用屏幕不会出现在 IPC topology。
- `Auto Start`、`Exit Bridge With Unity` 与 Pipe Name 保留原有语义；默认 Pipe 仍是 `Yuexin.RadarBridge`。

示例为 LEFT/L1、FRONT/F1+F2、RIGHT/R1。F1/F2 属于同一个 FRONT，Bridge 内两个 OutputRect 在物理交叠处保留重叠，由 FRONT 的融合/跟踪参数去重并维持同屏 Pointer ID。

## Bridge 配置边界

Unity 的 Hello topology 决定可选屏幕和逻辑分辨率。操作员在 Bridge 先选屏幕，再新增/删除传感器；每个 Sensor 分别设置 Real/Simulation/Replay、F10/F20、雷达 IP/端口、本机网卡、transform/filter/calibration 和 OutputRect。Fusion output rate/data age/distance、tracking confirm/lost/association/smoothing 以及 Touch/Dwell 参数属于 Screen。

## Runtime 与 Camera 路由

执行 **GameObject > Blaze Radar > Create Runtime**。一个场景只保留一个启用 EventSystem 输入模块；Canvas 使用 `GraphicRaycaster`，3D/2D Camera 使用相应 Raycaster。

为每个 Screen ID 选择独立绑定：

- 不同物理输出使用 Camera `targetDisplay`；Player 启用并核对每个 Display。
- 同一 Display 拼接时使用 Camera `pixelRect`，矩形像素布局必须匹配逻辑屏幕。
- 投影映射管线使用独立 RenderTexture，纹理尺寸/比例匹配逻辑屏幕。

PointerBatch 带 screen summary 和每屏像素坐标，router 只把一屏数据送到其绑定 Camera。per-camera world particles、UGUI、2D、3D 命中都应留在对应墙；不要无意让多个启用 Camera 消费相同 Screen ID。

## Basic Interaction 与 Multi-Screen Camera Routing

Basic Interaction：先用 Bridge Simulation，再用真实雷达/IPC，验证 Button、Toggle、Slider、Scroll、2D/3D targets 和完整 Pointer 生命周期。右侧日志与 `Player.log` 提供 Bridge/SDK/IPC、sequence、latency、dropped count、ID、phase 和命中对象。

Multi-Screen Camera Routing：先用 **LOCAL** 无 Bridge 模拟逐屏指针，检查 Display/`pixelRect`/RenderTexture 与 world particles；再用 **BRIDGE IPC** 连接 1.2.3 Bridge，按 LEFT/FRONT/RIGHT 和 L1/F1/F2/R1 逐路验证。FRONT 重叠区必须只有一个稳定 Pointer，不能重复 Click。

## 日志和 Player Build

Bridge 日志以 `[SCREEN/SENSOR]` 和 `[GLOBAL/IPC]` 标记。对齐 `Player.log` 的 SDK/Bridge/IPC、screenId、batch/frame sequence、pointer count、dropped count、timestamp/latency 和 EventSystem target。

Windows Build 后处理器只从当前 Package Manager Resolved Path 取 Bridge，删除旧 Player `RadarBridge/` 后复制完整 self-contained payload，校验 package/SDK/marker 均为 `1.2.3` 和 EXE SHA-256。构建后记录 Player-side marker/SHA；不一致时清理陈旧包缓存并重新 Build，不要手工换 EXE。

IPC v1/v2 不兼容。protocol mismatch 时关闭旧 Bridge/Player，移除旧 URL/cache，确认双方 `1.2.3`/IPC 2 后重连。现场还要完成 [三投影四雷达 8 小时清单](../INSTALL.md#10-现场-8-小时验收三投影四雷达)。
