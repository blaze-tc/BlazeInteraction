# Provider API 1 开发指南

Provider 是加载到 `BlazeInteractionBridge.exe` 的外置设备适配插件。Provider 负责把设备/算法输出转换为统一 `InteractionFrame`；它不创建 Named Pipe、不依赖 Unity，也不复制 Interaction Core。

## 目录布局

```text
Providers/<ProviderName>/
  provider.json
  My.Provider.dll
  My.Provider.deps.json        # 如需要
  private dependencies...
  profiles/...                # 可选
  native/<rid>/...            # 可选原生库
```

`provider.json` 示例：

```json
{
  "id": "example.sensor",
  "displayName": "Example Sensor",
  "version": "1.0.0",
  "providerApiVersion": 1,
  "entryAssembly": "Example.Provider.dll",
  "entryType": "Example.Provider.ExamplePlugin",
  "category": "Sensor",
  "capabilities": ["interaction-point", "multi-surface"]
}
```

Manifest 必须小于等于 64 KiB，字段名不能重复；ID 在 Providers root 内大小写不敏感唯一。入口必须是 Provider 目录内的简单 DLL 文件名。目录、manifest、入口、依赖或 native probe 使用 reparse/symlink 逃逸时会被拒绝。

## 实现接口

插件类实现 `IInteractionProviderPlugin`：

- `Descriptor` 必须与 manifest 的 ID、名称、版本、类别和 capabilities 一致；
- `CreateProvider` 每次返回新的 `IInteractionProvider` 实例；
- 无设置 UI 时 `SettingsViewFactory` 返回 null。

实例实现 `IInteractionProvider`：

- `ProviderInstanceId` 在 Bridge 实例内稳定且非空；
- `InitializeAsync` 接收本次 Unity Hello 的不可变 Surface 快照和 Host services；
- `StartAsync` 开始采集/计算；
- `StopAsync` 停止新输出并等待后台任务；
- `DisposeAsync` 释放所有托管/原生资源，允许 Host 回收 AssemblyLoadContext；
- `FrameReceived` 发布统一帧；`StatusChanged` 发布明确状态转换/错误。

生命周期方法必须尊重传入 CancellationToken，Stop/Dispose 应幂等并能被并发调用者共同等待。事件订阅者异常不能破坏 Provider 的采集线程。

## Frame 契约

一个 `InteractionFrame` 只能属于一个 Provider instance 和一个 Surface。同一 Provider/Surface 的 `Sequence` 严格递增。Point 要求：

- `SurfaceId` 与 frame 一致并存在于初始化 topology；
- `ProviderId`/`ProviderInstanceId` 与实例一致；
- `SourceId` 标识真实设备或融合输出，不能伪造不存在的单传感器来源；
- normalized 坐标有限且在 `[0,1]`；pixel 坐标有限并处于该 Surface 的逻辑像素语义；
- confidence 在 `[0,1]`；phase 只能是 Hover/Down/Move/Up/Cancel。

Provider 停止、切换或失去已激活 point 时必须允许 Host 生成 Cancel。不要把多个 Surface 合成一帧，也不要把一个设备帧无理由拆成每 point 一帧。

仅 Provider 特有的附加数据放入 `InteractionExtensions`。核心消费者忽略扩展后仍必须正确处理基础 point。

## 加载与依赖

Host 为每个 Provider 创建独立 collectible `AssemblyLoadContext`，并用 `AssemblyDependencyResolver` 解析私有依赖。`Blaze.Interaction.Contracts` 与 `Blaze.Interaction.Provider.Abstractions` 必须使用 Host 默认上下文的精确 identity；不要把不兼容副本私有加载。

避免会把 collectible 类型永久放入进程全局缓存的反射序列化元数据。Radar Provider 使用 source-generated JSON metadata 的原因就是保证 Stop/Dispose 后 ALC 可回收。

## 测试顺序

每个 Provider 阶段都按 RED → GREEN → 完整相关回归：

1. manifest/catalog/loader：非法路径、重复 ID、descriptor mismatch、依赖隔离、native probe；
2. lifecycle：Initialize/Start/Stop/Dispose 顺序、取消、并发、异常聚合、回调重入；
3. frames：Surface、sequence、phase、identity、坐标与来源语义；
4. unload：持有 `WeakReference` 验证 plugin/provider/load context 均可回收；
5. publish：Provider 目录不得含第二个 EXE，入口 DLL/manifest/profile 必须完整；
6. Bridge/IPC/Unity 回归。

Radar Provider 是参考实现：`providers/Radar/Blaze.Provider.Radar/`。
