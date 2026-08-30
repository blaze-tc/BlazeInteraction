# Blaze Interaction - Codex 执行入口

> 先阅读 `BlazeInteraction_统一感应设备平台_开发规格.md`，再开始任何代码修改。

## 目标

将现有 `blaze-tc/RadarControl` 演化为统一多感应设备平台：

- 唯一 Windows EXE：`BlazeInteractionBridge.exe`
- 唯一 Unity Package：`com.blaze.interaction`
- 外部 Provider DLL 插件：Radar、CameraHand、未来设备
- Unity 统一数据：`InteractionPoint + Extensions`
- V1 单 Provider 激活，架构预留未来多 Provider
- 保留现有 RadarControl 全部关键能力并提供 Radar API 兼容层

## 第一阶段只做 Gate A

不要立刻开发摄像头 AI。

1. 运行并记录现有 RadarControl 完整测试基线。
2. 阅读现有 Radar Contracts / Processing / IPC / Bridge / Unity Package。
3. 新建 `Blaze.Interaction.Contracts`。
4. 新建 `Blaze.Interaction.Provider.Abstractions`。
5. 实现 ProviderCatalog / ProviderLoader / ProviderManager。
6. 使用独立 AssemblyLoadContext 加载外部 Provider DLL。
7. 把现有 Radar pipeline 包装成 Radar Provider；不要重写 F10/F20 协议、Fusion、Tracking、Calibration。
8. 建立 Interaction IPC。
9. 新建 `com.blaze.interaction` 最小 Runtime。
10. 让现有 Radar Sample 通过新 Interaction 架构运行。
11. 建立 Radar Compatibility 骨架。
12. 完成回归测试后再进入 CameraHand。

## 硬性禁止

- 不允许第二个 EXE。
- 不允许 `ProviderHost.exe`。
- 不允许 `HandTrackingWorker.exe`。
- 不允许 Python Worker。
- 不允许 Unity 直接访问雷达或摄像头。
- 不允许 Bridge Core 依赖 Radar/CameraHand 具体类型。
- 不允许 CameraHand 开发先于 Radar Gate A 回归通过。

## 开发方法

每个 Task：

```text
Write failing test
→ Run and confirm failure
→ Minimal implementation
→ Run targeted tests
→ Run related full tests
→ Commit
```

第一阶段完成标准：

```text
Radar Hardware / Simulation
        ↓
Radar Provider DLL
        ↓
InteractionFrame
        ↓
BlazeInteractionBridge.exe
        ↓
Interaction IPC
        ↓
com.blaze.interaction
        ↓
Unity Sample
```

这条链路稳定后，再开发 CameraHand Provider。
