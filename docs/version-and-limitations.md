# 版本与已知限制

## 1.1.0 身份

- Unity 包：`com.blaze.interaction`，版本 `1.1.0`，最低 Unity `2021.3`。
- Bridge：`BlazeInteractionBridge.exe` / `bridge-version.txt` 为 `1.1.0`。
- Providers：`blaze.radar.f10f20` 与 `blaze.camera.vision`，Provider API `1`，版本 `1.1.0`。
- IPC：Interaction IPC `1`，默认基础 Pipe `Blaze.InteractionBridge`，运行时按项目数据目录增加唯一后缀。
- 平台：Windows x64。

## 1.1.0 能力

- Radar F10/F20、Simulation、Replay、区域/边缘/屏蔽过滤、四点标定、多雷达同屏融合、跟踪、Touch/Dwell。
- Radar 中心点与所有实际扫描点同时传给 Unity。
- CameraVision 摄像头能力枚举、分辨率/帧率选择、MediaPipe 手部检测、每手 21 骨骼点、多手输出、四角区域、平滑、X/Y 翻转和 Unity 分辨率换算。
- Radar/CameraVision 选择、返回切换和按项目/Player 隔离持久化。
- 通用 `InteractionPoint`、UGUI/Physics2D/Physics3D、Camera 路由、Samples 和 Windows Player 自动复制。

## 限制

- CameraVision 不提供跨帧的生物身份识别，也不区分左/右手；稳定性受模型、画面、遮挡和性能影响。
- “不限制手数量”表示 SDK 不写死两只手；底层模型和硬件吞吐仍构成实际容量上限。
- Camera 标定是二维四点映射，不解决镜头畸变、深度或三维姿态标定。
- Radar Fusion 只在同一 Surface 内去重，不提供跨 Surface 人员身份连续跟踪。
- `Fp` 是 point 附属明细点，不具有独立 Down/Move/Up 生命周期；业务需要每个关节的语义时读取 Camera extensions。
- WPF 控制台和 Camera 原生推理仅随 Windows x64 payload 发布。
- UPM 包包含自包含 .NET/WPF、OpenCV、模型和原生库，体积明显大于纯 C# 包。
- 自动化测试不能替代真实 Radar 网络、不同摄像头、投影 DPI/focus、长期运行和最终 Player 换机验收。

## 兼容性

1.1.0 保持 Interaction IPC 1 和现有 Unity `InteractionManager`/`InteractionPoint` 使用方式；新增 CameraVision 与 `Fp` 使用可选数据面。`com.blaze.radar` 的旧独立 IPC/Launcher 不应与本包并存。
