# Radar 项目状态、连接状态与轮廓点设计

日期：2026-08-24

状态：已确认设计，等待用户复核书面规格

范围：Gate A Radar 链路修复；不执行 CameraVision B1-B6

## 1. 目标

修复 Unity Package 使用统一 Radar Provider 时的三个问题：

1. 雷达配置、屏幕拉框和运行参数必须随当前 Unity 项目或当前 Player 沙盒持久化，不能与其他 Unity 项目或其他打包项目串用。
2. Radar 控制台必须显示真实的 Unity IPC 连接状态和真实的雷达连接汇总状态。
3. 每个 `InteractionPoint` 必须同时携带中心点和当前目标的全部实际扫描点/轮廓点，供 Unity 自由取用。

任何 Radar 回归都必须先解决。CameraVision 只作为 `Fp` 通用语义的未来消费者，本轮不实现 CameraVision 功能。

## 2. 已确认的产品决策

- `InteractionPoint.PixelPosition` 继续表示目标中心点。
- `InteractionPoint.Fp` 是 `Vector2Data` 列表，表示目标的实际扫描点或轮廓点。
- `PixelPosition` 和 `Fp` 使用相同的 Surface 像素坐标系，并始终同时传输。
- Radar 的 `Fp` 必须来自实际雷达扫描点，不插值、不拟合、不生成虚构轮廓点。
- 后续 CameraVision 可以用同一字段传输摄像头轮廓点，不新增 Camera 专有轮廓结构。
- 不新增 `RadarScanPoint`，不把轮廓点放进 Radar extension。
- 取消此前计划的“边缘点 / 中心点 / 中心点+边缘点”输出选项。Provider 始终发送中心和轮廓；Unity 自行选择使用方式。
- `Fp` 中的点没有独立 ID 或 Down/Up 生命周期；生命周期仍属于外层 `InteractionPoint`。

## 3. 架构边界

本项目属于长期维护的统一感应设备平台。本次只增加支撑当前需求的最小通用边界。

### 3.1 推荐模块

1. **Unity 启动与沙盒定位**：解析当前 Editor/Player 数据根目录，计算当前项目实例标识并启动对应 Bridge。
2. **Bridge Provider 存储上下文**：向 Provider 暴露可写、项目隔离的数据目录，不包含 Radar 专有配置类型。
3. **Bridge 主机连接状态**：以统一 Interaction IPC 的真实会话为唯一事实来源，向 Provider 暴露只读状态。
4. **Radar 配置与控制台**：在 Provider 数据目录加载/保存配置，并呈现 Unity 和雷达连接状态。
5. **Radar 点云传播链路**：从聚类到融合目标再到标准 `InteractionPoint.Fp`，完整保留实际扫描点。
6. **Unity SDK 合同与分发器**：反序列化、缓存、复制并公开中心点和 `Fp`，不解释 Radar 业务。

### 3.2 通信规则

- Unity Launcher 只负责组合路径、实例键和进程参数，不持有 Radar 配置。
- Bridge 通过 Provider-neutral 服务接口传递数据根目录和主机连接状态。
- Radar Provider 负责 Radar 配置和 Radar 点云映射。
- Unity SDK 只消费标准 Interaction 合同，不通过 Provider ID 分支实现轮廓逻辑。
- 不恢复 Gate A 已关闭的 Radar legacy IPC，不用旧 IPC 推断 Unity 状态。

## 4. 项目沙盒持久化

### 4.1 默认数据根目录

Unity 计算当前运行实例的数据根目录：

- Editor：`<UnityProject>/Library/BlazeInteraction/`
- Player：`<Application.persistentDataPath>/BlazeInteraction/`

Radar Provider 的配置文件位于：

```text
<data-root>/Providers/blaze.radar.f10f20/config.json
```

`InteractionRuntimeSettings.ProfilePath` 保留为高级覆盖项。为空时使用上述自动目录；非空时先按当前 Unity 项目/Player 环境解析并规范化为绝对路径。

### 4.2 隔离规则

- 不同 Unity 项目拥有不同的 Editor `Library`，因此配置隔离。
- 不同打包项目使用各自的 `Application.persistentDataPath`，因此配置隔离。
- 同一项目的多次 Play Mode 运行复用同一份配置，这是预期行为。
- 同一打包项目的升级版本复用该项目沙盒，这是预期行为。
- 统一 Provider 模式不得回退到全局 `%LOCALAPPDATA%/Yuexin/RadarBridge/config.json`。

为防止同时运行的不同 Unity 项目复用错误的 Bridge，默认 Pipe Name 必须附加由规范化数据根目录计算出的稳定项目实例键。Unity 客户端和 Launcher 使用同一个最终 Pipe Name。显式配置的自定义 Pipe Name 仍须在当前项目实例域内解析，避免绕过隔离。

