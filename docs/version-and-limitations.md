# 版本与已知限制

## 1.2.6 身份

- Bridge：`BridgeVersion.Value = 1.2.6`，WPF footer 为 `Bridge 1.2.6 · IPC 2 · Windows x64`。
- Unity SDK：`UnitySdkVersion.Value = 1.2.6`；`package.json` 与 `bridge-version.txt` 同为 `1.2.6`。
- Unity 包：`com.blaze.radar`，公共命名空间 `Blaze.Radar`，最低 Unity `2021.3`。
- IPC：`IpcProtocolVersion.Current = 2`。业务帧只发送 screen-addressed `PointerBatch`，v1 `PointerFrame` 不可混用。
- 安装 URL：`https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.6`。

## 1.2.6 能力

- “一键模拟”会将所有 Unity 已关联屏幕中的已启用雷达统一切换为 Simulation，保存后立即启动，不再要求先逐个修改数据源模式。
- “停止模拟”会停止已关联屏幕中运行及切换过渡中的 Simulation 管线，但保留 Simulation 数据源配置，便于再次启动。
- “区域 / 校准”按钮按等宽网格自动换行，在最小窗口宽度与垂直滚动条存在时仍完整可见。
- 任意数量启用逻辑屏幕，每屏稳定 ID、独立分辨率/比例、Order，且全局恰好一个 Primary。
- 每屏任意多个 F10/F20 Sensor；独立连接、物理变换、过滤/标定和 OutputRect；同屏融合、跟踪、交互参数。
- LEFT/L1、FRONT/F1+F2 overlap、RIGHT/R1 等拓扑可映射到独立 Display、Camera `pixelRect` 或 RenderTexture。
- Basic Interaction 与 Multi-Screen Camera Routing 支持 local/IPC 分层验证；Bridge/Player 日志可按 screen/sensor/sequence 对时。
- WPF 软件渲染、Per-Monitor V2 DPI、ClearType/像素对齐用于降低投影电脑上 GPU dirty-region 导致的控件消失或模糊。

## 限制

- 仅 Windows x64；UPM 含完整 self-contained .NET/WPF payload，体积明显大于纯 C# 包。
- 1.2.6 不自动完成联合标定；每个雷达仍由物理四角与 OutputRect 对齐。标定和屏蔽区依赖现场几何。
- Fusion 只在同屏去重；Pointer ID 只在同屏稳定，不提供跨屏人员身份连续跟踪。
- 不提供多雷达同步录制容器；`.radarrec` 仍是单传感器原始 TCP 块/连接状态，不等于厂商文件格式。
- 只读厂家点数据，不发送文档未定义的写命令，不修改设备 IP/网关/扫描频率/马达状态。
- IPC v1/v2 主版本不兼容；必须用 Hello/HelloAck 明确拒绝后升级双方，不能尝试降级解析。
- 自动测试不能替代真实三投影、四雷达、8 小时稳定性、NIC/雷达重连、投影 focus/DPI 和最终 Windows Player 验收。

版本或 SHA 不一致时，从 Package Manager 的 Resolved Path 和 Player `RadarBridge/` 开始排查，清除项目内陈旧 1.1.x package cache 后重建。现场验收必须归档最终 Schema 2 配置、tagged logs、`Player.log` 与 EXE/package SHA。
