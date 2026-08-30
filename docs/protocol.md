# Interaction IPC 1

Interaction IPC 是 Provider-neutral 的 Bridge ↔ Unity 协议。默认 Named Pipe 为 `Blaze.InteractionBridge`，当前 major 为 `1`。它不等同于 RadarControl legacy IPC 2；Gate A Unity 生产路径不得静默降级到 Radar IPC。

## Framing 与 Envelope

每条消息为：

```text
4-byte little-endian UTF-8 JSON byte length
UTF-8 JSON envelope
```

默认最大 payload 为 4 MiB。长度必须为正且不超限；截断、非法 JSON、未知消息类型、错误 payload 或错误 protocol major 都会终止当前 session，但 server accept loop 保持可重连。

Envelope 字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `messageType` | enum string | `Hello`、`HelloAck`、`InteractionFrame`、`Status`、`ProviderChanged`、`Ping`、`Pong`、`Shutdown`、`Error` |
| `protocolVersion` | integer | 必须为 `1` |
| `sequence` | int64 | 消息序号；frame 内另有 Provider/Surface 序号 |
| `payload` | object | 与消息类型对应的 payload |

JSON 使用 camelCase、字符串 enum；坐标对象必须显式包含有限数值 `x` 和 `y`。

## 握手

客户端首帧必须是 `Hello`：

```json
{
  "unityPid": 1234,
  "unityVersion": "2021.3.45f1",
  "sdkVersion": "1.1.1",
  "surfaces": [
    {
      "surfaceId": "FRONT",
      "name": "Front",
      "logicalWidth": 4096,
      "logicalHeight": 1536,
      "isPrimary": true,
      "order": 0
    }
  ]
}
```

Surface ID、Order 必须唯一，宽高为正，且恰好一个 Primary。Bridge 在 topology 生效、默认 Provider 完成 Initialize/Start 后返回 `HelloAck`：

```json
{
  "bridgeVersion": "1.1.1",
  "activeProvider": {
    "id": "blaze.radar.f10f20",
    "instanceId": "radar-main"
  },
  "capabilities": [
    "interaction-point",
    "preview",
    "multi-sensor",
    "calibration",
    "multi-surface"
  ]
}
```

Ack 写入前不得向客户端发布业务帧。默认握手超时 5 秒、心跳超时 10 秒、单次发送超时 2 秒。

## InteractionFrame

一个 frame 属于一个 Provider instance 和一个 Surface：

| 字段 | 规则 |
| --- | --- |
| `providerId` / `providerInstanceId` | 非空，标识插件类型与当前实例 |
| `surfaceId` | 必须存在于 Hello topology |
| `sequence` | 同 Provider/Surface 严格递增 |
| `timestampUnixMs` | frame 采样/输出时间 |
| `points` | 不可为 null；允许空列表表示视觉状态刷新 |

每个 point 含 `id`、Surface/Provider/Source identity、`Hover|Down|Move|Up|Cancel`、normalized `[0,1]`、logical pixel、confidence `[0,1]`、timestamp、可选的 `fp` footprint 和可选 `extensions`。`fp` 是按 point 关联的明细坐标数组，坐标使用与 `pixelPosition` 相同的像素坐标空间；Radar 使用它传实际扫描点，CameraVision 使用它传 21 个手部骨骼点。数组顺序由 Provider 定义且消费者必须保留，重复坐标也是有效数据。旧 JSON 缺少 `fp` 时按空列表处理。Point ID 的稳定域由 Provider 定义；`fp` 的生命周期归外层 point 所有，point 被撤销或离开 frame 后消费者不得将其视为独立生命周期对象。

`extensions` 是新增设备类型的可选数据面，不能改变核心字段含义。消费者必须能在忽略未知扩展时继续处理基础 InteractionPoint。

## 生命周期、背压与顺序

- 含 Down、Up 或 Cancel 的 frame 是可靠生命周期事务，按接纳顺序 FIFO。
- 仅含 Hover/Move 或空点列表的 frame 是视觉状态，可 latest-only 合并。
- 控制队列容量默认 64；控制消息保持有界 FIFO。持续控制流最多连续选择 8 条后必须给待发视觉帧一次机会。
- Unity 接收端重复该策略；生命周期队列满时读取线程等待，而不是丢边。
- Provider 退役时 Cancel 在 ProviderChanged 之前；未完成 HelloAck 或已断线的 session 不接收业务帧。

## 身份与会话

Pipe 使用 `CurrentUserOnly`。服务端从 Windows pipe handle 读取真实客户端 PID 和 Session ID：

- 真实 PID 必须等于 Hello 的 `unityPid`；
- 客户端与 Bridge 必须在同一 Windows Session；
- Bridge 由 Unity 启动时，真实 PID 还必须等于命令行 `--parent-pid`；
- 同一时间只允许一个活动客户端，第二个客户端等待当前 session 结束。

只有有效 Ping/Pong 刷新心跳；仅发送 frame 不能掩盖失活客户端。发送、握手或心跳超时会释放 session，并允许下一个 Hello。

## 错误与恢复

`Error` payload 为 `{ code, message }`。协议/客户端输入错误只关闭当前 session；server 程序错误和 writer fault 不会被伪装成可恢复输入错误。客户端收到 Error 时在 Unity 主线程触发 `ErrorReceived`，随后按配置延迟重连。

恢复顺序：停止旧 Player/Play Mode → 确认没有旧 `BlazeInteractionBridge.exe` 占用 → 校验双方使用 Interaction IPC 1 和项目专属 Pipe → 校验 package/Bridge/Provider `1.1.1` → 重新 Hello。修改 Pipe Name 不能绕过 protocol、版本或 PID 校验。
