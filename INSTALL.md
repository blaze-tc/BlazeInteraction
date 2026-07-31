# RadarControl 1.2.9 安装、升级与现场验收

本文面向 Unity 开发者和现场操作员。Blaze Radar SDK 1.2.9 使用 IPC 2，包内提供 Windows x64 的完整 self-contained Bridge，不需要另装 .NET Runtime。

需要按界面位置查看按钮和全部参数时，参阅 [RadarControl 图文使用说明](docs/user-guide.md)。

## 1. 环境

- Windows 10/11 x64；Unity 2021.3 LTS 或更高；Git 可从命令行使用。
- 每组真实 F10/F20 使用可配置静态 IPv4 的有线网卡。常见雷达端点为 `192.168.0.100:8487`，电脑可用 `192.168.0.10/24`，两者不能相同。
- 发布或移动 Player 时保留整个 `RadarBridge/` 目录。只复制 `RadarBridge.exe` 会丢失 .NET/WPF 运行时和 Profiles。

## 2. 从 1.1.x 升级并确认解析来源

1. 退出 Play Mode，关闭由当前项目启动的 RadarBridge。
2. 在 Package Manager 移除旧 Blaze Radar SDK；从 `Packages/manifest.json` 删除所有指向 `#v1.1.x` 的 RadarControl Git URL。不要同时保留旧 URL、本地包和新 URL。
3. 若 1.1.x Sample 已复制到 `Assets/Samples/Blaze Radar SDK/`，先备份自定义内容，再删除旧 Sample；UPM 移包不会删除已导入到 Assets 的副本。
4. Package Manager 选择 **Add package from git URL**，粘贴：

   ```text
   https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.9
   ```

5. 选择 Blaze Radar SDK，确认 Version 为 `1.2.9`。查看详情中的 Resolved Path：Git 包应来自本项目新解析的 `Library/PackageCache/com.blaze.radar@...`，不能指向旧缓存或意外的本地克隆。`Packages/packages-lock.json` 中也应只有 `com.blaze.radar` 的当前 Git dependency/revision。
6. 若仍解析旧内容，关闭 Unity，只删除该项目 `Library/PackageCache` 下对应的 `com.blaze.radar@...` 缓存和 `packages-lock.json` 中该包条目，再重开 Unity 让 Package Manager 从上述标签重新解析。不要删除共享仓库或整个用户目录。
7. 重新导入 **Basic Interaction** 与 **Multi-Screen Camera Routing** Samples。

也可在 `Packages/manifest.json` 的 `dependencies` 中使用同一 URL。离线开发可克隆仓库并 checkout `v1.2.9`，然后 **Add package from disk** 选择 `UnityPackage/com.blaze.radar/package.json`；此时 Resolved Path 应明确指向该克隆目录。

## 3. 配置任意逻辑屏幕

1. 打开 **Tools > Blaze Radar > Create or Select Settings**，再进入 **Project Settings > Blaze Radar**。
2. 为每块逻辑墙新增一个 Screen。启用的每屏必须有稳定、唯一且上线后不随意更改的 Screen ID，例如 `LEFT`、`FRONT`、`RIGHT`。
3. 设置 Unity Display Name、Order 和逻辑分辨率。逻辑分辨率是雷达坐标与 Camera 路由的共同像素空间，不要求每屏宽高或比例相同。
4. 所有启用屏幕中必须恰好一个 Primary。禁用屏幕不参与握手、融合或 PointerBatch。
5. 保存后先看拓扑校验；重复/空 Screen ID、非法分辨率、Order 冲突或非唯一 Primary 必须在运行/构建前修复。

建议 VRCave 示例：

| Screen ID | 投影 | 雷达 | 说明 |
| --- | --- | --- | --- |
| `LEFT` | 左墙 | `L1` | 一个雷达覆盖左墙 |
| `FRONT` | 正墙，Primary | `F1`、`F2` | 两个 OutputRect 保留物理交叠区，同屏融合去重 |
| `RIGHT` | 右墙 | `R1` | 一个雷达覆盖右墙 |

不要把 F1/F2 拆成两个 Unity 屏幕；它们属于同一个 `FRONT`，融合和 Pointer ID 稳定性以屏幕为边界。1.2.9 不提供跨屏人员身份延续。

