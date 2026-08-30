# RadarControl 当前项目分析

更新日期：2026-07-20

## 结论

当前仓库是新建空仓库。远端 `blaze-tc/RadarControl` 的 `main` 仅包含初始 `README.md`；本地除需求文档和厂家说明书外，不存在解决方案、C# 源码、Unity 工程、程序集定义、输入模块、构建处理器或既有命名规范。因此本项目应按需求文档给出的职责边界从零搭建，不存在需要迁移或避免改动的旧业务逻辑。

第一版采用双进程架构：.NET 8 WPF `RadarBridge` 独占雷达 TCP 连接并处理协议、扫描圈、过滤、聚类、跟踪、标定、录制/回放和 Named Pipe；Unity UPM Package 仅负责进程生命周期、IPC、主线程分发与 EventSystem 输入事件。

## 仓库与工具检查

| 检查项 | 结果 | 决策 |
|---|---|---|
| Git 远端 | `https://github.com/blaze-tc/RadarControl.git`，默认分支 `main` | 本地已关联 `origin/main` |
| 远端内容 | 仅初始 README | 可从零建立推荐目录 |
| 解决方案 | 不存在 | 新建 `RadarControl.sln` |
| Unity 工程 | 不存在，未找到 `ProjectVersion.txt` | SDK 以 Unity 2021.3 LTS 为最低兼容基线；示例以 UPM Sample 交付 |
| Assembly Definition | 不存在 | 新建 Runtime、Editor、Tests asmdef |
| InputModule | 不存在 | 新建 `RadarInputModule`，不改写旧模块 |
| BuildProcessor | 不存在 | 新建 SDK 专属构建复制处理器 |
| C# 命名空间 | 不存在 | 使用 `Yuexin.Radar.*` |
| .NET SDK | 已安装 8.0.412 与 10.0.301 | 使用 `global.json` 固定到 8.0.412 |
| GitHub CLI | 未安装 | 不影响本地开发；最终推送前需补齐 CLI 或使用现有 Git 凭据 |

## 厂家说明书核验

已阅读并渲染检查 `F10、F20说明书V9.1.1.pdf` 的 14 页内容。需求文档采用的下列事实与说明书一致：

- 默认雷达地址为 `192.168.0.100`，TCP Server 端口为 `8487`。
- F10 测量范围为 0.05m 至 10m，扫描频率 10Hz 至 25Hz，默认 15Hz，默认角分辨率 0.27°。
- F20 测量范围为 0.05m 至 40m，扫描频率 10Hz 至 30Hz，默认 25Hz，默认角分辨率 0.3°。
- 测量角范围为 280°，230° 至 310°为盲区。
- 点数据为 4 字节，A/B/C 高位为 0、D 高位为 1；A6-A4 是 B/C/D 中二进制 1 数量之和的低三位。
- 距离占 12 位、单位 cm；角度占 13 位、单位 1/16°。
- 同圈角度递增，当前角度小于前一点角度时可判定新圈开始。
- 说明书样例 `30 14 13 AF` 对应 40cm、154.9375°。

说明书也描述了设备端频率、IP、网关、掩码、角度输出和马达控制，但没有给出这些写入命令的协议字节。第一版严格不实现或猜测任何设备写入命令，仅提供软件侧配置与接收处理。

## 架构边界

```text
F10/F20 TCP
  -> Radar.Device（连接与重连）
  -> Radar.Protocol（流重同步、CRC、点包、扫描圈）
  -> Radar.Processing（坐标、区域、聚类、跟踪、标定、指针）
  -> Radar.Ipc（长度前缀 JSON、Named Pipe）
  -> Unity Runtime（后台接收、最新帧缓存）
  -> Unity 主线程（EventSystem 射线与多指针事件）
```

型号差异只允许位于 `IRadarModelProfile` 实现和配置校验层。协议公式不得因 F10/F20 切换而改变。

## 关键技术决策

1. Bridge 项目统一目标框架 `net8.0`，WPF 目标为 `net8.0-windows`，发布 RID 为 `win-x64`。
2. 纯逻辑模块不引用 WPF，确保协议、配置、处理与 IPC 编解码可独立测试。
3. TCP 字节流解析采用有界内部缓冲和逐字节重同步；CRC 失败仅丢 1 字节。
4. 扫描/处理链路使用容量 1 的最新值交换，不允许旧帧无限排队。
5. 配置先从随程序发布的默认 F10 Profile 加载，再覆盖 `%LocalAppData%/Yuexin/RadarBridge/config.json`；升级不覆盖用户标定。
6. IPC 使用 4 字节小端长度前缀 + UTF-8 JSON，并设置最大帧长度防止异常分配。
7. Unity Runtime 不使用 C# record、System.Text.Json 或新输入系统，以保持 Unity 2021.3 兼容。
8. 没有实机和现成 Unity 工程时，协议与运行时逻辑以自动测试、模拟 TCP、录制回放和 UPM 测试夹具验证；真实设备 8 小时稳定性和 Unity 场景交互保留为现场验收项。

## 风险与待现场确认项

- 当前没有真实雷达、真实 `.radarrec` 数据或厂家固件版本，无法在本机完成 8 小时设备稳定性与真实录制验收。
- 当前没有宿主 Unity 项目，无法读取实际 Unity 版本或验证其已有 EventSystem 配置；包以 2021.3 LTS 基线交付，接入具体项目后需再跑 PlayMode 测试。
- 厂家说明书中的 3 字节旧格式示例不属于 F10/F20 的 4 字节实现，本项目不支持该旧格式。
- 最终 GitHub 发布前需要可用的 `gh` 或 Git 凭据；当前环境只有 `git`。

## 阶段 0 验收记录

- 仓库、远端、工具链、Unity 缺失状态：已核对。
- 厂家 PDF 文本与关键页面：已提取并视觉核验。
- 实施计划：见 `docs/implementation-plan.md`。
- 本阶段未写入业务代码，符合阶段 0 约束。
