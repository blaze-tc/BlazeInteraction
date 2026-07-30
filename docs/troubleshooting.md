# 1.2.6 故障排查与现场证据

## 包版本或 Bridge 不是 1.2.6

Package Manager 应解析：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.6
```

移除旧 URL、本地覆盖和已导入的旧 Sample。确认 Version `1.2.6` 和 Resolved Path 指向本项目新 `Library/PackageCache/com.blaze.radar@...`。仍陈旧时关闭 Unity，只清除该包缓存与 lock 条目后重开。Player 构建后比较 `RadarBridge/bridge-version.txt` 和包内/已审核 EXE SHA-256；不要用单个旧 EXE 覆盖完整目录。

## IPC protocol mismatch 或一直 DISCONNECTED

IPC v1 PointerFrame 与 IPC v2 PointerBatch 不兼容。关闭所有旧 RadarBridge/Player，确认 package、SDK、Bridge 都为 `1.2.6`，日志显示 IPC 2，Pipe Name 两侧一致，再重连。HelloAck 必须包含 protocol 2、Bridge 1.2.6 和当前 screen summaries；不能忽略 Error 强行继续。

## 某屏无输入、串屏或重叠区双点

- Project Settings 中 Screen ID/Order 唯一，所有启用屏幕恰好一个 Primary，逻辑分辨率有效。
- Bridge 选择正确屏幕；检查每个 Sensor 的 enabled/source、连接、本机 NIC、transform/calibration 和 OutputRect。
- FRONT F1/F2 必须属于同一 FRONT；输出矩形覆盖真实交叠区，并调节 FRONT 的 data max age、fusion distance、association distance/confirm/lost。
- Camera 的 Display、`pixelRect` 或 RenderTexture 绑定必须对应 screenId；检查对应 Graphic/Physics/Physics2D Raycaster。
- 改拓扑、分辨率或 OutputRect 会触发 Pointer Up/reset，只在操作员预期时更改。

## 雷达连接失败

电脑可设 `192.168.0.10/24`，雷达常用 `192.168.0.100:8487`；两者不能相同。Bridge 每个 Sensor 选择实际 F10/F20 与正确本机 NIC。逐个断开/恢复雷达和 NIC，其他屏幕应持续；检查 `[SCREEN/SENSOR]` tagged reconnect/timeout 日志。

## WPF 控件消失或变模糊

Bridge 在窗口创建前强制 WPF 软件渲染，路径不依赖 GPU。仍需记录 Windows 缩放、投影分辨率、GPU/驱动和精确操作；测试 click、drag、scroll、resize、minimize/restore、跨 DPI 屏移动和 projector focus change。若能复现，保存同一时间段日志与截图，确认运行的是包内 1.2.6 完整 payload，而非缓存旧版。

## 日志关联

Bridge 日志：`%LOCALAPPDATA%/RadarControl/logs/RadarBridge-YYYYMMDD.log`；配置默认在 `%LOCALAPPDATA%/Yuexin/RadarBridge/config.json`，也可 `--profile` 指定。用 `[SCREEN/SENSOR]`、sequence 和 timestamp 对齐 `Player.log` 的 SDK/Bridge/IPC、screenId、batch/frame sequence、pointer/dropped count、latency 和 EventSystem target。

现场问题至少提供：最终 Schema 2 配置、Bridge tagged logs、`Player.log`、Package Manager Resolved Path、Player/包内 EXE SHA、拓扑/Camera 绑定与操作时间线。8 小时门禁见 [INSTALL.md](../INSTALL.md#10-现场-8-小时验收三投影四雷达)。
