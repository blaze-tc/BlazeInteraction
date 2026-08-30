# RadarControl 分阶段实施计划

本计划由 `RadarControl_Codex_Execution_Spec.md` 派生。每个阶段必须先通过本阶段的构建与自动测试，再进入下一阶段；阶段结果持续记录到 `docs/test-report.md`。

## 阶段 1：Contracts、型号与配置

- 建立解决方案和 Contracts、Device、Configuration 项目。
- 定义 F10/F20 Profile、默认 F10、集中式配置对象与校验。
- 验证 F10/F20 参数、无效型号回退、配置持久化和量程上限。

门禁：`dotnet build` 和全部现有 `dotnet test` 通过。

## 阶段 2：协议与扫描圈

- 实现高位模式、CRC、4 字节点解析、逐字节重同步和有界缓存。
- 实现坐标转换与不可变扫描圈快照。
- 覆盖合法包、CRC 错误、半包、粘包、错位、回绕和重置。

门禁：协议测试全部通过，样例 `30 14 13 AF` 解析为 40cm、154.9375°。

## 阶段 3：TCP 与录制/回放入口

- 实现指定本机 IP 的异步 TCP 连接、取消、超时、状态与退避重连。
- 连接恢复时清空解析缓存与未完成扫描圈。
- 原始 TCP 块和时间戳写入 `.radarrec`，回放重新进入相同字节解析链。
- 以本地模拟服务器验证分包、断线与重连。

门禁：Device 测试通过，关闭时无遗留后台任务。

## 阶段 4：过滤、聚类与跟踪

- 实现固定顺序的坐标变换、范围/角度/多边形过滤。
- 实现相邻点动态阈值聚类、稳健中心、最近邻 TrackId 与指数平滑。
- 用容量 1 的最新帧通道隔离采集与处理。

门禁：Processing 测试覆盖分簇、孤点、动态阈值、TrackId、丢失容忍与容量上限。

## 阶段 5：四点标定

- 求解 3x3 Homography、四角误差、共线/自交/退化检查。
- 映射到左下原点的 0..1 坐标并支持 Clamp/丢弃策略。
- 持久化标定来源、型号、安装变换和误差。

门禁：四角、中心、倾斜、翻转、旋转和无效输入测试通过。

## 阶段 6：Pointer 状态机

- 实现 Touch、Dwell、EnterTrigger、HoverOnly。
- 实现确认、丢失容忍、最小按下时间、移动/拖拽阈值和事件序列。

门禁：四种模式和抖动边界测试通过。

## 阶段 7：IPC

- 实现长度前缀 JSON、协议版本检查、Hello/HelloAck、PointerFrame、Status、Ping/Pong 和错误消息。
- 实现多客户端或明确的单 Unity 客户端生命周期、心跳超时与重连。

门禁：半包、粘包、非法长度/JSON、版本不匹配和心跳测试通过。

## 阶段 8：WPF Bridge

- 使用 MVVM 和依赖注入组合服务。
- 提供设备、点云、区域/标定、交互/Unity 状态、日志与录制回放 UI。
- 点云由单个自定义绘制控件渲染，UI 限频 30FPS，后台线程不触碰 UI 对象。

门禁：WPF Release 构建通过，ViewModel 关键逻辑测试通过，模拟模式可启动。

## 阶段 9：Unity UPM SDK

- Runtime：Bridge 启动、后台 Pipe、最新帧分发、多指针 `RadarInputModule`、Debug Overlay。
- Editor：SettingsProvider、Bridge 发布目录检查与构建后复制。
- Samples：Button、Toggle、Slider、ScrollRect、3D/2D 点击和双指针示例资源。

门禁：程序集静态兼容检查与可独立执行的纯 C# 协议测试通过；宿主 Unity 中的 EditMode/PlayMode 测试列为接入门禁。

## 阶段 10：发布与文档

- 提供 PowerShell 构建/测试/发布/复制脚本和默认 F10 Profile。
- 完成架构、协议、标定、Unity 接入、网络配置、故障排查、版本与限制说明。
- 生成 `win-x64` 自包含 Bridge 发布目录并执行启动冒烟测试。

门禁：干净环境下完整脚本通过；记录所有自动与现场待验项目。
