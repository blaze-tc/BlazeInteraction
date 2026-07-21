# Task 5 report — VRCave multi-screen RadarBridgeCoordinator

## Delivered

- Replaced the single-screen `RadarBridgeRuntime` with `RadarBridgeCoordinator` and moved the bridge version constant to `BridgeVersion.cs` (`1.1.5`).
- Added awaited IPC v2 Hello authentication. The server rejects invalid screen topology before invoking the callback; the Coordinator reconciles the accepted Unity topology and returns the complete effective ordered screen summary in `HelloAck`.
- Created one fusion engine per associated screen and one factory-created sensor pipeline per enabled sensor. Pipeline faults are logged as `[screen/sensor]` and do not stop sibling sensors or screens.
- Added fixed-rate per-screen scheduling with a global monotonic PointerBatch sequence. Every sent batch includes all currently negotiated screens, including empty frames.
- Added topology/remap transition handling: active pointers are reset to `Up`, followed by an explicit empty old-screen frame before the old runtime is removed; old runtime event handlers are detached during disposal.
- Removed the temporary .NET legacy Hello width/height and HelloAck constructors, removed the obsolete runtime and its tests, and changed WPF DI to inject the Coordinator/factory while retaining obsolete runtime API adapters for Task 6.

## TDD record

### RED

The following commands were run after adding the tests and before production implementation:

```powershell
dotnet test tests\Radar.Ipc.Tests\Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~RadarPipeServerTests"
dotnet test tests\Radar.Bridge.Wpf.Tests\Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarBridgeCoordinatorTests"
```

They failed for the expected missing capabilities: `RadarPipeServerOptions.AuthenticateHelloAsync` did not exist, and `RadarBridgeCoordinator` did not exist.

### GREEN / REFACTOR

```powershell
dotnet test tests\Radar.Ipc.Tests\Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~RadarPipeServerTests"
dotnet test tests\Radar.Bridge.Wpf.Tests\Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarBridgeCoordinatorTests" --no-restore
dotnet test tests\Radar.Bridge.Wpf.Tests\Radar.Bridge.Wpf.Tests.csproj -c Release --no-restore
dotnet test RadarControl.sln -c Release --no-restore
dotnet build RadarControl.sln -c Release --no-restore
dotnet build RadarControl.sln -c Release --no-restore -t:Rebuild
```

Results: IPC focused 8/8; Coordinator focused 5/5; Bridge tests 39/39; solution tests all passed (17 Unity compatibility, 17 Protocol, 18 IPC, 15 Device, 52 Processing, 35 Configuration, 39 Bridge); Release build completed with 0 errors.

The forced rebuild reports 249 warnings. These are the planned Task 2/Task 6 compatibility warnings from retained obsolete flat `RadarAppConfiguration` adapters and the temporary old ViewModel command adapters, plus existing nullable warnings in `RadarSensorPipeline`; no Task 5 error was reported.

## Review remediation

- Raised `BridgeVersion` to `1.2.0`; the coordinator's real pipe handshake test now asserts that the production `HelloAck` carries this value.
- The pipe server now keeps a candidate pipe private until it has read and validated `Hello`, successfully written `HelloAck`, and then marks it active. New tests cover a pre-Hello candidate, an invalid Hello, and the post-Ack activation point.
- Transition output is now a two-stage, complete-batch protocol: the old screen contributes `Up` to a complete batch, then an empty frame to a second complete batch. Fusion reset and runtime disposal/replacement happen only after the empty batch has actually been sent. The test-only clock treats its returned batch as delivered so its assertions remain deterministic.
- Fusion gained a non-mutating pressed-pointer snapshot API, enabling the coordinator to defer reset until transition delivery. Detection publication, fusion ticking, and reset all use the per-screen gate; active runtime enumeration uses a volatile immutable snapshot rather than the mutable screen dictionary.
- Pointer sends use a 250 ms linked cancellation timeout. A cancelled send disposes the active pipe, reports a client error, and lets the scheduler continue rather than waiting forever on a slow peer.

Remediation RED/GREEN evidence:

```powershell
dotnet test tests\Radar.Ipc.Tests\Radar.Ipc.Tests.csproj -c Release --filter "FullyQualifiedName~RadarPipeServerTests" --no-restore
dotnet test tests\Radar.Processing.Tests\Radar.Processing.Tests.csproj -c Release --filter "FullyQualifiedName~SnapshotPressedPointers" --no-restore
dotnet test tests\Radar.Bridge.Wpf.Tests\Radar.Bridge.Wpf.Tests.csproj -c Release --filter "FullyQualifiedName~RadarBridgeCoordinatorTests" --no-restore
```

Results: the two new IPC authentication tests first failed by receiving `PointerBatch` before `HelloAck`, then passed (11/11 IPC). The fusion snapshot test first failed to compile because the API did not exist, then passed. Coordinator focused tests passed 6/6 after the staged transition rewrite.

## Final acceptance (2026-07-21)

All requested Release/no-restore acceptance commands passed without retries for a failure:

```powershell
dotnet test tests\Radar.Ipc.Tests\Radar.Ipc.Tests.csproj -c Release --no-restore
dotnet test tests\Radar.Processing.Tests\Radar.Processing.Tests.csproj -c Release --no-restore
dotnet test tests\Radar.Bridge.Wpf.Tests\Radar.Bridge.Wpf.Tests.csproj -c Release --no-restore
dotnet test RadarControl.sln -c Release --no-restore # run 1
dotnet test RadarControl.sln -c Release --no-restore # run 2
dotnet build RadarControl.sln -c Release --no-restore --nologo
dotnet build RadarControl.sln -c Release --no-restore --nologo -t:Rebuild
```

Exact results: IPC 21/21; Processing 53/53; Bridge WPF 40/40. Each full-solution run passed 198/198: Unity Compatibility 17, Protocol 17, IPC 21, Device 15, Processing 53, Configuration 35, Bridge WPF 40. The incremental build reported 0 warnings and 0 errors; forced `Rebuild` reported 247 warnings and 0 errors. `git status --porcelain=v1` was clean before this report append.
