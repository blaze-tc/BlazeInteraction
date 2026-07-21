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
