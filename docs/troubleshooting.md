# BlazeInteraction 1.1.1 故障排查

## 先收集这些信息

- Package Manager 中的版本和 Resolved Path。
- Bridge 顶部 Unity/设备连接状态、活动 Provider。
- Unity Console/Editor Log 或 Player.log。
- 当前项目 `Library/BlazeInteraction`（Player 为 persistent data）中的配置备份。
- 复现时间、摄像头/雷达型号、分辨率、Windows 缩放和操作步骤。

## Unity 连接一直 False

1. 确认处于 Play Mode，场景中存在启用的 `InteractionBridgeLauncher`。
2. 确认 Project Settings 的 Surface 校验通过。
3. 场景只能有一个 Runtime/EventSystem；移除旧 Radar Runtime/Package。
4. 确认 Package、`InteractionSdkVersion` 和 `bridge-version.txt` 都是 `1.1.1`，IPC 是 Interaction IPC 1。
5. 关闭残留 Bridge 后重进 Play Mode。不同 Unity 项目使用不同带哈希后缀的 Pipe，不能拿另一个项目启动的 Bridge 状态判断当前项目。

## 重新运行后参数丢失

Editor 配置应在 `<项目>/Library/BlazeInteraction/`，Player 配置应在 `Application.persistentDataPath/BlazeInteraction/`。检查目录是否可写、是否每次删除 Library/persistent data、是否用绝对 `ProfilePath` 覆盖了默认路径。

点击 Provider 页面中的保存/应用按钮后再退出。Camera 每个 device index 有独立 profile；切换到另一摄像头时看到默认值不等于原设备配置丢失。

## Camera 已连接但检测点为 0

- 左上原始画面是否有实时帧、比例是否正常。
- 手是否位于四角有效区域内，光照、距离、遮挡是否满足检测。
- 检测/跟踪置信度是否过高。
- 左下 Unity 输出分辨率是否来自预期 Surface。
- Output FPS 为 0 且错误区有 native 错误时，点击重连；保留完整错误和相机模式。

`Invalid crop coordinates` 已在帧边界做防护；如果 1.1.1 仍复现，记录分辨率、翻转、四角坐标和出错前操作，不要只截异常弹窗。

## Camera 参数操作卡顿

分辨率/帧率通过下拉框选择并在应用时重建管线。连续拖四角只更新预览草稿，不应反复重启摄像头。若仍卡顿，记录 Camera/Inference/Output FPS、UI 已渲染/替换帧计数、CPU 和 Windows DPI，并确认不是缓存旧版。

## Radar 连接失败或无原始点

检查雷达 IP/端口、本机网卡 IP、型号和数据源。电脑与雷达需同网段且 IP 不相同。先用 Simulation 验证 Unity 链路，再在原始点区验证真机 TCP/解析。

原始点有、过滤点无：检查距离/角度、变换、有效区域、边缘死区和屏蔽区。过滤点有、Unity 无：检查标定、OutputRect、融合/跟踪、Surface 和 IPC 状态。

## Radar 放大编辑/点数多时卡顿

1.1.1 使用有界保留点数、合并 UI 快照和自绘画布。若仍复现，记录窗口大小、DPI、点数、操作顺序和内存/CPU；确认未使用旧 `RadarBridge.exe` 或旧 PackageCache。不要通过无限增加显示点上限处理设备噪声。

## 点位方向/位置错误

Radar：按原始点 → 变换/有效区 → 标定 → OutputRect → Unity 顺序定位。

Camera：左上四角区域定义透视换算，X/Y 翻转在标定后应用，左下是最终 Unity 预览。若左下正确但 Unity 错，检查 Surface logical resolution 和 Camera Router；若左下已经错误，修改 Camera 配置而非业务 Camera。

## Bridge/Provider 版本不匹配

移除旧包和旧 Sample，关闭 Unity，只清理当前项目中该包的 PackageCache/lock 解析，再安装固定 `v1.1.1` 或 Release `.tgz`。不要手工把新 EXE 覆盖到旧 payload；重新安装完整包。

## Player 换机启动失败

确认 Player 旁有完整 `BlazeInteractionBridge/`，包括 hostfxr、hostpolicy、两个 Providers、Camera 模型和原生 DLL。Windows x64 Release 自包含，不需安装 .NET；缺文件时重新复制整个构建输出。

## 日志与隐私

提交问题时可附配置和日志，但先删除客户名称、IP、目录用户名等敏感信息。Camera 截图可能包含人物/环境，只有在获得授权后提供，优先截错误状态和控制面板。
