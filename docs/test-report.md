# RadarControl 测试报告

更新日期：2026-07-21

## 阶段 0

- 厂家说明书：14 页文本提取完成，规格表、盲区图、TCP/数据格式、CRC、解析算法和圈判断页已渲染检查。
- 结论：主需求中的 F10/F20 参数和 4 字节协议与 V9.1.1 说明书一致。

## 阶段 1：Contracts、型号与配置

命令：

```powershell
dotnet test RadarControl.sln --no-restore
```

结果：

- `Radar.Device.Tests`：3/3 通过。
- `Radar.Configuration.Tests`：5/5 通过。
- 覆盖默认 F10、F20 参数、未知型号回退、量程上限、默认端点、配置校验和 F20 持久化。

## 阶段 2：协议与扫描圈

命令：

```powershell
dotnet test RadarControl.sln --no-restore
```

结果：

- `Radar.Protocol.Tests`：17/17 通过。
- 厂家样例 `30 14 13 AF`：CRC=3，距离=40cm，角度=154.9375°。
- 覆盖非法高位、CRC 错误、半包、粘包、错位恢复、错误后续包恢复、缓存上限、扫描回绕、短圈、角度噪声和连接重置用的缓存清理。
- 全解决方案合计：25/25 通过，0 失败，0 跳过。

## 阶段 3：TCP 与录制

- `Radar.Device.Tests`：15/15 通过（包含阶段 1 的 3 项，以及端点预校验和监听者故障隔离）。
- 使用真实 loopback TCP 验证指定本机 IP 绑定、异步接收、连接状态、断线重连、重连前半包清理、超时预警和取消关闭。
- `.radarrec` 验证 Header、原始 TCP 字节、连接状态、时间戳、型号和配置快照往返；非法文件头被拒绝。

## 阶段 4-6：点云、标定与 Pointer

- `Radar.Processing.Tests`：23/23 通过。
- 覆盖 Flip/Rotation/Offset 固定顺序、边界点多边形、盲区/有效区/屏蔽区、动态阈值聚类、孤点过滤、中位数中心、TrackId 稳定、确认与丢失帧。
- Homography 覆盖四角、中心、倾斜四边形和退化/共线/自交拒绝。
- Pointer 覆盖 Touch、Dwell、EnterTrigger、HoverOnly、未确认目标与丢失容忍。
- 最新值缓存验证未消费旧帧被替换，容量恒为 1。

## 阶段 7：IPC

- `Radar.Ipc.Tests`：11/11 通过。
- 覆盖长度前缀半包、粘包、非法长度、非法 JSON、版本不兼容。
- 使用真实 Named Pipe 验证 Hello/HelloAck、Ping/Pong 和不兼容版本 Error。

## 阶段 8：WPF Bridge

- `Radar.Bridge.Wpf.Tests`：27/27 通过。
- 覆盖主 ViewModel 的型号/网卡/区域/标定状态、坐标视口、模拟数据、回放暂停与单步、屏蔽区编辑、只读指标绑定的真实窗口启动、双数据区布局、点云短时余辉、Per-Monitor V2 manifest，以及运行时幂等释放。
- Release WPF 编译通过；原始雷达点与变换/过滤后的 Unity 输出点由两个独立 Surface 显示，输出区保留目标、有效区域与屏蔽区。

## 阶段 9：Unity UPM

- `Radar.Unity.Compatibility.Tests`：17/17 通过，直接编译包内长度前缀解码器、容量 1 最新值缓存、Named Pipe 客户端和 Bridge 路径解析器，并验证 IPC 帧序号/时间戳保留、`com.blaze.radar` 包身份、Sample 显式绑定、有界诊断日志、内嵌 self-contained Bridge、版本标记与 F10/F20 Profile 完整存在。
- UPM 包包含 Launcher、PipeClient、FrameDispatcher、RadarInputModule、BuildProcessor、SettingsProvider、PlayMode 测试与 Basic Interaction Sample。
- Unity 2021.3.45f1 已真实导入并编译 `Blaze.Radar.Runtime`、`Blaze.Radar.Editor` 与 `Blaze.Radar.Sample.BasicInteraction`；已在 Play Mode 验证场景启动、内嵌 Bridge 拉起、Hello/HelloAck IPC 连接、UGUI/3D/2D 射线配置，以及指针探针和详细界面日志。包内自动 PlayMode 测试仍保留给目标项目的 Unity Test Runner 执行。

## 阶段 10：构建与发布

- 命令：`.\scripts\build.ps1`、`.\scripts\test.ps1 -NoBuild`、`.\scripts\publish-bridge.ps1`、`.\scripts\test-embedded-bridge.ps1`。
- Release 全解决方案编译：0 警告、0 错误。
- `win-x64` self-contained 发布产物包含 `RadarBridge.exe` 与 F10/F20 Profile，并自动同步到 UPM 包的 `Bridge~/win-x64/`。
- 内嵌 EXE 已验证可创建正常 WPF 主窗口，并在 Unity 父进程结束后以退出码 0 自动关闭。
- 在 Windows 125% 缩放（DPI 120）下验证发布进程为 Per-Monitor DPI Aware；启动模拟并点击翻转控件后，双数据区和整窗文字保持清晰。

## 当前全量结果

- `Radar.Protocol.Tests`：17/17。
- `Radar.Device.Tests`：15/15。
- `Radar.Configuration.Tests`：6/6。
- `Radar.Processing.Tests`：23/23。
- `Radar.Ipc.Tests`：11/11。
- `Radar.Bridge.Wpf.Tests`：27/27。
- `Radar.Unity.Compatibility.Tests`：17/17。
- 合计：116/116 通过，0 失败，0 跳过。

## 现场尚待执行

- 真实雷达 8 小时稳定性、真实录制/回放、现场四点标定需要 F10/F20 和目标安装环境。
- Unity Test Runner 的完整 PlayMode 套件与 Windows Player 构建仍需在目标 Unity 2021.3+ 项目中执行；Basic Interaction 已完成 Unity 2021.3.45f1 Play Mode 画面、IPC 状态和场景结构验收。