### 4.3 首次启动与保存

- 如果项目沙盒中不存在 Radar 配置，Provider 将内置 `profiles/radar-default.json` 复制为项目配置，然后加载该副本。
- 内置 Package/Bridge 配置只读，运行时不得修改。
- “保存并应用”必须先验证配置，再原子写入项目配置，最后切换运行时配置。
- 保存内容包括屏幕拉框、雷达关联、方向、尺寸、过滤、聚类、融合、交互参数及控制台中所有可编辑 Radar 参数。
- Provider 重启、Unity 重新进入 Play Mode、Unity Editor 重启和 Player 重启后必须恢复相同配置。

### 4.4 错误处理

- 数据根目录不可创建或不可写时，Provider 必须显示明确路径和错误，不得静默写入全局目录。
- 配置 JSON 损坏或验证失败时，不覆盖原文件；控制台显示可操作错误，Radar 不以未知参数启动。
- 原子保存使用同目录临时文件和替换/移动，失败时保留上一份有效文件。

## 5. 真实连接状态

### 5.1 Unity 状态

Bridge 增加 Provider-neutral 的只读主机状态服务。状态来源是统一 `InteractionPipeServer` 的真实会话：

1. Pipe 建立但尚未完成 Hello 验证时，不显示已连接。
2. Hello 验证完成且 HelloAck 成功后，显示 `Unity：已连接`。
3. 客户端断开、心跳超时、协议失败或 Bridge 停止时，显示 `Unity：未连接`。

Radar Provider 订阅该服务并映射到现有 ViewModel。Provider 发布回调成功不等价于 Unity 已连接，不得用“成功生成一帧”推断连接状态。

### 5.2 Radar 状态

控制台顶部增加启用雷达的汇总状态：

- 全部运行：`雷达：2/2 已连接`
- 部分运行：`雷达：1/2 部分连接`
- 全部断开：`雷达：0/2 未连接`
- 没有启用雷达：`雷达：0/0 未配置`

分母是当前配置中启用的雷达数量；分子是运行状态为 `Running` 的数量。汇总必须随设备状态和配置变化实时更新。设备列表继续显示每台雷达的详细状态与错误。

## 6. InteractionPoint.Fp 合同

### 6.1 .NET 合同

`InteractionPoint` 增加可选、非空、只读快照字段：

```csharp
public IReadOnlyList<Vector2Data> Fp { get; init; } = Array.Empty<Vector2Data>();
```

合同规则：

- JSON 字段名固定为 `fp`。
- 缺失 `fp` 等价于空列表，保证旧消息可被新客户端读取。
- 显式 `null` 不合法；元素为 `null` 不合法。
- 每个坐标必须为有限数值。
- 坐标属于外层 `SurfaceId` 的逻辑像素坐标系，与 `PixelPosition` 一致。
- 保留 Provider 给出的点顺序和重复点，不在合同层排序、去重或简化。
- 合同构造时复制输入集合，之后不受调用方修改影响。

### 6.2 Unity 合同

Unity Package 的消息模型增加：

```csharp
[JsonProperty("fp")]
public List<Vector2Data> Fp { get; set; } = new List<Vector2Data>();
```

IPC 反序列化后必须将缺失字段规范化为空列表。`InteractionFrameDispatcher` 的缓存、事件和任何 `CopyWithPhase`/兼容复制路径都必须保留 `Fp`。

本次是向 Interaction IPC 1 增加可选字段，不改变已有字段含义，协议 major 不升级。旧 Unity Package 可以忽略未知 `fp`；新 Package 可以读取旧消息并得到空列表。

### 6.3 Unity 消费方式

Unity 可以直接选择：

- 使用 `point.PixelPosition` 驱动单个光标或交互中心。
- 遍历 `point.Fp` 绘制轮廓、遮挡区或点云。
- 同时使用两者。

SDK 不提供全局“中心/轮廓”选择开关，不替项目决定渲染或碰撞语义。

## 7. Radar 数据流

```text
雷达原始帧
  -> 坐标变换与现有过滤
  -> RadarCluster(center + actual Points)
  -> SensorDetection(center + footprint pixels)
  -> 多雷达融合观察值(center + merged footprints)
  -> 稳定目标/Pointer(center + current footprint)
  -> RadarFrameAdapter
  -> InteractionPoint.PixelPosition + InteractionPoint.Fp
  -> Interaction IPC
  -> Unity InteractionManager
```

规则如下：