## 4. Bridge 操作员配置

1. 运行 Play Mode 或 `RadarBridge.exe --profile <schema2.json>`，完成 IPC 2 Hello/HelloAck 后，Bridge 左侧“Unity 屏幕”列出当前启用屏幕。
2. 先选择屏幕，再用 `＋` 新增雷达或“删除雷达”移除所选雷达。Sensor ID 在投入使用后保持稳定，日志和回放记录都依赖它。
3. 每个传感器分别设置：Enabled、F10/F20、Real/Simulation/Replay、雷达 IP/端口、本机网卡 IP、自动重连；旋转/翻转/偏移、量程/角度、有效区、四边边线死区/屏蔽区、四点标定和 `OutputRectPixels`。三面墙现场先让有效区覆盖完整墙面，再用左/右/下 `0.05–0.15 m` 起步的边线死区覆盖橙色静态边线带。
4. F1/F2 的 OutputRect 应各自对应 FRONT 的逻辑像素区域，并在真实覆盖交叠处有合理重叠。错误的矩形或四角会产生跳点、空区或重复目标。
5. 每屏设置输出频率、数据最大年龄和跨雷达融合距离；再设置确认帧、丢失帧、最大关联距离、平滑系数、Touch/Dwell 交互参数。这些参数只影响所选屏幕。快速挥动不连续时，先完成边线过滤，再载入“快速移动预设”并保存；输出频率不会提高雷达真实扫描频率。
6. 分别连接所选雷达、整屏或全部；断开/重连一个传感器时，其他屏幕应继续输出。只有操作员预期 Pointer Up/reset 时才改分辨率、OutputRect 或拓扑。

## 5. 场景和 Camera 绑定

执行 **GameObject > Blaze Radar > Create Runtime**。场景中只保留一个启用的 EventSystem 输入模块；Canvas 配 `GraphicRaycaster`，3D/2D Camera 分别配 `PhysicsRaycaster`/`Physics2DRaycaster`。

每个 Screen ID 可绑定一种独立输出目标：

- **Display**：每个 Camera 使用不同 `targetDisplay`，Player 启动时激活对应 Display；确认 Windows 显示排列与投影布线一致。
- **pixelRect**：多屏共享一个 Display 时，为每屏指定不重叠或刻意布局的 Camera 像素矩形，并与该屏逻辑分辨率/输出区域一致。
- **RenderTexture**：把 Camera 输出绑定到独立 RenderTexture，再由场景材质/投影映射使用；确认纹理尺寸和纵横比匹配逻辑屏幕。

不要让两个启用 Camera 同时消费同一 Screen ID，除非确实希望镜像。修改路由后检查 UI、2D/3D 射线和 per-camera world particles 都只出现在目标墙。

## 6. 两条 Sample 测试路径

### Basic Interaction

1. 导入并打开 Sample。先用 Bridge Simulation 验证 IPC、Button、Toggle、Slider、ScrollRect、2D/3D 目标和 Pointer Down/Move/Up/Click/Drag。
2. 再连接一台真实雷达重复操作。调试鼠标时临时用 `RadarAndMouseDebug`，正式部署改回 `RadarOnly`。
3. 保存右侧事件日志与同时间段 `Player.log`。`IPC CONNECTED` 且 `POINTERS 0` 表示链路活着但当前无有效目标，不等于卡死。

### Multi-Screen Camera Routing

1. 先选 **LOCAL**：无需 Bridge，逐屏注入本地指针，检查 LEFT/FRONT/RIGHT 的 Display、Camera `pixelRect`、RenderTexture、UI 和 world particles。
2. 再选 **BRIDGE IPC**：启动 1.2.9 Bridge，让 Project Settings 拓扑完成 Hello/HelloAck，逐屏/逐雷达模拟或真机输入，确认 screenId、逻辑/像素坐标和 Camera 命中一致。
3. FRONT 同时运行 F1/F2，反复走过交叠区；只能看到一个稳定 Pointer，不能产生双击。

## 7. 日志对时

