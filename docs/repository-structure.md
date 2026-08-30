# 仓库结构与文件所有权

```text
BlazeInteraction/
├─ src/                         Interaction Core、IPC、Bridge、既有 Radar 实现
├─ providers/
│  ├─ Radar/                    RadarControl → Provider API 1 适配
│  └─ CameraVision/             摄像头、手部推理、配置和 WPF 控制台
├─ native/                      CameraVision 原生手部推理桥接源码
├─ eng/                         第三方模型/原生资产来源与哈希
├─ UnityPackage/
│  └─ com.blaze.interaction/    唯一可发布 UPM 包
├─ tests/                       .NET 单元、集成、发布和兼容测试
├─ scripts/                     构建、发布、嵌入、Unity 和 smoke 脚本
├─ config/                      Radar 默认配置
├─ docs/                        当前文档、设计记录和验证报告
└─ artifacts/ tmp/ bin/ obj/    生成物，不提交
```

## 关键入口

| 需求 | 首要文件/目录 |
| --- | --- |
| 修改统一点位合同 | `src/Blaze.Interaction.Contracts/` |
| 修改 Provider 接口 | `src/Blaze.Interaction.Provider.Abstractions/` |
| 修改插件加载/切换 | `src/Blaze.Interaction.Runtime/` |
| 修改 Bridge 握手/转发/选择页 | `src/Blaze.Interaction.Bridge.Wpf/` |
| 修改 Radar 算法/设备 | `src/Radar.*` 与 `src/Radar.Bridge.Wpf/` |
| 修改 Radar Provider | `providers/Radar/Blaze.Provider.Radar/` |
| 修改 Camera 捕获/推理/UI | `providers/CameraVision/Blaze.Provider.CameraVision/` |
| 修改 MediaPipe 原生边界 | `native/Blaze.HandTracking.Native/` |
| 修改 Unity Runtime | `UnityPackage/com.blaze.interaction/Runtime/` |
| 修改 Unity 设置/构建复制 | `UnityPackage/com.blaze.interaction/Editor/` |
| 修改示例 | `UnityPackage/com.blaze.interaction/Samples~/` |

## 源文件与生成物

- `providers/**/provider.json`、`package.json`、C#/C++/XAML 和脚本是源文件。
- `UnityPackage/com.blaze.interaction/Bridge~/win-x64/` 是提交到包内的发布快照，但必须由 `scripts/publish-interaction-bridge.ps1 -EmbedUnityPackage` 整体重建，禁止手工替换单个 DLL/EXE。
- `artifacts/`、`tmp/`、`bin/`、`obj/` 是可删除重建的本地输出，并已由 `.gitignore` 排除。
- `eng/mediapipe-hand.json` 固定 Camera 模型和原生 DLL 的来源、ABI 与 SHA-256；更新第三方资产时必须同步来源记录与验证测试。
- 根目录的开发规格与 `docs/superpowers/` 是历史决策记录，不是运行时依赖。

## 仓库清洁原则

Release 只提交源码、当前文档、测试和完整的 UPM 内嵌 payload。不提交用户 Unity 项目的 `Library/BlazeInteraction/`、现场配置、日志、个人截图、临时压缩包或本地绝对路径。
