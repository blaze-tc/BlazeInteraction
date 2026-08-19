# 雷达控制与 Unity 交互系统 — Codex 执行说明书

> 文档用途：本文件用于交给 Codex 读取并执行。  
> 项目目标：开发一个以 **F10 雷达为默认型号**、同时可切换支持 F20 的独立雷达控制程序，并提供 Unity SDK，使 Unity 能启动该程序、接收雷达点位、完成 UI 与场景射线交互。  
> 目标平台：Windows 10/11 x64。  
> 默认语言：C#。  
> 默认雷达型号：**F10**。  
> 可切换型号：F10 / F20。

---

## 1. Codex 总执行指令

请严格按以下顺序执行，不要跳过分析、测试和验收步骤。

1. 先检查当前仓库结构、现有 Unity 版本、已有程序集定义、包管理方式和命名规范。
2. 不要直接修改现有业务逻辑；优先以独立目录、独立程序集和 Unity Package 的方式接入。
3. 先生成实施计划，再分阶段实现。
4. 每完成一个阶段都要：
   - 编译；
   - 运行单元测试；
   - 记录测试结果；
   - 更新文档；
   - 再进入下一阶段。
5. 雷达协议解析、聚类、坐标标定、IPC 通信必须可独立测试。
6. Unity 主线程不得直接执行阻塞式 TCP、Named Pipe 读取或文件 IO。
7. Unity 不直接连接雷达；雷达连接、协议解析和坐标处理全部由独立程序负责。
8. F10 是默认实现和默认配置，但代码必须通过设备配置和驱动抽象切换到 F20。
9. 不允许把 F10、F20 参数散落在业务代码中；型号差异必须集中在设备描述和配置层。
10. 不得根据未知协议猜测雷达写入命令。说明书未提供的设备控制指令不要自行构造。
11. 第一版仅实现雷达数据接收、软件侧过滤、区域设置、标定和 Unity 交互。
12. 所有异常都必须有明确日志，不允许使用空 `catch`。
13. 不允许用 `Thread.Sleep` 作为线程同步方案。
14. 正式运行时必须使用“最新帧优先”策略，禁止旧帧无限排队。
15. 最终需要提供：
   - 独立雷达控制程序；
   - Unity SDK；
   - 示例 Unity 场景；
   - 自动构建和复制脚本；
   - 配置文件；
   - 单元测试；
   - 使用说明；
   - 故障排查说明。

---

# 2. 项目最终形态

系统采用双进程架构：

```text
F10 / F20 雷达
    │
    │ TCP，默认 192.168.0.100:8487
    ▼
RadarBridge.exe
    ├─ 雷达连接
    ├─ 型号切换
    ├─ TCP 字节流重组
    ├─ 4 字节数据包解析
    ├─ CRC 校验
    ├─ 单圈扫描构建
    ├─ 点云过滤
    ├─ 点云聚类
    ├─ 多目标跟踪
    ├─ 感应区域配置
    ├─ 四点坐标标定
    ├─ 点击状态生成
    ├─ 数据录制与回放
    └─ Named Pipe IPC 服务
    │
    │ 标准化坐标、指针状态、状态消息
    ▼
Unity Radar SDK
    ├─ 启动/关闭 RadarBridge.exe
    ├─ Named Pipe 客户端
    ├─ 接收线程与主线程分发
    ├─ RadarInputModule
    ├─ UI GraphicRaycaster
    ├─ PhysicsRaycaster
    ├─ 多指针事件
    └─ 示例场景
```

---

# 3. 技术栈

## 3.1 独立程序

- .NET 8
- C#
- WPF
- MVVM
- x64
- `System.Net.Sockets`
- `System.IO.Pipes`
- `System.Text.Json`
- 测试框架：xUnit
- 日志：Microsoft.Extensions.Logging 或 Serilog
- 依赖注入：Microsoft.Extensions.DependencyInjection

## 3.2 Unity SDK

应优先兼容当前项目使用的 Unity LTS 版本。实现前先读取当前 Unity 项目的 `ProjectVersion.txt`。

建议最低兼容：

- Unity 2021.3 LTS
- .NET Standard 2.1 可用时优先
- 不依赖 Unity 新输入系统
- 基于 `EventSystem`、`BaseInputModule`、`PointerEventData`

Unity 包形式：

```text
Packages/com.yuexin.radar/
```

或作为本地 UPM Package 放在：

```text
Assets/Packages/com.yuexin.radar/
```

具体路径按现有项目结构决定。

---

# 4. 雷达型号策略

## 4.1 默认型号

默认使用：

```text
F10
```

默认启动配置中必须写明：

```json
{
  "deviceModel": "F10"
}
```

## 4.2 型号切换

控制程序界面必须提供型号选择：

```text
F10
F20
```

切换型号后，应刷新：

- 最大有效距离；
- 默认扫描频率显示；
- 可配置扫描频率范围；
- 默认角度分辨率说明；
- 推荐过滤参数；
- UI 中的型号状态；
- 配置文件。

切换型号不得改变通信解析公式。F10/F20 使用相同的 4 字节点数据格式，但量程、默认频率和推荐参数不同。

## 4.3 型号配置对象

必须创建统一的设备描述接口：

```csharp
public interface IRadarModelProfile
{
    RadarModel Model { get; }
    string DisplayName { get; }
    float MinimumDistanceMeters { get; }
    float MaximumDistanceMeters { get; }
    int MinimumScanFrequencyHz { get; }
    int MaximumScanFrequencyHz { get; }
    int DefaultScanFrequencyHz { get; }
    float DefaultAngularResolutionDegrees { get; }
    float BlindZoneStartDegrees { get; }
    float BlindZoneEndDegrees { get; }
}
```