Bridge 日志位于 `%LOCALAPPDATA%/RadarControl/logs/`，消息包含 `[SCREEN/SENSOR]`（如 `[FRONT/F1]`）或 `[GLOBAL/IPC]` 标签。Unity `Player.log`/Editor Log 应记录 SDK version、Bridge version、IPC protocol、screenId、batch/frame sequence、pointer count、dropped count、timestamp/latency 和 EventSystem target。

排障时先记本机时间和操作，再按 screenId、sensorId、sequence 与 timestamp 对齐两侧日志。不要只提交截图；同时归档 Schema 2 Profile、Bridge 日志和 `Player.log`。

## 8. Windows Player 与 Bridge 身份验证

构建处理器从 Package Manager 当前 Resolved Path 读取 `Bridge~/win-x64`，删除 Player 旁旧 `RadarBridge/`，复制完整目录，并检查 package `1.2.9`、SDK `1.2.9`、`bridge-version.txt`、必需文件和 EXE SHA-256。任一不一致应使 Build 失败。

构建后在发布机记录：

```powershell
Get-Content .\RadarBridge\bridge-version.txt
Get-FileHash .\RadarBridge\RadarBridge.exe -Algorithm SHA256
Get-ChildItem .\RadarBridge -File -Recurse | Measure-Object
```

版本必须是 `1.2.9`，SHA 必须等于已审核包内 `Bridge~/win-x64/RadarBridge.exe`。若不一致，先确认 Package Manager Resolved Path，再按第 2 节清理该项目的陈旧包缓存并重新 Build；不要手工用另一台机器的 EXE 覆盖。

## 9. IPC v1/v2 不兼容与投影显示检查

IPC 1 客户端不能消费 IPC 2 PointerBatch，IPC 2 客户端也拒绝旧 PointerFrame。看到 protocol mismatch、旧 Bridge version 或 HelloAck 失败时：退出 Play Mode，关闭旧 Bridge，移除旧 URL/缓存，重新安装 `v1.2.9`，确认 package/SDK/Bridge 都是 `1.2.9` 且日志显示 IPC 2，再启动。不要通过修改 Pipe 名或忽略 Error 绕过主版本检查。

RadarBridge 在窗口创建前强制 WPF 软件渲染，不依赖现场 GPU 驱动的脏区刷新。投影电脑仍必须实际检查：逐个点击/拖动控件、滚动参数、调整窗口大小、最小化/恢复、跨不同 DPI 显示器移动并切换投影焦点。文字/点云应清晰，任何控件都不能点击后消失或变糊；若出现问题，记录 Windows 缩放、投影分辨率、GPU/驱动、窗口操作和日志时间点。

## 10. 现场 8 小时验收（三投影、四雷达）

- [ ] 固定最终 Schema 2 配置：LEFT/L1、FRONT/F1+F2 overlap、RIGHT/R1；记录每屏逻辑分辨率、每雷达端点/OutputRect 和每屏融合/跟踪参数。
- [ ] 用最终 Windows Player 和包内 Bridge 连续运行 8 小时；每小时记录 CPU、内存、画面、IPC 状态、序号/丢帧和四雷达连接状态。
- [ ] 反复穿越 FRONT F1/F2 重叠区，确认只有一个 Pointer、ID 在 FRONT 内稳定且无重复 Down/Click。
- [ ] 逐台断开/重连 L1、F1、F2、R1，再逐个禁用/恢复对应 NIC；无关屏幕持续更新，恢复后该屏不遗留粘住指针。
- [ ] 只在明确预期 reset 时修改逻辑分辨率/OutputRect；确认 Unity 收到新像素，旧 Pointer 先 Up/取消。
- [ ] 在三墙逐一操作 Button、Toggle、Slider、Scroll、2D/3D target 和 per-camera world particles；确认 Camera 路由无串屏。
- [ ] 点击、拖动、滚动、resize、minimize/restore、投影 focus change 后 Bridge 控件不消失、不模糊。
- [ ] 制造并恢复一次 IPC 断线；确认 v2 HelloAck、版本和 screen summaries 正常，旧 v1 客户端被明确拒绝。
- [ ] 归档最终 Profile、Bridge tagged logs、`Player.log`、Package Manager Resolved Path、Player/包内 RadarBridge.exe SHA-256、文件数和现场记录。

自动化完成不等于现场通过；只有以上每项有记录并由现场负责人签字后才完成 release acceptance。
