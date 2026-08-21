# BlazeInteraction

BlazeInteraction 是统一外部感应设备交互平台。Gate A 已完成以下唯一生产链路：

```text
RadarControl 既有算法
  -> Interaction Core / Provider API 1
  -> Radar Provider (blaze.radar.f10f20)
  -> BlazeInteractionBridge.exe
  -> Interaction IPC 1
  -> com.blaze.interaction 1.0.0
  -> Unity UGUI / Physics2D / Physics3D
```

Gate A 保留 FaseLase F10/F20 的连接、配置、标定、过滤、同屏融合、跟踪、Touch 与 Dwell 行为，并把它们封装为外置 Radar Provider。Unity 不再拥有 Radar 专用 Pipe Client 或第二套 Launcher。CameraHand 不属于 Gate A，仓库当前没有 CameraHand 生产实现。

## 安装 Unity 包

开发时可在 Unity Package Manager 使用 `Add package from disk...` 选择：

```text
E:\Project\BlazeInteraction\UnityPackage\com.blaze.interaction\package.json
```

也可使用 Git URL：

```text
https://github.com/blaze-tc/BlazeInteraction.git?path=/UnityPackage/com.blaze.interaction
```

正式部署必须在 URL 尾部固定已审核 tag 或 commit。Package Manager 应显示 `Blaze Interaction SDK 1.0.0`，仓库只发布 `com.blaze.interaction` 这一套 UPM 包。

安装后：

1. 在 **Project Settings > Blaze Interaction** 创建并校验 Surface topology；所有启用 Surface 的 ID、Order 必须唯一，且恰好一个 Primary。
2. 执行 **GameObject > Blaze Interaction > Create Runtime**，场景只保留一个启用的 Interaction Input Module/EventSystem。
3. 先导入 **Basic Interaction** 用 Simulation 验证，再用 **Multi-Surface Routing** 验证多 Camera、`pixelRect` 与 RenderTexture 路由。

详细步骤见 [Unity 集成](docs/unity-integration.md)，从 `com.blaze.radar` 迁移见 [Radar 迁移](docs/radar-migration.md)。

## 构建与验证

环境：Windows 10/11 x64、.NET 8 SDK、Unity 2021.3.45f1。

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -UnityEditor "D:\Developer\2021.3.45f1\Editor\Unity.exe" -TestPlatform All -IncludeSamples

$publish = Join-Path $env:TEMP ("BlazeInteractionBridge-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $publish
powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 -OutputDirectory $publish -EmbedUnityPackage
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1
```

发布布局必须满足：

- 根目录恰好一个 `BlazeInteractionBridge.exe`；
- Radar 位于 `Providers/Radar/`，由 `provider.json` 声明 Provider API 1；
- Unity 包内嵌的是完整 self-contained Bridge payload，不允许只替换 EXE；
- `bridge-version.txt`、Provider manifest、入口 DLL 与 SHA-256 校验全部通过。

本轮 Gate A 自动化结果见 [Gate A 测试报告](docs/gate-a-test-report.md)。真实雷达、投影机、网卡和长稳运行仍需现场验收，自动化不能替代硬件门禁。

## 仓库结构

- `src/Blaze.Interaction.*`：Contracts、Provider 托管、Interaction IPC 与 WPF Bridge。
- `providers/Radar/Blaze.Provider.Radar/`：RadarControl 到 Provider API 1 的适配层。
- `src/Radar.*`：保留并继续回归的 F10/F20 设备、配置、处理与旧桥接实现。
- `UnityPackage/com.blaze.interaction/`：唯一 UPM 包、兼容层、Samples、测试与内嵌 Bridge。
- `tests/`：Interaction、Radar、发布布局、兼容性与端到端测试。
- `scripts/`：完整测试、发布、嵌入和真实 IPC 冒烟脚本。

## 文档

- [架构与所有权](docs/architecture.md)
- [Interaction IPC 1](docs/protocol.md)
- [Unity 集成](docs/unity-integration.md)
- [Provider 开发](docs/provider-development.md)
- [Radar 迁移](docs/radar-migration.md)
- [Gate A 测试报告](docs/gate-a-test-report.md)
- [Radar 标定](docs/calibration.md)
- [故障排查](docs/troubleshooting.md)
- [Radar 用户说明](docs/user-guide.md)