枚举：

```csharp
public enum RadarModel
{
    F10 = 10,
    F20 = 20
}
```

实现：

```csharp
public sealed class F10RadarModelProfile : IRadarModelProfile
{
    public RadarModel Model => RadarModel.F10;
    public string DisplayName => "FaseLase F10";
    public float MinimumDistanceMeters => 0.05f;
    public float MaximumDistanceMeters => 10f;
    public int MinimumScanFrequencyHz => 10;
    public int MaximumScanFrequencyHz => 25;
    public int DefaultScanFrequencyHz => 15;
    public float DefaultAngularResolutionDegrees => 0.27f;
    public float BlindZoneStartDegrees => 230f;
    public float BlindZoneEndDegrees => 310f;
}
```

```csharp
public sealed class F20RadarModelProfile : IRadarModelProfile
{
    public RadarModel Model => RadarModel.F20;
    public string DisplayName => "FaseLase F20";
    public float MinimumDistanceMeters => 0.05f;
    public float MaximumDistanceMeters => 40f;
    public int MinimumScanFrequencyHz => 10;
    public int MaximumScanFrequencyHz => 30;
    public int DefaultScanFrequencyHz => 25;
    public float DefaultAngularResolutionDegrees => 0.3f;
    public float BlindZoneStartDegrees => 230f;
    public float BlindZoneEndDegrees => 310f;
}
```

所有型号参数必须从 `IRadarModelProfile` 获取，不允许在其他类中直接写死。

---

# 5. 说明书协议约束

## 5.1 网络参数

默认雷达地址：

```text
192.168.0.100
```

默认 TCP 端口：

```text
8487
```

电脑有线网卡需与雷达处于同一网段。

示例：

```text
雷达 IP：192.168.0.100
电脑 IP：192.168.0.20
子网掩码：255.255.255.0
```

程序应允许配置雷达 IP 和端口，但默认值保持上述配置。

## 5.2 点数据格式

每个点使用 4 字节：

```text
A B C D
```

合法包基本特征：

```text
A7 = 0
B7 = 0
C7 = 0
D7 = 1
```

有效数据：

- A6、A5、A4：校验位；
- A3～A0、B6～B0、C6：距离；
- C5～C0、D6～D0：角度；
- 距离单位：cm；
- 角度单位：1/16°。

## 5.3 距离解析

```csharp
public static int DecodeDistanceCentimeters(
    byte a,
    byte b,
    byte c)
{
    var distance = a & 0x0F;
    distance <<= 7;
    distance += b & 0x7F;
    distance <<= 1;

    if ((c & 0x40) != 0)
    {
        distance++;
    }

    return distance;
}
```

## 5.4 角度解析

```csharp
public static int DecodeAngleRaw(
    byte c,
    byte d)
{
    var angle = c & 0x3F;
    angle <<= 7;
    angle += d & 0x7F;
    return angle;
}
```

```csharp
public static float DecodeAngleDegrees(
    byte c,
    byte d)
{
    return DecodeAngleRaw(c, d) / 16f;
}
```

## 5.5 CRC 校验

A6～A4 保存 B、C、D 三个字节中二进制 1 的数量总和的低三位。

```csharp
public static int CalculateCrc(byte b, byte c, byte d)
{
    return (BitOperations.PopCount(b)
          + BitOperations.PopCount(c)
          + BitOperations.PopCount(d)) & 0x07;
}
```

读取原 CRC：

```csharp
public static int ReadPacketCrc(byte a)
{
    return (a >> 4) & 0x07;
}
```

判断：

```csharp
public static bool IsCrcValid(
    byte a,
    byte b,
    byte c,
    byte d)
{
    return ReadPacketCrc(a) == CalculateCrc(b, c, d);
}
```

如目标框架不能直接对 `byte` 使用 `BitOperations.PopCount`，应使用预计算的 256 项 PopCount 表。

## 5.6 扫描圈判断

同一圈内角度递增。

若：

```text
currentAngleRaw < previousAngleRaw
```

则视为新一圈开始。

应增加容错：

- 忽略单个明显异常角度；
- 只有上一圈达到最小有效点数时才提交；
- 新一圈开始时发布上一圈快照；
- 发布后不得再修改该快照。

---

# 6. 推荐仓库结构

Codex 应根据实际仓库调整路径，但职责必须保持一致。

