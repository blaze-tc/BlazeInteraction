# Blaze Interaction SDK 1.1.1 安装与升级

本文面向需要在另一台 Windows 电脑上直接导入 Unity 的使用者。推荐下载 GitHub Release 中的 `.tgz`，它已经包含 Windows x64 自包含 Bridge、Radar Provider、CameraVision Provider、手部模型和原生运行库。

## 1. 环境要求

- Windows 10/11 x64。
- Unity 2021.3 LTS 或更高。
- 摄像头模式需要 Windows 可访问的摄像头；Radar 真机模式需要 F10/F20 与正确配置的有线网卡。
- 使用 Release `.tgz` 不需要单独安装 .NET Runtime。

## 2. 推荐：从 Release 本地文件导入

1. 打开 [BlazeInteraction Releases](https://github.com/blaze-tc/BlazeInteraction/releases)。
2. 下载 `com.blaze.interaction-1.1.1.tgz`，不要解压。
3. Unity 打开 **Window > Package Manager**。
4. 点击左上角 `+`，选择 **Add package from tarball...**。
5. 选择下载的 `.tgz`。
6. Package Manager 应显示 **Blaze Interaction SDK 1.1.1**。

`.tgz` 可以复制到离线电脑再导入。Unity 会把包缓存到当前项目，不依赖原下载路径长期存在。

## 3. 其他安装方式

### 固定 Git 标签

Package Manager 选择 **Add package from git URL...**：

```text
https://github.com/blaze-tc/BlazeInteraction.git?path=/UnityPackage/com.blaze.interaction#v1.1.1
```

### 本地源码

Package Manager 选择 **Add package from disk...**，选择：

```text
<仓库>\UnityPackage\com.blaze.interaction\package.json
```

本地源码适合开发，不适合在另一台电脑部署，因为包引用依赖该绝对路径。

## 4. 创建 Unity Runtime

1. 执行 **Tools > Blaze Interaction > Create or Select Settings**。
2. 在 **Project Settings > Blaze Interaction** 中至少保留一个启用 Surface。
3. `Surface ID` 和 `Order` 必须唯一；逻辑宽高必须大于 0；所有启用 Surface 中必须恰好一个 Primary。
4. 执行 **GameObject > Blaze Interaction > Create Runtime**。
5. 场景中只保留一个启用的 EventSystem/Interaction Input Module。

`Logical Width/Height` 是 Provider 与 Unity 共用的输出像素空间。CameraVision 右侧的 Unity 输出分辨率来自当前 Surface，是只读显示；X/Y 翻转在四点标定之后、映射到该分辨率时应用。

## 5. 导入并运行示例

1. Package Manager 选中 Blaze Interaction SDK。
2. 在 **Samples** 中导入 **Basic Interaction**。
3. 打开导入的 `BasicInteraction.unity`。
4. 进入 Play Mode。
5. 首次运行在设备选择页选择 **Radar** 或 **CameraVision**。

Basic Interaction 会显示 IPC/Provider 状态、中心点和 `Fp` 粒子：Radar 模拟模式下可看到环绕中心点的真实扫描点模拟；CameraVision 下中心点跟随手掌，`Fp` 对应 21 个骨骼点。

## 6. Radar 首次配置

1. 选择 Radar 后，在控制台确认顶部 **Unity 连接** 为 True。
2. 无真机时点 **一键模拟**，先验证完整链路。
3. 真机时选择雷达型号、IP/端口和本机网卡，连接后检查设备状态。
4. 在原始点/过滤预览中设置有效区域、边缘过滤、屏蔽区、旋转/翻转和四点标定。
5. 选择输出方式：中心点、边缘点或中心点 + 边缘点；`Fp` 始终保存实际扫描点集合供 Unity 自由使用。
6. 保存并应用配置。

## 7. CameraVision 首次配置

1. 选择 CameraVision，选择摄像头、分辨率和帧率。
2. 左上真实画面保持摄像机比例，叠加手部骨骼点和四角标定区域；拖动四角控制点定义有效范围。
3. 左下预览显示最终发送到 Unity 的中心点和 21 个骨骼点。
4. 根据项目坐标约定设置 X/Y 翻转、检测/跟踪置信度与 EMA 平滑。
5. 点击 **应用设置**。

检测手数量不在 SDK 层设置固定上限；实际数量与相机画面、模型能力、CPU/GPU 性能有关。系统不区分左右手，只输出稳定的“手”点位和骨骼数据。

## 8. 配置存储与项目隔离

Editor：

```text
<当前 Unity 项目>\Library\BlazeInteraction\
```

Windows Player：

```text
<Application.persistentDataPath>\BlazeInteraction\
```

其中包含 `bridge-settings.json` 和 `Providers/<provider-id>/` 下的配置。Pipe 名也根据该数据目录生成唯一后缀，所以多个 Unity 项目不会共享选择模式、参数或 IPC 会话。

不要把 `Library/BlazeInteraction` 提交到 Git；需要复制现场配置时，应在退出 Play Mode/Player 后按项目单独备份该目录。

## 9. 构建 Windows Player

Unity Build Processor 会把完整 Bridge 复制到 Player 输出旁的 `BlazeInteractionBridge/`。发布时必须整体保留：

```text
MyGame.exe
BlazeInteractionBridge/
  BlazeInteractionBridge.exe
  bridge-version.txt
  Providers/Radar/...
  Providers/CameraVision/...
```

不要只复制 EXE。构建器会验证 SDK/Bridge/Provider 版本和必需文件，发现混装版本时直接中止构建。

## 10. 升级或卸载

升级前退出 Play Mode 并关闭当前项目启动的 Bridge。

- `.tgz` 安装：在 Package Manager 移除旧包，再从新的 tarball 导入。
- Git 安装：把 URL 的 tag 改为新版本。
- 本地源码：切换仓库版本并让 Unity 重新解析。

若已经把 Sample 导入 `Assets/Samples/`，升级不会自动替换该副本。先备份自定义内容，再删除旧 Sample 并重新 Import。

不要同时安装 `com.blaze.radar` 和 `com.blaze.interaction`，也不要在 `Assets/` 保留旧 Runtime/asmdef 副本。

## 11. 安装验收

- [ ] Package Manager 显示 `1.1.1`。
- [ ] Project Settings Surface 校验通过。
- [ ] Play Mode 自动打开 Blaze Interaction Bridge。
- [ ] 顶部 Unity 连接为 True，当前 Provider 与选择模式一致。
- [ ] Radar Simulation 或 CameraVision 能在 Basic Interaction 中产生中心点和 `Fp` 粒子。
- [ ] 停止 Play Mode 后没有粘住的 Pointer。
- [ ] Windows Player 输出包含完整 `BlazeInteractionBridge/`。
- [ ] 外部电脑上不安装 .NET Runtime也能启动 Bridge。

遇到问题见 [故障排查](docs/troubleshooting.md)。