- 单雷达聚类的每个实际 `RadarPoint` 通过与中心相同的 Surface 映射转换为 `Vector2Data`，按雷达采样顺序加入 `Fp`。
- 多雷达检测融合为一个中心目标时，合并所有参与检测的实际点。顺序先按规范化 `SensorId`，再保持各传感器采样顺序，确保结果确定。
- 中心点继续使用现有融合、跟踪和平滑逻辑。
- `Fp` 不做平滑，因为它必须表示当前帧的实际扫描形状。
- 跟踪目标在丢帧宽限期没有当前观察值时，`Fp` 为空，不重复发送上一帧轮廓。
- Up/Cancel 等终止帧允许 `Fp` 为空；外层 InteractionPoint 生命周期保持现有行为。
- 非 Radar Provider 在没有轮廓时发送空 `Fp`。CameraVision 填充轮廓属于后续 Gate，不在本轮实现。

## 8. UI 调整

Radar 控制台顶部显示：

- `Unity：已连接/未连接`
- `雷达：已连接数/启用数 + 汇总文本`
- 现有“全部连接/全部断开”操作保持。

不增加中心/边缘输出模式选择。可以在状态或日志中显示当前配置文件绝对路径，便于确认参数保存位置，但不允许在 UI 中暴露 Package 内默认文件为可写配置。

## 9. 测试驱动实施顺序

每一阶段都必须先写失败测试，再做最小实现，再运行该阶段的完整相关测试。

### 阶段 1：核心合同 `Fp`

- .NET 合同：快照不可变、空默认、拒绝 null/非法坐标、JSON 往返、旧 JSON 缺失字段兼容。
- Interaction IPC codec：`fp` 完整往返，未知/缺失字段兼容。
- Unity 合同和 Dispatcher：反序列化、缓存、Added/Updated/Removed、阶段复制均保留 `Fp`。
- Radar/Camera 现有适配器未提供轮廓时保持空列表，不改变中心行为。

### 阶段 2：Radar 实际点传播

- 单聚类的全部实际点按顺序映射到 `Fp`。
- 不插值、不去重、不丢点。
- 多雷达融合合并参与检测的全部点，顺序确定。
- 中心跟踪和平滑不改变 `Fp` 的实际位置。
- 丢帧宽限和终止帧不重复陈旧轮廓。
- 现有中心点、Phase、ID、Surface 路由测试必须继续通过。

### 阶段 3：项目沙盒持久化

- Editor 和 Player 路径解析测试。
- 不同项目根目录生成不同数据目录和 Pipe Name。
- 首次复制默认配置、保存、重载和跨项目隔离测试。
- 自定义 Profile Path 测试。
- 不可写路径、损坏配置和原子保存失败测试。

### 阶段 4：连接状态

- Interaction IPC 建连、HelloAck、断开、心跳超时的状态转换测试。
- Radar Provider 使用统一主机状态而非 legacy IPC 的测试。
- `0/0`、全部、部分和全部断开的 Radar 汇总测试。
- WPF 绑定和状态刷新测试。

### 阶段 5：完整回归与现场验证

- 先记录当前完整 Radar 和全解决方案基线。
- 运行全部 Radar 测试；任何失败先修复，不带失败进入下一阶段。
- 运行完整 .NET 解决方案测试。
- 运行 Unity Package EditMode/PlayMode 相关完整测试。
- 使用已启动的 UnitySkills 在当前 Unity 项目中验证：
  - 保存拉框和参数后退出 Play Mode，再次运行能恢复。
  - Radar 控制台 Unity 状态与真实 Play Mode IPC 一致。
  - 雷达连接汇总与设备列表一致。
  - 收到的 `InteractionPoint.PixelPosition` 和 `Fp` 同时存在。
  - `Fp.Count` 等于目标关联的实际扫描点数量。

## 10. 非目标

- 不实现 CameraVision B1-B6。
- 不实现摄像头轮廓提取。
- 不恢复或扩展 Radar legacy IPC。
- 不为 `Fp` 中的点增加独立生命周期、ID、置信度或设备专有元数据。
- 不在 Unity SDK 中内置轮廓渲染、碰撞或点击策略。
- 不静默截断、插值或压缩实际扫描点；如超出 IPC payload 上限，必须明确报错并记录，而不是发送伪造或不完整轮廓。

## 11. 验收标准

1. 两个不同 Unity 项目修改 Radar 参数后互不影响。
2. 同一 Unity 项目或 Player 重启后，屏幕拉框和全部 Radar 参数保持不变。
3. 控制台显示的 Unity 状态来自统一 Interaction IPC，连接和断开均及时更新。
4. 控制台准确显示启用雷达的连接比例。
5. 每个 Radar `InteractionPoint` 同时包含中心 `PixelPosition` 和全部实际扫描点 `Fp`。
6. Unity 能直接读取 `Fp`；旧消息缺少 `fp` 时得到空列表。
7. 中心点 ID、Phase、坐标、Surface 路由及现有 Radar 行为无回归。
8. 完整 Radar、Interaction IPC、Unity Package 和解决方案测试全部通过。