```text
RadarControl/
├─ RadarControl.sln
├─ README.md
├─ docs/
│  ├─ architecture.md
│  ├─ protocol.md
│  ├─ calibration.md
│  ├─ unity-integration.md
│  └─ troubleshooting.md
│
├─ src/
│  ├─ Radar.Contracts/
│  │  ├─ Radar.Contracts.csproj
│  │  ├─ RadarModel.cs
│  │  ├─ RadarPoint.cs
│  │  ├─ RadarScanFrame.cs
│  │  ├─ RadarTarget.cs
│  │  ├─ RadarPointer.cs
│  │  ├─ RadarStatus.cs
│  │  └─ IpcMessages.cs
│  │
│  ├─ Radar.Protocol/
│  │  ├─ Radar.Protocol.csproj
│  │  ├─ FaseLasePacketParser.cs
│  │  ├─ FaseLaseCrc.cs
│  │  ├─ RadarByteStreamDecoder.cs
│  │  └─ RadarScanFrameBuilder.cs
│  │
│  ├─ Radar.Device/
│  │  ├─ Radar.Device.csproj
│  │  ├─ IRadarModelProfile.cs
│  │  ├─ F10RadarModelProfile.cs
│  │  ├─ F20RadarModelProfile.cs
│  │  ├─ RadarModelProfileFactory.cs
│  │  ├─ RadarConnectionOptions.cs
│  │  ├─ RadarTcpClient.cs
│  │  └─ RadarConnectionService.cs
│  │
│  ├─ Radar.Processing/
│  │  ├─ Radar.Processing.csproj
│  │  ├─ RadarCoordinateConverter.cs
│  │  ├─ PointFilters.cs
│  │  ├─ SequentialPointClusterer.cs
│  │  ├─ RadarTargetTracker.cs
│  │  ├─ ExponentialPositionFilter.cs
│  │  ├─ HomographyCalibration.cs
│  │  ├─ PolygonRegion.cs
│  │  └─ PointerStateMachine.cs
│  │
│  ├─ Radar.Ipc/
│  │  ├─ Radar.Ipc.csproj
│  │  ├─ RadarPipeServer.cs
│  │  ├─ IpcFrameCodec.cs
│  │  └─ IpcProtocolVersion.cs
│  │
│  ├─ Radar.Configuration/
│  │  ├─ Radar.Configuration.csproj
│  │  ├─ RadarAppConfiguration.cs
│  │  ├─ RadarConfigurationStore.cs
│  │  └─ ConfigurationValidator.cs
│  │
│  └─ Radar.Bridge.Wpf/
│     ├─ Radar.Bridge.Wpf.csproj
│     ├─ App.xaml
│     ├─ MainWindow.xaml
│     ├─ ViewModels/
│     ├─ Views/
│     ├─ Services/
│     └─ Resources/
│
├─ tests/
│  ├─ Radar.Protocol.Tests/
│  ├─ Radar.Processing.Tests/
│  ├─ Radar.Device.Tests/
│  ├─ Radar.Ipc.Tests/
│  └─ Radar.Configuration.Tests/
│
├─ tools/
│  └─ Radar.SampleData/
│
└─ UnityPackage/
   └─ com.yuexin.radar/
      ├─ package.json
      ├─ Runtime/
      │  ├─ Yuexin.Radar.Runtime.asmdef
      │  ├─ RadarBridgeLauncher.cs
      │  ├─ RadarPipeClient.cs
      │  ├─ RadarFrameDispatcher.cs
      │  ├─ RadarInputModule.cs
      │  ├─ RadarPointerState.cs
      │  ├─ RadarRuntimeSettings.cs
      │  └─ RadarDebugOverlay.cs
      ├─ Editor/
      │  ├─ Yuexin.Radar.Editor.asmdef
      │  ├─ RadarSettingsProvider.cs
      │  └─ RadarBuildProcessor.cs
      ├─ Samples~/
      │  └─ BasicInteraction/
      └─ Documentation~/
         └─ index.md
```

---

# 7. 核心数据结构

## 7.1 原始雷达点

```csharp
public readonly record struct RadarPoint(
    int DistanceCentimeters,
    int AngleRaw,
    float AngleDegrees,
    float X,
    float Y);
```

建议坐标单位统一为米：

```text
X、Y：米
DistanceCentimeters：保留原始协议值
```

## 7.2 单圈数据

```csharp
public sealed record RadarScanFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> Points);
```

## 7.3 聚类目标

```csharp
public sealed record RadarCluster(
    int ClusterIndex,
    IReadOnlyList<RadarPoint> Points,
    float CenterX,
    float CenterY,
    float WidthMeters,
    float EstimatedDistanceMeters);
```

## 7.4 跟踪目标

```csharp
public sealed record RadarTarget(
    int TrackId,
    float PhysicalX,
    float PhysicalY,
    float NormalizedX,
    float NormalizedY,
    float Confidence,
    int PointCount,
    bool IsConfirmed);
```

## 7.5 指针事件

```csharp
public enum RadarPointerPhase
{
    Hover = 0,
    Down = 1,
    Move = 2,
    Up = 3
}
```

```csharp
public sealed record RadarPointer(
    int PointerId,
    float NormalizedX,
    float NormalizedY,
    RadarPointerPhase Phase,
    float Confidence,
    long TimestampUnixMilliseconds);
```

---

# 8. TCP 接收与字节流解析

## 8.1 必须使用缓存

TCP 不能假定一次 `ReadAsync` 收到完整 4 字节。

必须实现一个可增长或环形缓冲区。

处理逻辑：

```text
收到字节
  ↓
追加到缓存
  ↓
缓存长度 >= 4
  ↓
检查第 0～3 字节是否满足 A/B/C/D 高位特征
  ├─ 不满足：丢弃第 1 个字节
  └─ 满足：执行 CRC
       ├─ CRC 失败：丢弃第 1 个字节
       └─ CRC 成功：解析 4 字节并全部移除
```

## 8.2 同步恢复

不能在 CRC 失败时直接跳过 4 字节。

原因：当前缓存可能从一个错误偏移位置开始。

正确方式：

```text
CRC 失败 → 只移动 1 字节 → 重新寻找合法包
```

## 8.3 网络线程

使用：

```csharp
Task
CancellationToken
Socket.ReceiveAsync
```

不得在 WPF UI 线程或 Unity 主线程上读取网络。

## 8.4 重连

建议状态：

```text
Disconnected
Connecting
Connected
Reconnecting
Faulted
```

推荐参数：

```text
数据超时警告：500ms
判定断线：1500ms
重连间隔：500ms、1s、2s、5s
最大重连间隔：5s
```

