# RadarControl 图文使用说明

本文说明 Blaze Radar SDK 与 `RadarBridge.exe` 的安装、连接、界面按钮、参数含义，以及 Unity Sample 的验证方法。适用版本：`1.2.3`，IPC 协议：`2`。

## 1. 安装到 Unity

1. 打开 **Window > Package Manager**。
2. 点击左上角 `＋`，选择 **Add package from git URL**。
3. 输入：

   ```text
   https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.3
   ```

4. 选中 **Blaze Radar SDK**，确认版本为 `1.2.3`。
5. 在 **Samples** 中导入 **Basic Interaction**；多屏项目再导入 **Multi-Screen Camera Routing**。
6. 打开 **Tools > Blaze Radar > Create or Select Settings**，再到 **Project Settings > Blaze Radar** 配置 Unity 逻辑屏幕。

升级时必须先移除旧 Git URL，并删除 `Assets/Samples/Blaze Radar SDK/` 中已导入的旧 Sample 副本，否则相同 asmdef 会导致重复程序集错误。不要只复制 `RadarBridge.exe`；必须保留包内 `Bridge~/win-x64` 的完整目录。

## 2. Bridge 主界面

![RadarBridge 主界面](images/radarbridge-overview.png)

> 图中是现场操作布局截图。`1.2.3` 保持左右栏位置不变，并把中间“区域 1”拆为 `1A 原始点观察` 与 `1B 拉框过滤结果`；两块数据不再叠加显示。

界面从左到右分为五个工作区：Unity 屏幕、当前屏幕雷达、单雷达数据、屏幕融合输出、参数。底部日志用于把 Bridge 状态与 Unity `Player.log` 按时间对齐。

### 2.1 Unity 屏幕

屏幕列表来自 Unity 启动时的 IPC Hello。每项显示 Screen ID/名称、有效分辨率和在线雷达数。

| 按钮 | 作用 |
| --- | --- |
| 全部连接 | 启动所有启用屏幕下的启用雷达 |
| 全部断开 | 停止所有雷达输入，但不关闭 Unity IPC |
| 连接屏幕 | 只连接当前所选屏幕的全部启用雷达 |
| 断开屏幕 | 只断开当前所选屏幕的雷达 |

### 2.2 当前屏幕雷达

先选屏幕，再在该屏幕下管理雷达。一个屏幕可以有多个雷达；同屏雷达的有效目标会在区域 2 融合后再发送给 Unity。

| 按钮 | 作用 |
| --- | --- |
| `＋` | 为当前屏幕新建一个雷达配置 |
| 连接所选 / 断开所选 | 只控制当前雷达 |
| 启动全部模拟 | 将当前屏幕全部雷达以 Simulation 数据启动，适合无真机验收 |
| 删除雷达 | 删除当前雷达配置；现场已使用的 Sensor ID 不应随意重建 |

### 2.3 区域 1：单雷达数据

- **1A 原始点观察**：显示设备上报的原始物理点。它不应用旋转、X/Y 翻转、偏移、距离/角度、拉框或屏蔽区，仅用于判断雷达是否收到数据及原始扫描方向是否正常。
- **1B 拉框过滤结果**：显示完成旋转、翻转、偏移、距离/角度、有效区域与屏蔽区过滤后的点。绿色四角可以拖动；这里看到的点才会进入聚类、标定、OutputRect 和屏幕融合链路。
- 两个视图使用同一个“显示最大范围”，只改变观察比例，不改变过滤或发给 Unity 的数据。

### 2.4 区域 2：屏幕融合输出

区域 2 使用屏幕逻辑像素坐标显示：

- 每个雷达的 `OutputRectPixels`；
- 各雷达映射后的检测目标；
- 同屏多雷达去重后的融合目标；
- 最终发送给 Unity 的 Pointer。

若 1B 有过滤点而区域 2 为 0，依次检查聚类最少点数、最大宽度、标定、OutputRect、数据最大年龄和确认帧。若区域 2 有 Pointer 而 Unity 无反应，检查 Unity IPC 状态、Screen ID、`RadarInputModule` 的 Input Mode 与 EventSystem Raycaster。

## 3. 屏幕参数

| 参数 | 含义与建议 |
| --- | --- |
| 宽度 / 高度 | 当前屏幕逻辑像素分辨率；决定传给 Unity 的 `pixelX/pixelY` 范围 |
| 恢复 Unity 默认分辨率 | 使用 Project Settings 中该 Screen 的默认宽高 |
| 输出频率 (Hz) | 该屏向 Unity 发布融合帧的频率，通常 30 Hz |
| 数据最大年龄 (ms) | 超过此时间的雷达检测不参与本帧融合 |
| 跨雷达融合距离 (px) | 同屏不同雷达目标小于该像素距离时合并 |
| 确认帧 | 连续命中多少帧后建立稳定 Pointer |
| 丢失帧 | 连续丢失多少帧后释放 Pointer |
| 最大关联距离 (px) | 相邻帧目标允许关联到同一 Pointer 的最大位移 |
| 平滑系数 | 0–1；越大越跟手，越小越平滑 |
| 交互模式 | Touch 使用 Down/Move/Up；Dwell 按停留条件触发 |
| 停留时长 (ms) | Dwell 模式需要保持的时间 |

