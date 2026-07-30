# RadarControl 1.2.7

FaseLase F10/F20 雷达桥接程序与 Unity 多屏、多雷达、多指针交互 SDK。Windows x64 的 `RadarBridge.exe` 独占雷达 TCP 连接，Unity 只通过 IPC 2 Named Pipe 接收按屏幕分组的指针批次。

正式项目请固定到已审核标签：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.7
```

从旧版本升级时，先从 `Packages/manifest.json` 删除旧的 Git URL，再安装上述 URL；安装后在 Package Manager 中选择 Blaze Radar SDK，确认版本为 `1.2.7`，Resolved Path 指向本次解析的 `Library/PackageCache/com.blaze.radar@...`，而不是旧缓存或本地覆盖目录。完整步骤见 [安装、升级与现场验收](INSTALL.md)。

## 快速验证

环境：Windows 10/11 x64、.NET 8 SDK、Unity 2021.3 LTS。

```powershell
dotnet clean RadarControl.sln -c Release
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform All -IncludeSamples
powershell -ExecutionPolicy Bypass -File scripts/publish-bridge.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -StartupTimeoutSeconds 20
```

发布脚本生成完整 self-contained 输出到 `artifacts/publish/RadarBridge/win-x64/`，再原样嵌入 `UnityPackage/com.blaze.radar/Bridge~/win-x64/`。两个目录必须保留全部 DLL、runtimeconfig、`profiles/` 和 `bridge-version.txt`；只复制 EXE 无法运行。

## 1.2.7 工作流

- 在 **Project Settings > Blaze Radar** 定义任意数量逻辑屏幕，给每屏设置稳定且唯一的 Screen ID、逻辑分辨率和顺序；所有启用屏幕中必须恰好一个 Primary。
- Unity Hello 后，Bridge 的“Unity 屏幕”列表选择对应屏幕；每屏可新增/删除多个传感器，分别配置 F10/F20、雷达/本机 IP、输出矩形、变换、过滤与标定，再配置该屏幕的融合、跟踪和交互参数。
- 区域 1 的“放大编辑”提供独立大画布；空白处左键拖动可平移，滚轮/按钮可缩放，并可适应完整拉框或恢复雷达参数范围，所有视图操作都不改变 Unity 坐标。
- 推荐现场拓扑：`LEFT/L1`、`FRONT/F1+F2`（输出矩形保留重叠区用于融合）、`RIGHT/R1`。Pointer ID 只保证同屏稳定，不跨屏延续。
- **Basic Interaction** 用于单屏 UGUI/2D/3D 事件检查；**Multi-Screen Camera Routing** 可切换 LOCAL 模拟或 BRIDGE IPC，验证独立 Display、Camera `pixelRect` 和 RenderTexture 路由。
- Bridge 日志带 `[SCREEN/SENSOR]` 标签；与 `Player.log` 中 SDK/Bridge/IPC 版本、screenId、batch/frame sequence、pointer count、dropped count 和 latency 对时排障。

## 仓库内容

- `src/`：Contracts、设备连接、配置、协议、处理、IPC 与 WPF Bridge。
- `tests/`：.NET、协议、WPF、Unity 共享源码、E2E 和发布契约测试。
- `UnityPackage/com.blaze.radar/`：UPM 包、Editor 工具、Runtime、Samples、测试与完整 Bridge。
- `config/default-profile.json` / `config/f20-profile.json`：Schema 2 F10/F20 配置模板。
- `docs/`：架构、协议、Unity 路由、故障排查、限制与现场门禁。

## 文档

- [图文使用说明：界面、按钮与参数](docs/user-guide.md)
- [安装、升级与现场验收](INSTALL.md)
- [Unity 多屏集成](docs/unity-integration.md)
- [故障排查](docs/troubleshooting.md)
- [架构与线程模型](docs/architecture.md)
- [雷达及 IPC 2 协议](docs/protocol.md)
- [版本与限制](docs/version-and-limitations.md)
- [区域与四点标定](docs/calibration.md)
- [测试报告](docs/test-report.md)

软件自动化不能代替现场验收。正式交付前必须在三台投影机和四台真实雷达上完成 [8 小时检查表](INSTALL.md#10-现场-8-小时验收三投影四雷达)，归档配置、Bridge 日志、`Player.log` 和 EXE/package SHA-256。
