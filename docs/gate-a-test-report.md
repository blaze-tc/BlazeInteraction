# Gate A 测试报告

日期：2026-08-21  
分支：`codex/blaze-interaction-gate-a`  
Unity：2021.3.45f1  
范围：RadarControl → Interaction Core → Radar Provider → Interaction IPC → `com.blaze.interaction`

## 结论

Gate A 自动化门禁通过。没有 CameraHand 生产实现。Radar 初始导入基线为 398/398；随着卸载、序列化和发布可靠性回归补强，当前官方 Radar 套件为 407/407，全部通过。

此结论只覆盖软件自动化和 Simulation IPC 冒烟，不替代真实 F10/F20、投影机、网卡与长稳现场验收。

## .NET 全解决方案

命令：

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
```

结果：647 passed，0 failed，0 skipped。

| 测试项目 | 通过 |
| --- | ---: |
| Blaze.Interaction.Contracts.Tests | 39 |
| Blaze.Interaction.Runtime.Tests | 72 |
| Blaze.Interaction.Ipc.Tests | 59 |
| Blaze.Interaction.Bridge.Wpf.Tests | 35 |
| Blaze.Interaction.Release.Tests | 1 |
| Blaze.Provider.Radar.Tests | 34 |
| Radar.Protocol.Tests | 17 |
| Radar.Device.Tests | 16 |
| Radar.Ipc.Tests | 24 |
| Radar.Processing.Tests | 61 |
| Radar.Configuration.Tests | 51 |
| Radar.Bridge.Wpf.Tests | 138 |
| Radar.Unity.Compatibility.Tests | 97 |
| Radar.EndToEnd.Tests | 3 |

## 官方 Radar 回归

命令：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

结果：407 passed，0 failed，0 skipped。

| Radar 分组 | 通过 |
| --- | ---: |
| Protocol | 17 |
| Device | 16 |
| IPC | 24 |
| Processing | 61 |
| Configuration | 51 |
| Radar Bridge WPF | 138 |
| Unity/Release Compatibility | 97 |
| End-to-End | 3 |

Radar 回归覆盖 F10/F20 协议、设备生命周期、Schema 1/2 迁移、标定/过滤/融合/跟踪、旧桥接、Provider publish/unload、兼容源码、发布脚本和端到端行为。Gate A 没有改写这些算法。

## Unity

在已启动的 `E:\UnityProject\BlazeInteraction-Test` 中以 UnitySkills 1.8.4 驱动 Unity 2021.3.45f1：

| 门禁 | 结果 |
| --- | --- |
| Editor tests | 5/5 passed |
| 真实 PlayMode tests | 44/44 passed；0 skipped/inconclusive；0.8616631 s |
| Basic Interaction Sample | 编译通过，Console 0 Error |
| Multi-Surface Routing Sample | 编译通过，Console 0 Error |
| Runner self-test | 2/2 passed；两个 Sample asmdef 均被验证 |

UnitySkills 的普通 PlayMode job record 会在 Domain Reload 后丢失。为避免错误地把 EditMode 过滤结果当作 PlayMode，本轮使用临时 `ITestRunCallback` 将根测试树结果持久化；结果写入后删除回调及 `.meta`，再独立运行 Editor tests。最终 Unity Console 为 0 Error。

PlayMode 首轮为 43/44，唯一失败是迁移后的 Camera Router test 漏掉 `Physics.SyncTransforms()`；对照旧 Radar 测试确认是刚移动 Collider 的测试场景未同步，而非运行时射线逻辑。先保留红测试，再补回物理同步，完整 44 项通过。

## Bridge、发布与 IPC 冒烟

Bridge 完整测试：35/35。新增并验证：

- Down/Up/Cancel 可靠有序；Hover/Move latest-only；
- Unity stall 不丢生命周期边；
- 断线/未确认 session 不把不可投递生命周期帧加入旧可靠链；
- Provider Cancel 先于 ProviderChanged；
- 父进程结束、手工模式与 topology 重连。

发布命令重新生成并嵌入最终 payload。结果：

- 根目录恰好 1 个 `BlazeInteractionBridge.exe`；
- Radar Provider 位于 `Providers/Radar/`，根目录无第二个 Provider EXE；
- `provider.json` 为 `blaze.radar.f10f20` / Provider API 1 / version 1.0.0；
- Release layout 1/1 passed；
- WPF 顶层窗口存在；
- Interaction IPC 1 Hello/HelloAck 返回 Bridge 1.0.0；
- 使用 Simulation；
- Unity parent 结束后 Bridge exit code 0；
- `BlazeInteractionBridge.exe` SHA-256：`ACA5585BC69307A7915457576DAF70BAB2596847E0A6A5D4E75C0F2FF18092C4`。

## 静态门禁

- `UnityPackage/` 中只有一个 `package.json`：`com.blaze.interaction`。
- 旧 `UnityPackage/com.blaze.radar` 不存在。
- 包内递归 `.exe` 数量为 1。
- 生产路径 CameraHand 命中为 0。
- Compatibility 没有 Pipe Client 或第二 Launcher/InputModule 实现。
- `git diff --check` 通过。
- 用户提供的 ZIP/DOCX 未修改、未提交。

## 仍需现场完成

- 真实 F10/F20 逐台连接、断网和恢复；
- LEFT/L1、FRONT/F1+F2、RIGHT/R1 的物理 OutputRect 与 overlap；
- 三投影 Camera/Display/RenderTexture 对齐；
- click/drag/resize/minimize/focus；
- 目标机器长稳运行、日志和配置归档。