连接恢复后：

- 清空旧字节缓存；
- 清空未完成扫描圈；
- 保留配置；
- 重新开始序列号；
- 发布连接恢复状态。

---

# 9. 坐标系统

## 9.1 雷达极坐标转平面坐标

按说明书角度定义：

```csharp
var radians = angleDegrees * MathF.PI / 180f;

var x = distanceMeters * MathF.Cos(radians);
var y = distanceMeters * MathF.Sin(radians);
```

## 9.2 安装修正

需要支持：

```csharp
public sealed class RadarTransformOptions
{
    public float RotationDegrees { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public float OffsetXMeters { get; set; }
    public float OffsetYMeters { get; set; }
}
```

转换顺序必须固定：

```text
原始极坐标
→ 笛卡尔坐标
→ FlipX / FlipY
→ Rotation
→ Offset
→ 区域过滤
→ 标定映射
```

---

# 10. 感应区域

## 10.1 区域类型

第一版实现：

1. 四边形有效区域；
2. 多边形屏蔽区域；
3. 最近距离；
4. 最远距离；
5. 有效角度；
6. 左、右、上、下边缘死区。

不需要第一版实现复杂自由曲线编辑。

## 10.2 默认区域

F10 默认最大距离应不超过：

```text
10m
```

但应用层初始建议值可配置为：

```text
0.1m ～ 5m
```

具体场地通过界面调整。

F20 切换后可允许配置到：

```text
40m
```

## 10.3 多边形包含判断

实现稳定的点在多边形内判断。

边界上的点应视为有效，避免光标在边缘抖动消失。

---

# 11. 点云过滤、聚类与目标跟踪

## 11.1 过滤顺序

```text
协议合法性
→ 最小距离
→ 最大距离
→ 盲区
→ 用户有效角度
→ 有效多边形
→ 屏蔽多边形
→ 孤立点过滤
→ 聚类
```

## 11.2 聚类算法

第一版使用相邻扫描点顺序聚类。

相邻点空间距离：

```text
gap = sqrt((x2-x1)^2 + (y2-y1)^2)
```

若：

```text
gap > dynamicThreshold
```

则开始新聚类。

动态阈值建议：

```text
threshold = baseGap + distanceMeters * distanceScale
```

默认值必须放配置中，不得写死。

示例初始值：

```json
{
  "baseGapMeters": 0.08,
  "distanceScale": 0.015,
  "minimumClusterPointCount": 2,
  "maximumClusterWidthMeters": 0.8
}
```

## 11.3 目标中心

目标中心应支持：

- 算术平均；
- 中位数；
- 最近点。

默认使用中位数或截尾平均，减少边缘噪点影响。

## 11.4 跟踪

第一版使用最近邻匹配：

- 每个新聚类与上一帧目标计算距离；
- 在最大关联距离内匹配最近目标；
- 未匹配目标进入丢失状态；
- 未匹配聚类创建新候选目标。

默认参数：

```json
{
  "confirmFrames": 2,
  "lostFrames": 3,
  "maximumAssociationDistanceMeters": 0.35,
  "smoothingAlpha": 0.5
}
```

## 11.5 最新帧优先

处理链路只保留最新完整扫描圈。

可以使用：

```csharp
Channel<T>
```

并配置容量为 1，满时丢弃旧帧。

禁止建立无限队列。

---

# 12. 四点标定

## 12.1 标定目标

将雷达物理坐标映射到 Unity 标准化屏幕坐标：

```text
X：0～1
Y：0～1
```

## 12.2 四角顺序

界面必须要求按固定顺序标定：

```text
左上
右上
右下
左下
```

对应 Unity 标准化坐标：

```text
左上：(0, 1)
右上：(1, 1)
右下：(1, 0)
左下：(0, 0)
```

## 12.3 映射方式

实现 3×3 Homography。

输入：

```text
雷达平面四个点
```

输出：

```text
标准化矩形四个点
```

必须提供：

```csharp
public interface ICalibrationMapper
{
    bool IsValid { get; }

    bool TryMap(
        float physicalX,
        float physicalY,
        out float normalizedX,
        out float normalizedY);
}
```

## 12.4 标定验证

完成四点标定后：

- 显示四角误差；
- 显示中心测试点；
- 超出 0～1 的坐标可选择 Clamp 或丢弃；
- 默认设置为丢弃有效区域外点；
- 不允许无效矩阵被保存。

## 12.5 配置持久化

保存：

- 四个物理角点；
- Homography 矩阵；
- 创建时间；
- 雷达型号；
- 雷达安装变换；
- 校验误差。

型号切换时：

- 不自动删除标定；
- 明确提示用户型号切换可能影响量程和点云密度；
- 允许保留或重新标定。

---

# 13. 点击与指针状态

## 13.1 点击模式

必须实现：

```csharp
public enum RadarInteractionMode
{
    Touch = 0,
    Dwell = 1,
    EnterTrigger = 2,
    HoverOnly = 3
}
```

## 13.2 Touch 模式

适合雷达扫描面贴近屏幕或投影面：

```text
目标确认 → Down
持续存在并移动 → Move
目标丢失超过容忍帧 → Up
```

## 13.3 Dwell 模式

```text
目标出现 → Hover
在指定半径内停留指定时长 → Click
离开停留区域 → 重置计时
```

默认：

```text
停留时间：800ms
允许移动半径：0.03 标准化坐标
```

## 13.4 EnterTrigger 模式

目标首次进入指定区域：

```text
Down + Up
```

只触发一次，离开区域后才可再次触发。

