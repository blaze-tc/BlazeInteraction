# Task 4 — Independent Radar Sensor Pipeline

## Delivered

- Added `IRadarSensorPipeline`, immutable per-sensor runtime snapshot, runtime state and source lifecycle API.
- Added `RadarSensorPipelineFactory` as the production construction entry point.
- Added `RadarSensorPipeline`; each instance owns its own bounded latest-frame channel, cancellation source, connection service, replay gate, recording stream/writer, processing loop, metrics and state.
- Reused the existing F10/F20 connection service, TCP recording format, replay decoder/frame builder, transforms, filters, sequential clustering, homography calibration and `RadarOutputMapper`.
- Sensor detections are tagged with the configured `SensorId`, use cluster index as `DetectionId`, and omit non-finite or out-of-screen mappings. Logs are tagged `[ScreenId/SensorId]`.
- Stop and disposal are idempotent and emit an empty sensor snapshot/detection frame so a later coordinator can release sensor data.

## TDD record

1. **RED** — added `TwoSimulationPipelines_RunAndStopIndependently` and `ProcessingChannel_DropsOldFramesInsteadOfAccumulatingLatency`; the focused test command failed to compile because `RadarSensorPipeline` did not exist.
2. **GREEN** — implemented the pipeline boundary, isolated simulation lifecycle and latest-frame channel; focused tests passed (2/2).
3. **RED** — added `StopReplayAsync_DoesNotStopASimulationPipeline`; it failed with `Expected: Running / Actual: Stopped`.
4. **GREEN/REFACTOR** — tracked the active source separately, so replay commands only affect replay; focused tests passed (3/3). The implementation then retained immutable processing snapshots and safe observer invocation while keeping tests green.

`PublishScan` is an internal production source callback used by real TCP, simulation and replay; the test assembly receives internals access rather than adding a test-only API.

## Verification

| Command | Result |
| --- | --- |
| `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~RadarSensorPipelineTests\|FullyQualifiedName~RadarReplayGateTests\|FullyQualifiedName~RadarBridgeRuntimeTests"` | Passed: 13 |
| `dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release --no-restore` | Passed: 33 |
| `dotnet test RadarControl.sln -c Release --no-restore` | Passed: 184 |
| `dotnet build RadarControl.sln -c Release --no-restore --nologo` | Passed: 0 warnings, 0 errors |

## Known limits / handoff

- Task 4 deliberately does not wire these pipelines into `RadarBridgeCoordinator`, screen fusion, WPF selection/UI or Unity; that belongs to later tasks.
- Existing source files still carry compatibility-obsolescence warnings during `dotnet test`; the final standalone Release build is warning-free.
