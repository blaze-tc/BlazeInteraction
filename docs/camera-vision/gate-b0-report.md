# CameraVision Gate B0 验收报告

日期：2026-08-24
范围：仅 Gate B0（Provider Skeleton + Camera Capture + Fake Point）

## 结论

`GATE B0 PASSED`

CameraVision 已作为第二个 Provider API 1 插件接入唯一生产链路：

```text
CameraVision Provider
  -> InteractionPoint / InteractionFrame
  -> Interaction IPC 1
  -> com.blaze.interaction
  -> Unity standard dispatcher / BasicInteraction cursor
```

未新增 Camera 专属 IPC、Named Pipe、Bridge、Launcher、Unity Package、Python/worker 或第二个业务 EXE。未实现 Gate B1-B6 的标定、颜色检测、Hand、Tracking、滤波、配置/预览 UI 或性能阶段功能。默认未指定 `--provider` 时仍选择 Radar。

## Gate B0 交付

- `blaze.camera.vision` manifest、Provider discovery/loader 和 API 1 descriptor。
- initialize/start/stop/dispose 生命周期；50 次自动循环，并额外执行 10 轮压力复跑（500 个 start/stop 周期）。
- 进程内 OpenCvSharp Windows capture backend；设备枚举、打开/关闭、分辨率、FPS、Mirror X、0/90/180/270 旋转。
- Camera disconnected/reconnecting 状态和自动重连；摄像头不可用时 Bridge/Provider 不崩溃。
- capacity=1 的 `LatestFrameSlot`，覆盖旧帧时立即释放并统计 drop。
- captured/drop/reconnect/实际宽高/请求 FPS/最后帧时间统计。
- 可预测 `FakeVisualDetector`：`x=PingPong(time)`、`y=0.5`。
- Fake 点映射成标准 Hover `InteractionPoint`，通过标准 `InteractionFrame` 和 IPC1；IPC 枚举没有 Camera 消息。
- BasicInteraction 使用 Provider-neutral cursor 显示标准点位；Unity runtime 未加入 Camera 客户端或设备依赖。
- 发布与 Unity embedded payload 同时包含 Radar、CameraVision 两个外部 Provider，仍只有 `BlazeInteractionBridge.exe` 一个 EXE。
- embedded smoke 先验证默认 Radar 标准帧，再用 `--provider blaze.camera.vision` 验证 CameraVision fake 标准帧和父进程退出。

## TDD 与回归结果

每个阶段均先加入失败测试，再做最小实现并运行相关完整套件。实施期间捕获并修复了两个真实竞态：

1. Gate B0 开始前，Interaction Pipe 在取消与 writer dispose 并发时泄漏 `ObjectDisposedException`；新增确定性 IPC 回归后只在取消路径归一化为 Cancelled，IPC 61/61、Bridge 35/35 通过。
2. 全量并发回归发现 Camera capture 启动 lambda 延迟读取已被 Stop 清空的 CTS 字段；改为捕获局部 CTS，之后 500 个额外生命周期周期与 CameraVision 21/21 通过。

最终验证：

| 验证 | 结果 |
|---|---:|
| `dotnet test BlazeInteraction.sln --no-restore --verbosity minimal` | PASS，673/673 |
| `scripts/test.ps1 -Configuration Release`（RadarControl/Gate A 硬门禁） | PASS，410/410 |
| CameraVision Provider tests | PASS，21/21 |
| CameraVision 50-cycle test × 10 stress rerun | PASS，10/10（500 cycles） |
| Interaction IPC tests | PASS，61/61 |
| Radar Provider tests | PASS，34/34 |
| Radar Configuration tests（含 F10/F20/profile） | PASS，51/51 |
| Radar Processing tests（含 calibration/fusion/tracking/touch/dwell） | PASS，61/61 |
| Radar End-to-End tests | PASS，3/3 |
| Radar Unity compatibility / publish / embedded smoke | PASS，100/100 |
| Release self-contained layout + Radar/CameraVision IPC smoke | PASS，1/1 |

## Unity 2021.3 验证

- 使用已运行的 Unity 2021.3.45f1 + UnitySkills 专用工程临时安装本地 `com.blaze.interaction`。
- 强制重编译后生成 `Blaze.Interaction.Editor.Tests.dll` 与 `Blaze.Interaction.Runtime.Tests.dll`，Unity Console compiler error 为 0。
- Test Runner 实际发现 6 个 `InteractionEditorTests`（包括新增 BasicInteraction provider-neutral cursor 测试）；UnitySkills 将该测试树聚合报告为 1/1 passed。
- 临时导入 Basic Interaction 和 Multi-Surface Routing，成功生成两个 Sample 程序集，Unity Console compiler error 为 0。
- 隔离命令行 runner 首轮发现并修复 `PackageInfo` 歧义编译错误；修复后 Windows 阻止启动第二个 Unity（requires elevation）。UnitySkills PlayMode 作业在进入域切换后丢失 job 记录，因此没有把该次 PlayMode API 作业列为独立通过计数。Gate B0 的 Unity point/dispatcher/sample 契约同时由 Unity 编译和 `Radar.Unity.Compatibility.Tests` 覆盖。
- 验证结束后已移除专用 Unity 工程中的临时 Sample、本地 package dependency 和 `testables`，没有保留外部工程改动。

## 发布结构审计

- 业务 EXE：1（`BlazeInteractionBridge.exe`）。
- Provider 目录：`Providers/Radar`、`Providers/CameraVision`。
- CameraVision native runtime：恰好 1 个 `OpenCvSharpExtern.dll`，并包含视频 I/O runtime 依赖。
- Provider API：两者均为 1。
- IPC：Interaction IPC 1；没有 Camera 专属消息类型或 Pipe。
- Unity：仍为单一 `com.blaze.interaction` 包。

## 硬件边界

`REQUIRES HARDWARE VALIDATION`

本轮没有把自动化 capture backend 当作真实 USB 摄像头验收。现场仍需验证设备枚举、DirectShow/MSMF 打开、实际分辨率/FPS 协商、镜像/旋转画面、拔插重连和关闭后的独占句柄释放。该声明不影响 B0 软件链路、Fake Point 和 Radar 回归通过，但禁止表述为已完成现场硬件验收。

## 已知非阻塞诊断

当前 .NET 8 SDK 的 Roslyn 4.11 会对 OpenCvSharp 4.13 自带的 Roslyn 4.14 analyzer 输出 `CS9057` 版本提示；编译、测试、发布和 native payload 均成功。它不改变运行时行为，但升级 SDK 后可消除该第三方 analyzer 版本提示。

## 停止点

Gate B0 到此停止。未执行 Gate B1、B2、B3、B4、B5 或 B6。