## 13.5 防抖

必须支持：

```text
目标确认帧数
目标丢失容忍帧数
最小按下时间
最大点击移动距离
拖拽启动距离
```

---

# 14. 独立程序界面

## 14.1 主窗口布局

建议：

```text
左侧：设备与连接
中间：点云和区域编辑
右侧：过滤、标定、交互、Unity
底部：状态和日志
```

## 14.2 设备区

必须包含：

- 型号下拉框，默认 F10；
- F10/F20 切换；
- 本机网卡选择；
- 本机 IP 显示；
- 雷达 IP；
- 端口；
- 连接；
- 断开；
- 自动重连；
- 当前连接状态；
- 数据接收速率；
- 实际扫描圈频率；
- CRC 错误数；
- 丢弃数据数。

## 14.3 点云视图

必须显示：

- 雷达原点；
- 0°、90°、180°；
- 230°～310°盲区；
- 距离网格；
- 原始点；
- 有效点；
- 聚类范围；
- 跟踪目标；
- TrackId；
- 标准化坐标；
- 当前鼠标模拟点。

显示层应可单独开关。

## 14.4 区域编辑

必须支持：

- 拖拽四个有效区域顶点；
- 添加屏蔽多边形；
- 删除屏蔽多边形；
- 设置最近距离；
- 设置最远距离；
- 旋转；
- X 翻转；
- Y 翻转；
- 偏移；
- 恢复默认。

## 14.5 标定

必须支持：

- 开始标定；
- 当前标定步骤；
- 采集当前目标中心；
- 重做当前点；
- 保存标定；
- 清除标定；
- 标定误差显示；
- Unity 坐标预览。

## 14.6 Unity 状态

显示：

- Unity 是否已连接；
- Unity 进程 ID；
- Unity 分辨率；
- IPC 协议版本；
- 最近一帧发送时间；
- 当前指针数；
- 最近一次错误。

---

# 15. 配置文件

## 15.1 默认配置

文件：

```text
RadarBridge/default-profile.json
```

默认型号必须是 F10。

示例：

```json
{
  "schemaVersion": 1,
  "device": {
    "deviceModel": "F10",
    "radarIp": "192.168.0.100",
    "port": 8487,
    "localIp": "",
    "autoReconnect": true
  },
  "transform": {
    "rotationDegrees": 0,
    "flipX": false,
    "flipY": false,
    "offsetXMeters": 0,
    "offsetYMeters": 0
  },
  "range": {
    "minimumDistanceMeters": 0.1,
    "maximumDistanceMeters": 5.0,
    "minimumAngleDegrees": 0,
    "maximumAngleDegrees": 360
  },
  "clustering": {
    "baseGapMeters": 0.08,
    "distanceScale": 0.015,
    "minimumClusterPointCount": 2,
    "maximumClusterWidthMeters": 0.8
  },
  "tracking": {
    "confirmFrames": 2,
    "lostFrames": 3,
    "maximumAssociationDistanceMeters": 0.35,
    "smoothingAlpha": 0.5
  },
  "interaction": {
    "mode": "Touch",
    "dwellMilliseconds": 800,
    "dwellRadiusNormalized": 0.03,
    "dragThresholdNormalized": 0.015
  },
  "ipc": {
    "pipeName": "Yuexin.RadarBridge",
    "sendRawPoints": false,
    "sendClusters": false
  }
}
```

## 15.2 用户配置目录

运行时配置放在：

```text
%LocalAppData%/Yuexin/RadarBridge/
```

例如：

```text
%LocalAppData%/Yuexin/RadarBridge/config.json
%LocalAppData%/Yuexin/RadarBridge/calibration.json
%LocalAppData%/Yuexin/RadarBridge/logs/
%LocalAppData%/Yuexin/RadarBridge/recordings/
```

程序升级不得覆盖用户标定数据。

---

# 16. IPC 通信

## 16.1 方案

使用：

```text
Named Pipe
```

默认名称：

```text
Yuexin.RadarBridge
```

## 16.2 协议

第一版可使用长度前缀 JSON，确保 Unity 兼容性和可调试性。

帧格式：

```text
4 字节小端 PayloadLength
N 字节 UTF-8 JSON
```

禁止使用换行作为消息边界。

## 16.3 消息类型

```csharp
public enum IpcMessageType
{
    Hello = 1,
    HelloAck = 2,
    Status = 3,
    PointerFrame = 4,
    RawScanFrame = 5,
    ConfigurationChanged = 6,
    Error = 7,
    Ping = 8,
    Pong = 9,
    Shutdown = 10
}
```

基础消息：

```csharp
public sealed record IpcEnvelope(
    int ProtocolVersion,
    IpcMessageType MessageType,
    long Sequence,
    long TimestampUnixMilliseconds,
    JsonElement Payload);
```

## 16.4 握手

Unity 连接后发送：

```json
{
  "protocolVersion": 1,
  "messageType": "Hello",
  "payload": {
    "unityProcessId": 1234,
    "unityVersion": "2021.3.45f1",
    "screenWidth": 1920,
    "screenHeight": 1080
  }
}
```

Bridge 返回：

```json
{
  "protocolVersion": 1,
  "messageType": "HelloAck",
  "payload": {
    "bridgeVersion": "1.0.0",
    "deviceModel": "F10",
    "connected": true
  }
}
```

## 16.5 指针帧

```json
{
  "protocolVersion": 1,
  "messageType": "PointerFrame",
  "sequence": 100,
  "timestampUnixMilliseconds": 1784515200000,
  "payload": {
    "pointers": [
      {
        "pointerId": 1,
        "normalizedX": 0.42,
        "normalizedY": 0.67,
        "phase": "Move",
        "confidence": 0.93
      }
    ]
  }
}
```

