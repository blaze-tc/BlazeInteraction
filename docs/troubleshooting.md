# 1.2.8 故障排查与现场证据

## 包版本或 Bridge 不是 1.2.8

Package Manager 应解析：

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.8
```

移除旧 URL、本地覆盖和已导入的旧 Sample。确认 Version `1.2.8` 和 Resolved Path 指向本项目新 `Library/PackageCache/com.blaze.radar@...`。仍陈旧时关闭 Unity，只清除该包缓存与 lock 条目后重开。Player 构建后比较 `RadarBridge/bridge-version.txt` 和包内/已审核 EXE SHA-256；不要用单个旧 EXE 覆盖完整目录。

## IPC protocol mismatch 或一直 DISCONNECTED

IPC v1 PointerFrame 与 IPC v2 PointerBatch 不兼容。关闭所有旧 RadarBridge/Player，确认 package、SDK、Bridge 都为 `1.2.8`，日志显示 IPC 2，Pipe Name 两侧一致，再重连。HelloAck 必须包含 protocol 2、Bridge 1.2.8 和当前 screen summaries；不能忽略 Error 强行继续。

## 某屏无输入、串屏或重叠区双点

- Project Settings 中 Screen ID/Order 唯一，所有启用屏幕恰好一个 Primary，逻辑分辨率有效。
- Bridge 选择正确屏幕；检查每个 Sensor 的 enabled/source、连接、本机 NIC、transform/calibration 和 OutputRect。
- FRONT F1/F2 必须属于同一 FRONT；输出矩形覆盖真实交叠区，并调节 FRONT 的 data max age、fusion distance、association distance/confirm/lost。
- Camera 的 Display、`pixelRect` 或 RenderTexture 绑定必须对应 screenId；检查对应 Graphic/Physics/Physics2D Raycaster。
- 改拓扑、分辨率或 OutputRect 会触发 Pointer Up/reset，只在操作员预期时更改。

## 三面墙边线噪点进入 Unity

不要缩小有效拉框来躲避墙角或地面边线。选择对应雷达，在“雷达参数 > 边线过滤（优先）”分别设置左、右、上、下向内死区；从 `0.05–0.15 m` 开始，观察 1B/放大编辑器的橙色带覆盖静态边线，而 1A 保留原始点用于诊断。1.2.8 按绿色拉框的真实斜边计算距离，不受拉框旋转或梯形透视影响。设置后必须保存并应用。

## 快速挥动点位稀疏或 Pointer ID 跳变

- 先过滤边线噪点，再点“载入快速移动预设”并“保存并应用配置”。预设把确认帧设为 1、丢失帧设为 5、平滑设为 0.8、数据年龄设为 220 ms、最少聚类点数设为 1，并按屏幕宽度计算 240–480 px 的最大关联距离。
- 1A 稀疏说明真实雷达帧/回波就稀疏；输出频率不能增加设备实际扫描点。检查 scan Hz、CRC、input dropped 和目标反射。
- 1A 连续而 1B 稀疏，检查范围、边线/屏蔽过滤和聚类；1B 连续而区域 2 跳 ID，增加最大关联距离。
- 区域 2 连续而 Unity 稀疏，检查 Unity FPS 与 `DroppedBatchCount`/日志 `dropped=`。Unity SDK 会合并积压的纯 Move 可视帧以保护主线程，但保留 Down/Up 顺序。

## 雷达连接失败

电脑可设 `192.168.0.10/24`，雷达常用 `192.168.0.100:8487`；两者不能相同。Bridge 每个 Sensor 选择实际 F10/F20 与正确本机 NIC。逐个断开/恢复雷达和 NIC，其他屏幕应持续；检查 `[SCREEN/SENSOR]` tagged reconnect/timeout 日志。

## WPF 控件消失或变模糊

Bridge 在窗口创建前强制 WPF 软件渲染，路径不依赖 GPU。仍需记录 Windows 缩放、投影分辨率、GPU/驱动和精确操作；测试 click、drag、scroll、resize、minimize/restore、跨 DPI 屏移动和 projector focus change。若能复现，保存同一时间段日志与截图，确认运行的是包内 1.2.8 完整 payload，而非缓存旧版。

## 日志关联

Bridge 日志：`%LOCALAPPDATA%/RadarControl/logs/RadarBridge-YYYYMMDD.log`；配置默认在 `%LOCALAPPDATA%/Yuexin/RadarBridge/config.json`，也可 `--profile` 指定。用 `[SCREEN/SENSOR]`、sequence 和 timestamp 对齐 `Player.log` 的 SDK/Bridge/IPC、screenId、batch/frame sequence、pointer/dropped count、latency 和 EventSystem target。

现场问题至少提供：最终 Schema 2 配置、Bridge tagged logs、`Player.log`、Package Manager Resolved Path、Player/包内 EXE SHA、拓扑/Camera 绑定与操作时间线。8 小时门禁见 [INSTALL.md](../INSTALL.md#10-现场-8-小时验收三投影四雷达)。
