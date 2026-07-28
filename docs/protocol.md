# 雷达及 IPC 2 协议

## F10/F20 设备层

| 参数 | F10 | F20 |
| --- | ---: | ---: |
| 推荐量程 | 0.05–10 m | 0.05–40 m |
| 扫描频率 | 10–25 Hz，默认 15 | 10–30 Hz，默认 25 |
| 角分辨率 | 默认 0.27° | 默认 0.3° |
| 输出角度/盲区 | 280° / 230°–310° | 280° / 230°–310° |
| 默认 IP/端口 | `192.168.0.100:8487` | `192.168.0.100:8487` |

每点 `A B C D` 四字节：A/B/C 最高位 0，D 最高位 1。距离厘米为 `((A & 0x7F) << 7) | (B & 0x7F)`；角度为 `(((C & 0x7F) << 7) | (D & 0x7F)) / 16`。CRC 统计 B/C/D 置 1 位数取低 3 位，与 A 低 3 位比较。失败时逐字节重新同步。Profile 只描述软件范围，不发送未定义设备写命令。

## IPC 2 framing 与握手

- Pipe：`Yuexin.RadarBridge`。
- Frame：4-byte little-endian JSON byte length + UTF-8 JSON；length、JSON 和 protocolVersion 在业务层前验证。
- Unity 首帧必须是 protocol 2 `Hello`，payload 带 Unity PID/version 和启用 Screen summaries（stable ID、name、logical width/height、primary、order）。
- Bridge 校验非空/唯一 ID、合法分辨率/order 和恰好一个 Primary，应用 topology 后返回 `HelloAck`：Bridge `1.2.0`、protocol 2、capability `multi-screen` 和最终 screen summaries。
- 业务消息为 `PointerBatch`、Status、Ping/Pong、Shutdown、Error。PointerBatch 含每屏 summary、screen-local sequence/timestamp 和 Pointers；坐标同时包含该屏左下原点 normalized `[0,1]` 与 logical pixels。

每个 Pointer ID 仅保证在其 Screen 内稳定。LEFT/L1、FRONT/F1+F2 overlap、RIGHT/R1 中，FRONT 的 F1/F2 detections 先映射到同一逻辑像素空间再融合；IPC 不暴露两个雷达的重复 Pointer。

## v1/v2 不兼容与恢复

IPC 2 禁止发布 legacy `PointerFrame`，IPC 1 客户端也不理解 screen-addressed `PointerBatch`。主版本不一致时服务端返回 Error 并拒绝业务会话；客户端不得继续消费或静默降级。

恢复顺序：停止 Player/Play Mode → 关闭旧 Bridge → 移除 `#v1.1.x` Package URL/cache → 安装 `https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.0` → 确认 package/SDK/Bridge `1.2.0`、Resolved Path 和 `bridge-version.txt` → 重新握手并检查 HelloAck protocol 2/screen summaries。Pipe Name 不应用来绕过版本校验。

## 诊断契约

Bridge tagged logs 至少能按 `[SCREEN/SENSOR]` 或 `[GLOBAL/IPC]` 关联连接、topology/fusion 和序号。`Player.log` 对应 SDK/Bridge/IPC、screenId、batch/frame sequence、pointer count、dropped count、timestamp/latency、phase/pixel 和 EventSystem target。现场需以 timestamp/sequence 对时并归档两侧日志、Schema 2 Profile、Camera 路由和 EXE SHA。

修改 topology、逻辑分辨率或 OutputRect 会重置受影响 Pointer；只有操作员预期 Up/reset 时才执行。Protocol 自动化不能代替 FRONT overlap 单 Pointer、逐雷达/NIC 重连和 8 小时真实三投影四雷达验证。