## 16.6 心跳

- Unity 每 1 秒发送 Ping；
- Bridge 返回 Pong；
- 3 秒未收到心跳，Bridge 标记 Unity 离线；
- Unity 断开后 Bridge 继续运行或退出，由启动参数控制。

---

# 17. Unity SDK

## 17.1 启动 Bridge

实现：

```csharp
public sealed class RadarBridgeLauncher : MonoBehaviour
{
    public bool AutoStart = true;
    public bool ExitBridgeWithUnity = true;
}
```

启动参数：

```text
--parent-pid <UnityPid>
--profile <ProfilePath>
--minimized
```

必须避免重复启动：

- 先尝试连接 Named Pipe；
- 如已有 Bridge 在线，则复用；
- 只有连接失败且允许自动启动时才启动进程。

## 17.2 进程路径

构建后：

```text
Game.exe
RadarBridge/
    RadarBridge.exe
    *.dll
Game_Data/
```

Unity Editor 中路径和正式版路径要分别处理。

## 17.3 Pipe 客户端

要求：

- 后台线程连接；
- 自动重连；
- CancellationToken；
- 收到消息后写入线程安全最新帧缓存；
- 主线程 `Update` 中消费最新帧；
- Unity 销毁时停止线程；
- 不允许后台线程访问 UnityEngine 对象。

## 17.4 坐标换算

Bridge 发送标准化坐标。

Unity：

```csharp
var screenPosition = new Vector2(
    normalizedX * Screen.width,
    normalizedY * Screen.height);
```

默认不反转 Y，因为 Bridge 使用左下为 `(0,0)`、左上为 `(0,1)`。

## 17.5 RadarInputModule

创建：

```csharp
public sealed class RadarInputModule : BaseInputModule
```

必须支持：

- PointerEnter；
- PointerExit；
- PointerDown；
- PointerUp；
- PointerClick；
- BeginDrag；
- Drag；
- EndDrag；
- Drop；
- 多 PointerId；
- GraphicRaycaster；
- PhysicsRaycaster；
- Physics2DRaycaster。

每个 PointerId 必须拥有独立的：

```csharp
PointerEventData
pointerPress
rawPointerPress
pointerDrag
hovered
pressPosition
clickTime
```

## 17.6 UI 和场景优先级

使用：

```csharp
eventSystem.RaycastAll(pointerEventData, raycastResults);
```

按 Unity 默认排序选择第一命中对象。

UI Canvas 必须有：

```text
GraphicRaycaster
```

摄像机按场景需求有：

```text
PhysicsRaycaster
Physics2DRaycaster
```

## 17.7 系统输入模块冲突

示例场景中应明确：

- 使用 RadarInputModule 时停用 StandaloneInputModule；
- 或实现“同时允许鼠标调试”的兼容模式；
- 不得让两个输入模块重复触发相同点击。

推荐提供：

```text
Radar Only
Radar + Mouse Debug
```

两种模式。

---

# 18. 数据录制与回放

## 18.1 必须实现

没有雷达时，开发和测试仍应可进行。

录制内容：

- 原始 TCP 字节；
- 时间戳；
- 连接状态；
- 配置快照；
- 雷达型号。

文件扩展名：

```text
.radarrec
```

## 18.2 回放

回放必须走与真实 TCP 数据相同的解析流程。

不得把录制内容直接转换为最终目标坐标后回放，否则无法验证协议和聚类。

支持：

- 1x；
- 0.5x；
- 2x；
- 单圈步进；
- 循环；
- 暂停。

## 18.3 模拟模式

可增加程序内置的合成点模拟器，但正式验收必须使用真实录制数据。

---

# 19. 测试要求

## 19.1 协议测试

必须覆盖：

1. 已知合法 4 字节包；
2. CRC 错误；
3. A/B/C 高位错误；
4. D 高位错误；
5. 半包；
6. 粘包；
7. 错位后重新同步；
8. 多个连续包；
9. 扫描圈角度回绕；
10. 连接重置后缓存清空。

示例包：

```text
30 14 13 AF
```

按说明书示例应解析为：

```text
距离：40cm
角度：154.9375°
```

请在测试中验证 CRC 和结果。

## 19.2 型号切换测试

必须覆盖：

```text
默认创建 F10 Profile
F10 最大距离 = 10m
F10 默认频率 = 15Hz
F10 频率范围 = 10～25Hz

切换 F20 Profile
F20 最大距离 = 40m
F20 默认频率 = 25Hz
F20 频率范围 = 10～30Hz
```

还需验证：

- 切换型号不会改变解析结果；
- 配置保存后重启仍为选定型号；
- 无效型号回退到 F10；
- F10 配置的最大距离不能超过 10m；
- F20 配置的最大距离不能超过 40m。

## 19.3 聚类测试

覆盖：

- 同一物体连续点聚为一个目标；
- 大间距分为两个目标；
- 单个孤立点被过滤；
- 远距离动态阈值生效；
- 聚类中心稳定。

## 19.4 跟踪测试

覆盖：

- 目标持续移动时 TrackId 不变化；
- 两个目标不会轻易交换 TrackId；
- 短暂丢失不立即触发 Up；
- 达到 lostFrames 后触发 Up；
- 新目标经过 confirmFrames 后才确认。

## 19.5 标定测试

覆盖：