逻辑分辨率改变后，区域 2、Unity 委托和 Camera 路由会使用新像素范围。多雷达覆盖同一正面墙时，它们必须属于同一个 Screen ID，而不是拆成多个 Unity 屏幕。

## 4. 雷达参数

### 4.1 数据源与连接

| 参数 | 含义 |
| --- | --- |
| 启用此雷达 | 关闭后不参与连接、融合或输出 |
| 型号 | F10 或 F20；影响量程、角分辨率、盲区和默认频率 |
| 数据源模式 | Real 真机、Simulation 内置模拟、Replay 录制回放 |
| 雷达 IP / 端口 | 真机网络端点，常用端口为 8487 |
| 本机网卡 IP | 与雷达同网段的有线网卡；留空时由系统选择 |
| 自动重连 | 真机断线后持续重连；现场通常保持勾选 |

### 4.2 物理范围与变换

| 参数 | 含义 |
| --- | --- |
| 最小/最大距离 | 过滤物理量程之外的点 |
| 显示最大范围 | 只调整区域 1 的缩放，不影响数据 |
| 旋转角度 | 将原始雷达坐标旋转到屏幕安装方向 |
| 翻转 X / 翻转 Y | 镜像坐标；勾选状态使用青色实心框和深色对勾 |
| X/Y 偏移 | 将雷达物理原点平移到统一墙面坐标 |

调整这些参数时应对比 1A 与 1B：1A 始终不变，1B 应按参数实时变化。

### 4.3 聚类与输出

| 参数 | 含义 |
| --- | --- |
| 基础间隙 / 距离缩放 | 相邻雷达点归为同一目标的距离阈值 |
| 最少点数 | 小于该点数的簇被当作噪声 |
| 最大宽度 | 过宽的点簇不作为单一目标 |
| OutputRect X/Y/宽/高 | 该雷达在当前屏幕逻辑像素中的负责区域；同屏双雷达可保留合理重叠用于融合 |

### 4.4 区域、标定、屏蔽和回放

| 按钮/参数 | 作用 |
| --- | --- |
| 重置区域 | 恢复有效区域四角 |
| 添加屏蔽 / 删除屏蔽 | 新建或删除不参与输出的遮挡区域 |
| 开始标定 | 开始采集四个物理角点 |
| 采集点 / 撤销 | 记录当前目标为标定角点或撤销上一步 |
| 保存 / 清除 | 保存四点 Homography 或清空标定 |
| 开始/停止录制 | 真机模式录制原始 TCP 字节 |
| 选择回放文件 | 选择 `.radarrec` 文件 |
| 回放速度 / 循环回放 | 设置 0.1x–8.0x 和是否循环 |
| 暂停 / 继续 / 单步 / 停止 | 控制 Replay |
| 保存并应用配置 | 校验配置，保存到用户配置文件并重建受影响运行链路 |

## 5. Basic Interaction：委托粒子验证

打开 `BasicInteraction.unity` 后，`Basic Interaction Presenter` 上已绑定 `RadarPointerParticleBinder`：

```csharp
radarDispatcher.ScreenPointerReceived += OnScreenPointerReceived;
```

脚本按 `screenId` 选择逻辑屏幕，将 Bridge 的逻辑像素按该屏幕分辨率换算到 Camera 像素矩形，再执行 `targetCamera.ScreenToWorldPoint`，在对应世界坐标发射 `Radar Pointer Particles`。`OnDisable` 会解除委托，避免重复订阅。

验证步骤：

1. 在 `EventSystem > RadarInputModule` 选择 `RadarOnly`。
2. 进入 Play Mode，确认右上角为 `IPC CONNECTED`。
3. 在 Bridge 将 `main` 下雷达设为 Simulation，或连接真实雷达。
4. 移动目标；场景中应出现青色粒子轨迹，右侧日志应出现 `PointDelegate`、屏幕 ID、Pointer ID、像素和世界坐标。
5. 移过 Button、Toggle、Slider、ScrollRect、2D 与 3D 目标，确认粒子位置和标准 EventSystem 回调位置一致。

业务项目可以直接复制 `RadarPointerParticleBinder.cs` 的订阅、屏幕过滤和 Camera 换算部分，再把粒子输出替换成自己的射线、特效或交互逻辑。

## 6. 日志与故障定位

Bridge 底部日志按 `[SCREEN/SENSOR]` 与 `[GLOBAL/IPC]` 标记；筛选框中 `*` 表示全部。重点字段包括 raw/valid、groups/pointers、batch、latency、CRC、丢帧和重连原因。

日志文件：

```text
Bridge: %LOCALAPPDATA%\RadarControl\logs\RadarBridge-YYYYMMDD.log
Unity Player: %USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\Player.log
```

排障时同时保存配置、Bridge 日志、`Player.log`、Package Manager 的 Version/Resolved Path、`bridge-version.txt` 和 EXE SHA-256。完整升级与现场检查见 [INSTALL.md](../INSTALL.md)，常见错误见 [troubleshooting.md](troubleshooting.md)。
