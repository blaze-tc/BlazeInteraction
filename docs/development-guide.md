# 代码维护与扩展指南

本文给后续人工开发者提供“从需求找到代码、先写什么测试、怎样重新发布”的最短路径。开始修改前先读 [architecture.md](architecture.md) 和对应模块测试。

## 1. 不可破坏的边界

1. 生产环境只有一个 `BlazeInteractionBridge.exe`、一个 Interaction IPC server 和一个 Unity Pipe Client。
2. Radar/CameraVision 都是 Provider；Provider 不创建第二条 Unity IPC。
3. `InteractionPoint.PixelPosition` 始终是通用中心点；`Fp` 始终是与该 point 同坐标空间的明细点列表。
4. Provider 切换、停止、断线和拓扑改变必须结束活动点，不能在 Unity 留下粘住的 Pointer。
5. WPF/捕获/推理线程不得直接调用 Unity API；Unity 数据只在 `InteractionManager.Tick()` 主线程分发。
6. 配置必须使用 Bridge 提供的项目级 `DataRoot`，不得退回全局固定目录。

## 2. 常见修改路径

### 增加 `InteractionPoint` 字段

按以下顺序修改：

1. `src/Blaze.Interaction.Contracts/InteractionContracts.cs`。
2. `src/Blaze.Interaction.Ipc/` codec/验证。
3. `UnityPackage/com.blaze.interaction/Runtime/Contracts/` 的 Unity 镜像模型。
4. `InteractionFrameDispatcher` 的深拷贝和主线程边界。
5. Radar/Camera Provider 映射。
6. Contracts、IPC、Unity compatibility、Provider 与 E2E 测试。
7. `docs/protocol.md` 和示例。

新增可选字段并保持旧 JSON 缺失时有安全默认值，通常可保留 IPC major 1；改变现有字段语义或生命周期则必须设计新协议版本。

### 修改 Radar 行为

先判断第一处错误数据：

```text
原始 TCP/Replay
→ Protocol frame
→ 物理变换/过滤
→ 聚类 Detection
→ 同屏 Fusion/Tracking
→ Radar Provider InteractionFrame
→ Unity
```

设备/算法修改放在 `src/Radar.*`，Provider 只做统一合同适配。任何 Radar 修改至少运行全部 Radar 测试与 Interaction 发布/Unity compatibility 测试。

### 修改 CameraVision 行为

```text
OpenCv capture
→ NativeHandLibrary / MediaPipe
→ HandDetectionResult
→ calibration / region / flip / smoothing
→ InteractionPoint(center + 21 Fp)
→ preview snapshot + IPC frame
```

- 捕获能力枚举：`Camera/CameraCapabilityEnumerator.cs`。
- 原始摄像头：`Camera/OpenCvCameraCaptureBackend.cs`。
- 原生推理边界：`Hand/NativeHandLibrary.cs` 与 `native/Blaze.HandTracking.Native/`。
- 配置 Schema：`Configuration/CameraVisionConfiguration.cs`。
- 坐标和 frame：`CameraVisionProvider.cs`。
- UI/ViewModel：`UI/CameraVisionSettings*.cs`、`CameraImageSurface.cs`、`CameraPointSurface.cs`。

Native `process frame` 错误必须在帧边界转换为可诊断状态，并允许后续帧/重连恢复。不要让 Dispose 再次弹出同一个旧推理错误。

### 增加新 Provider

参照 [provider-development.md](provider-development.md)：建立独立目录、manifest、插件类、Provider 实例、配置和测试。优先通过 `InteractionExtensions` 携带设备特有数据；只有所有 Provider 都需要且 Unity 通用消费者必须理解时，才扩展核心合同。

### 修改 Unity API/示例

- Runtime 公共 API 位于 `UnityPackage/com.blaze.interaction/Runtime/`。
- Editor 设置和构建复制位于 `Editor/`。
- Samples 必须保持 Provider-neutral，不直接引用 Radar/CameraVision 程序集。
- `InteractionManager` 后台读取只写入有界缓冲，Unity 对象访问留在主线程。
- 新公开能力至少增加一个 Runtime/EditMode 测试和可见 Sample 演示。

## 3. 配置兼容

配置记录必须包含 `schemaVersion`。新增字段时：

- 给旧配置明确默认值；
- Load 后归一化并验证，再替换当前不可变快照；
- 使用临时文件 + 原子替换保存；
- 切换设备时按 device index 读取对应 Camera profile；
- 不在 UI 文本变化的每个按键同步重启昂贵捕获/推理资源。

破坏性 Schema 变更需要迁移测试和用户文档，不能默默重置现场参数。

## 4. 性能约定

- 高频预览使用自绘控件和不可变快照，不为每个点创建 WPF `Ellipse`。
- UI 最多保留有限点数，后台只保留最新视觉帧；Down/Up/Cancel 走可靠 FIFO。
- Camera 捕获/推理/预览节奏分离；下拉框编辑只更新草稿，点击应用后才重建管线。
- ViewModel 变更事件合并调度，避免滚动/拖框时重复全量验证和序列化。
- 所有 async lifecycle 都要可取消、幂等，并在 Dispose 前等待后台任务结束。

## 5. 测试矩阵

| 修改范围 | 最低验证 |
| --- | --- |
| Contracts / IPC | `Blaze.Interaction.Contracts.Tests`、`Blaze.Interaction.Ipc.Tests`、Unity compatibility |
| Runtime / Provider lifecycle | Runtime tests、对应 Provider tests、Bridge tests |
| Radar | 所有 `Radar.*.Tests`、Radar Provider/UI、E2E、发布布局 |
| CameraVision | CameraVision tests、Bridge tests、发布布局、native verification |
| WPF UI/性能 | ViewModel/UI tests、稳定性测试、真实窗口拖动/滚动/resize 冒烟 |
| Unity Runtime/Editor | `.NET` compatibility mirror + Unity EditMode/PlayMode + Samples |
| 发布内容 | 全解决方案、publish、embedded smoke、archive inspection |

常用命令：

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo

powershell -ExecutionPolicy Bypass -File scripts/verify-hand-native.ps1

powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 `
  -Runtime win-x64 -EmbedUnityPackage

powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1

powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 `
  -UnityEditor "D:\Developer\2021.3.45f1\Editor\Unity.exe" `
  -TestPlatform All -IncludeSamples
```

## 6. 发布版本清单

发布版本不是只改 `package.json`。至少同步：

- `UnityPackage/com.blaze.interaction/package.json`；
- `Runtime/InteractionSdkVersion.cs`；
- Bridge 与两个 Provider 的 `.csproj`；
- 两个 `provider.json`；
- Bridge HelloAck 版本；
- publish/smoke 脚本和版本身份测试；
- README、INSTALL、版本文档和 Release Notes；
- 重新生成完整 `Bridge~/win-x64`。

然后：全量测试 → Unity 测试 → pack `.tgz` → 检查归档 → merge `main` → tag → GitHub Release → 回下载核对 SHA-256。

## 7. 代码评审重点

- 是否引入第二个进程/管道/Unity Manager。
- 是否在 Provider 切换或错误时遗漏 Cancel/Stop/Dispose。
- 是否把 Provider 特有概念泄漏到通用 Sample/API。
- 是否让高频点云/骨骼点产生无界集合或大量 UI 对象。
- 是否将配置写到全局目录导致项目互相覆盖。
- 是否只更新了内嵌 EXE 而没有重建完整 payload。
- 是否有绝对路径、个人配置、日志、密钥或未核验第三方二进制进入提交。