- 四角精确映射；
- 中心点映射；
- 倾斜四边形；
- X/Y 翻转；
- 旋转；
- 无效四边形；
- 共线点拒绝；
- 映射误差。

## 19.6 IPC 测试

覆盖：

- 长度前缀半包；
- 多消息粘包；
- 非法长度；
- 非法 JSON；
- 协议版本不匹配；
- 断线重连；
- 心跳超时。

## 19.7 Unity 测试

至少提供：

- Button 点击；
- Toggle；
- Slider 拖动；
- ScrollRect；
- 3D Cube 点击；
- 2D Collider 点击；
- 两个指针同时存在；
- RadarInputModule 与鼠标调试模式。

---

# 20. WPF 与线程安全要求

1. 网络线程不直接更新 UI。
2. 通过 `Dispatcher` 或线程安全状态快照更新 UI。
3. 点云绘制不得每点创建 WPF 控件。
4. 使用单个 Canvas 绘制或自定义 DrawingVisual。
5. UI 刷新频率可限制为 30FPS。
6. 点云处理频率保持雷达扫描频率。
7. 日志 UI 只显示最近固定数量记录，例如 500 条。
8. 日志文件异步写入。
9. 关闭程序时按顺序：
   - 停止接收；
   - 取消处理任务；
   - 关闭 Pipe；
   - 保存配置；
   - 等待任务结束；
   - 退出进程。

---

# 21. 构建与发布

## 21.1 Bridge 发布

使用：

```bash
dotnet publish src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=false
```

第一版不强制单文件发布，避免原生依赖和调试困难。

## 21.2 Unity 自动复制

实现 `IPostprocessBuildWithReport`：

- 构建完成后；
- 将 Bridge 发布目录复制到游戏 EXE 同级 `RadarBridge/`；
- 不覆盖用户 AppData 配置；
- 验证 `RadarBridge.exe` 存在；
- 缺失时构建失败并给出明确错误。

## 21.3 版本

Bridge、Unity SDK 和 IPC 协议分别维护版本：

```text
BridgeVersion
UnitySdkVersion
IpcProtocolVersion
```

IPC 版本不兼容时必须拒绝通信并显示原因。

---

# 22. 分阶段执行计划

## 阶段 0：仓库检查

Codex 执行：

- [ ] 读取当前目录结构；
- [ ] 读取 Unity `ProjectVersion.txt`；
- [ ] 检查是否已有解决方案；
- [ ] 检查现有命名空间；
- [ ] 检查是否已有 InputModule；
- [ ] 检查 BuildProcessor；
- [ ] 输出实施计划；
- [ ] 不写业务代码。

阶段输出：

```text
docs/radar-current-project-analysis.md
```

---

## 阶段 1：Contracts 与型号配置

完成：

- [ ] Radar.Contracts；
- [ ] RadarModel；
- [ ] IRadarModelProfile；
- [ ] F10RadarModelProfile；
- [ ] F20RadarModelProfile；
- [ ] RadarModelProfileFactory；
- [ ] 默认型号 F10；
- [ ] 配置校验；
- [ ] 单元测试。

验收：

```text
dotnet test
```

全部通过。

---

## 阶段 2：雷达协议解析

完成：

- [ ] CRC；
- [ ] 4 字节解析；
- [ ] 字节流重同步；
- [ ] 扫描圈构建；
- [ ] 示例包测试；
- [ ] 半包/粘包/错位测试。

验收：

- 解析结果正确；
- 错位可恢复；
- CRC 错误不会破坏后续包；
- 无无限内存增长。

---

## 阶段 3：TCP 连接

完成：

- [ ] 绑定本机网卡；
- [ ] 雷达 IP/端口配置；
- [ ] 异步连接；
- [ ] 自动重连；
- [ ] 超时检测；
- [ ] 状态事件；
- [ ] 日志；
- [ ] 模拟服务器测试。

验收：

- 断网后恢复；
- 重连后旧缓存清空；
- 关闭程序无后台残留任务。

---

## 阶段 4：点云处理

完成：

- [ ] 坐标转换；
- [ ] 安装变换；
- [ ] 距离和角度过滤；
- [ ] 有效区域；
- [ ] 屏蔽区域；
- [ ] 聚类；
- [ ] 目标跟踪；
- [ ] 平滑；
- [ ] 最新帧优先。

验收：

- 单元测试全部通过；
- 处理一小时无队列增长；
- 延迟不会持续增加。

---

## 阶段 5：四点标定

完成：

- [ ] Homography；
- [ ] 四角采集；
- [ ] 标定校验；
- [ ] 标准化坐标；
- [ ] 标定保存；
- [ ] 型号切换提示；
- [ ] 标定测试。

验收：

- 四角误差满足配置阈值；
- 中心映射正确；
- 无效四边形不可保存。

---

## 阶段 6：Pointer 状态机

完成：

- [ ] Touch；
- [ ] Dwell；
- [ ] EnterTrigger；
- [ ] HoverOnly；
- [ ] Down/Move/Up；
- [ ] 丢失容忍；
- [ ] 点击防抖；
- [ ] 拖拽阈值；
- [ ] 状态机测试。

---

## 阶段 7：IPC

完成：

- [ ] Named Pipe Server；
- [ ] 长度前缀；
- [ ] Hello/HelloAck；
- [ ] PointerFrame；
- [ ] Status；
- [ ] Ping/Pong；
- [ ] 错误消息；
- [ ] 重连测试。

---

## 阶段 8：WPF 控制程序

完成：

