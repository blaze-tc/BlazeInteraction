# BlazeInteraction

BlazeInteraction 是面向 Unity 的统一外部感应设备平台。一个 Windows Bridge 负责加载不同 Sensor Provider，通过同一条 Interaction IPC 把点位发送给 Unity；当前发布版同时包含 Radar 和 CameraVision 两种模式。

```text
Radar F10/F20 / Simulation       Camera + MediaPipe Hand Landmarker
               \                 /
                Provider API 1
                      |
          BlazeInteractionBridge.exe
                      |
              Interaction IPC 1
                      |
        com.blaze.interaction 1.1.0
                      |
       UGUI / Physics2D / Physics3D
```

## 当前能力

- **Radar**：FaseLase F10/F20、Simulation/Replay、点云过滤、四点标定、同屏多雷达融合、Touch/Dwell、实际扫描点 `Fp` 输出。
- **CameraVision**：摄像头分辨率/帧率选择、不限制手数量的检测、每只手 21 个骨骼点、四角有效区、Unity 坐标换算、X/Y 翻转、设备级配置持久化。
- **统一数据**：`InteractionPoint.PixelPosition` 是交互中心点；`InteractionPoint.Fp` 是 Radar 实际扫描点或 Camera 手部骨骼点。
- **项目隔离**：Editor 配置写入当前 Unity 项目的 `Library/BlazeInteraction/`；Player 配置写入该 Player 的 `Application.persistentDataPath/BlazeInteraction/`，不同项目互不覆盖。
- **Unity 集成**：自动启动/复用 Bridge，支持 UGUI、2D/3D Physics、Surface → Camera 路由、Basic Interaction 与 Multi-Surface Routing Samples。

![Radar 控制台](docs/images/radarbridge-overview.png)

## 最快安装

从 [GitHub Releases](https://github.com/blaze-tc/BlazeInteraction/releases/latest) 下载：

```text
com.blaze.interaction-1.1.0.tgz
```

Unity 中打开 **Window > Package Manager**，点击左上角 `+`，选择 **Add package from tarball...**，选中下载的 `.tgz`。随后：

1. 在 **Project Settings > Blaze Interaction** 创建至少一个 Surface；启用项必须恰好一个 Primary。
2. 执行 **GameObject > Blaze Interaction > Create Runtime**。
3. 在 Package Manager 的 **Samples** 中导入 **Basic Interaction**。
4. 进入 Play Mode；首次打开选择 Radar 或 CameraVision，后续会记住当前项目的选择。

完整步骤、Git/本地磁盘安装和升级说明见 [INSTALL.md](INSTALL.md)。

## Unity 安装地址

固定发布标签：

```text
https://github.com/blaze-tc/BlazeInteraction.git?path=/UnityPackage/com.blaze.interaction#v1.1.0
```

本仓库开发：

```text
UnityPackage/com.blaze.interaction/package.json
```

正式项目应固定 tag，不要使用无 tag 的移动分支。

## 数据约定

```csharp
InteractionManager.Instance.PointAdded += point =>
{
    // Radar: 聚类中心；Camera: 手中心
    var center = point.PixelPosition;

    // Radar: 实际扫描点；Camera: 21 个手部骨骼点
    var detailPoints = point.Fp;
};
```

CameraVision 不区分左右手。多只手以多个 `InteractionPoint` 表示，每个 point 的 `Fp` 保存该手的骨骼点。Camera 特有的结构化手数据仍可从 extensions 读取；忽略 extensions 不影响通用交互。

## 开发与验证

要求 Windows 10/11 x64、.NET 8 SDK、Unity 2021.3 LTS 或更高。

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo

powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 `
  -Runtime win-x64 -EmbedUnityPackage

powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1

powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 `
  -UnityEditor "D:\Developer\2021.3.45f1\Editor\Unity.exe" `
  -TestPlatform All -IncludeSamples
```

发布包必须包含完整 `Bridge~/win-x64`，不能只复制 `BlazeInteractionBridge.exe`。

## 文档入口

- [安装与升级](INSTALL.md)
- [文档总览](docs/README.md)
- [图文功能说明](docs/user-guide.md)
- [Unity API 与场景集成](docs/unity-integration.md)
- [架构与数据流](docs/architecture.md)
- [代码维护与扩展](docs/development-guide.md)
- [仓库结构](docs/repository-structure.md)
- [Provider 开发](docs/provider-development.md)
- [Interaction IPC 1](docs/protocol.md)
- [故障排查](docs/troubleshooting.md)
- [版本与限制](docs/version-and-limitations.md)

历史设计稿、阶段计划和门禁报告保留在 `docs/superpowers/`、`docs/camera-vision/` 与各 `*-test-report.md` 中，仅作决策追溯；以本页及 `docs/README.md` 指向的当前文档为准。