- [ ] 主窗口；
- [ ] F10/F20 切换；
- [ ] 连接管理；
- [ ] 点云显示；
- [ ] 区域编辑；
- [ ] 标定；
- [ ] 过滤参数；
- [ ] Pointer 模式；
- [ ] Unity 状态；
- [ ] 日志；
- [ ] 配置保存；
- [ ] 数据录制；
- [ ] 数据回放。

F10 必须是首次启动默认选中项。

---

## 阶段 9：Unity SDK

完成：

- [ ] UPM 包；
- [ ] Bridge Launcher；
- [ ] Named Pipe Client；
- [ ] 最新帧缓存；
- [ ] 主线程分发；
- [ ] RadarInputModule；
- [ ] 多指针；
- [ ] UI 射线；
- [ ] 3D 射线；
- [ ] 2D 射线；
- [ ] Debug Overlay；
- [ ] Settings Provider；
- [ ] 示例场景。

---

## 阶段 10：构建与文档

完成：

- [ ] Bridge 发布脚本；
- [ ] Unity BuildProcessor；
- [ ] 示例配置；
- [ ] 安装说明；
- [ ] 网络配置说明；
- [ ] F10/F20 切换说明；
- [ ] 标定说明；
- [ ] Unity 接入说明；
- [ ] 故障排查；
- [ ] 发布包验证。

---

# 23. 验收标准

## 23.1 设备连接

- 默认按 F10 配置启动；
- 可切换 F20；
- 可连接 `192.168.0.100:8487`；
- 可选择指定本机网卡；
- 断线自动重连；
- 连接状态清晰。

## 23.2 协议

- 连续运行 8 小时无持续错位；
- CRC 错误可统计；
- 错误帧不会导致后续帧全部失败；
- 扫描圈识别稳定。

## 23.3 坐标

- 可设置有效区域；
- 可设置屏蔽区域；
- 可进行四点标定；
- 输出标准化坐标；
- Unity 分辨率改变后不需要重新标定。

## 23.4 交互

- UI Button 可点击；
- Slider 可拖动；
- ScrollRect 可拖动；
- 3D/2D 对象可接收射线事件；
- 支持至少两个目标；
- PointerId 稳定；
- 短暂丢点不会反复点击。

## 23.5 独立程序

- 可单独运行；
- 可查看原始点云；
- 可修改区域；
- 可保存配置；
- 可录制；
- 可回放；
- 可查看 Unity 连接状态。

## 23.6 Unity 集成

- Unity 启动后可自动启动 Bridge；
- 已有 Bridge 时不会重复启动；
- Unity 关闭时按配置退出 Bridge；
- Bridge 崩溃后 Unity 可重连；
- Unity 主线程无阻塞；
- 正式构建自动携带 Bridge。

---

# 24. 不在第一版范围内

以下内容禁止在未取得厂家协议前自行实现：

- 写入雷达内部扫描频率；
- 写入设备 IP；
- 写入设备网关；
- 写入子网掩码；
- 写入设备端角度输出；
- 控制马达启停；
- 自行逆向厂家软件通信命令。

第一版只在软件侧控制：

- 型号配置；
- 数据接收；
- 点云过滤；
- 有效范围；
- 坐标标定；
- 聚类；
- 目标跟踪；
- Unity 交互。

---

# 25. Codex 最终交付清单

Codex 完成后必须提供：

```text
[ ] 完整源码
[ ] RadarControl.sln
[ ] RadarBridge.exe 发布目录
[ ] Unity UPM Package
[ ] Unity 示例场景
[ ] 默认 F10 配置
[ ] F20 切换配置
[ ] 单元测试
[ ] 测试报告
[ ] 构建脚本
[ ] 使用说明
[ ] 标定说明
[ ] 网络配置说明
[ ] 故障排查文档
[ ] 版本说明
[ ] 已知限制
```

最终报告必须说明：

1. 创建了哪些文件；
2. 修改了哪些文件；
3. F10 默认逻辑位于何处；
4. F20 切换逻辑位于何处；
5. 协议测试结果；
6. Unity UI 和射线测试结果；
7. 尚未完成或受厂家协议限制的部分；
8. 如何构建；
9. 如何启动；
10. 如何进行现场标定。

---

# 26. Codex 开始执行时使用的首条提示词

将本文件放入项目根目录后，可向 Codex 发送：

```text
请完整阅读项目根目录中的《RadarControl_Codex_Execution_Spec.md》。

这是本项目的唯一主需求文档。请先检查当前仓库、Unity 版本、已有程序集和目录结构，然后生成实施计划。

注意：
1. 默认雷达型号必须是 F10。
2. 控制程序必须能够切换 F10/F20。
3. 不得将型号参数写死在业务代码中，必须通过 IRadarModelProfile 管理。
4. Unity 不直接连接雷达，由独立 RadarBridge 程序统一处理。
5. 必须先完成协议和型号配置测试，再开发界面。
6. 不得猜测说明书未提供的设备写入命令。
7. 每完成一个阶段都执行测试并报告结果。
8. 发现当前项目结构与文档建议不一致时，应遵循现有项目规范，但不得改变架构职责边界。
9. 未经确认不要删除或重构现有业务代码。
10. 先输出项目分析和分阶段实施计划，不要立即一次性生成全部代码。
```

---

# 27. 参考资料

开发时应同时参考项目提供的：

```text
F10、F20说明书V9.1.1.pdf
```

协议实现以该说明书为准。若实机数据与说明书不一致：

1. 保存原始字节；
2. 记录设备型号和固件信息；
3. 用厂家监控软件交叉验证；
4. 不要直接修改解析公式掩盖问题；
5. 输出差异报告后再决定兼容策略。
