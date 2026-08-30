# CameraVision Gate B0 Implementation Plan

> Scope is limited to Gate B0 from `BlazeInteraction_CameraVision_GateB_Codex执行与验证规格.md`. Do not create B1-B6 production types.

**Goal:** Add one Provider API 1 CameraVision plugin that captures an RGB camera in-process, keeps only the latest frame, emits a predictable fake interaction point through the existing Bridge/IPC/Unity chain, and preserves all Radar behavior.

**Architecture:** Keep all camera-specific behavior inside `providers/CameraVision/Blaze.Provider.CameraVision`. Use injectable capture abstractions for deterministic tests and an OpenCvSharp Windows backend for production capture. Publish only standard `InteractionFrame` values; reuse the existing ProviderManager, Bridge, Interaction IPC 1 and Unity package.

**Tech stack:** .NET 8 Windows, OpenCvSharp4.Windows, xUnit, existing Unity Test Framework, PowerShell publish/smoke scripts.

---

## Task 0: Restore the Gate A baseline

**Files:**

- Modify: `src/Blaze.Interaction.Ipc/InteractionPipeServer.cs`
- Test: `tests/Blaze.Interaction.Ipc.Tests/InteractionPipeServerTests.cs`

1. Add a deterministic session-cancellation test whose transport writer completes with `ObjectDisposedException`.
2. Run it and confirm RED.
3. Normalize only cancellation-time disposed-transport errors; preserve program/serialization writer faults.
4. Run full IPC and Bridge tests.

## Task 1: Provider manifest, discovery and lifecycle

**Files:**

- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/provider.json`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionPlugin.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj`
- Test: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionProviderTests.cs`

1. Add failing manifest/discovery/descriptor and lifecycle tests, including 50 start/stop cycles and idempotent disposal.
2. Run and record RED.
3. Implement the smallest valid Provider API 1 plugin and lifecycle coordinator.
4. Run the CameraVision test project.

## Task 2: Camera capture, reconnect, transforms and latest-frame ownership

**Files:**

- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Camera/*`
- Test: `tests/Blaze.Provider.CameraVision.Tests/CameraCaptureTests.cs`
- Test: `tests/Blaze.Provider.CameraVision.Tests/CameraFrameTests.cs`
- Test: `tests/Blaze.Provider.CameraVision.Tests/LatestFrameSlotTests.cs`

1. Add failing tests for enumeration, requested width/height/FPS, close/release, unavailable device, disconnected/reconnecting/running transitions, Mirror X, all four rotations, capacity-one replacement/disposal and frame statistics.
2. Run and record RED.
3. Implement injectable capture interfaces, OpenCvSharp production backend, capture/reconnect service, `CameraFrame`, transforms, statistics and `LatestFrameSlot`.
4. Run the complete CameraVision test project.

## Task 3: Fake detector and standard Interaction publication

**Files:**

- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Detection/*`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Test: `tests/Blaze.Provider.CameraVision.Tests/FakeVisualDetectorTests.cs`
- Test: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionProviderTests.cs`
- Test: `tests/Blaze.Interaction.Ipc.Tests/InteractionProtocolTests.cs` or nearest existing protocol test

1. Add failing tests for deterministic PingPong output, Surface normalized/pixel conversion, monotonically increasing frames and consumption through unchanged IPC1.
2. Run and record RED.
3. Implement only `IVisualDetector`, `VisualDetection` and `FakeVisualDetector`; drive standard Hover points while B0 is active, without Hand/Color/Tracking/Homography.
4. Run CameraVision, IPC, Runtime and Bridge suites.

## Task 4: Unity sample and package verification

**Files:**

- Modify: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/BasicInteractionPresenter.cs`
- Test: `UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionFrameDispatcherTests.cs`
- Test: `UnityPackage/com.blaze.interaction/Tests/Editor/InteractionEditorTests.cs`

1. Add a failing provider-neutral fake-cursor presentation/dispatcher test using Provider ID `blaze.camera.vision` and a standard frame.
2. Run Unity tests and record RED.
3. Extend the existing BasicInteraction presenter to render a cursor from standard normalized/pixel data; do not add Camera networking or APIs.
4. Run Unity Editor + PlayMode tests with samples.

## Task 5: Publish, embedded Bridge and Gate report

**Files:**

- Modify: `scripts/publish-interaction-bridge.ps1`
- Modify: `scripts/test-embedded-bridge.ps1`
- Modify: `scripts/embedded-bridge-smoke-client.ps1` only if standard-provider assertion needs a parameter
- Modify: `tests/Blaze.Interaction.Release.Tests/InteractionPublishLayoutTests.cs`
- Create: `docs/camera-vision/gate-b0-report.md`

1. Add failing release assertions for both Provider manifests, CameraVision managed/native assets, exact one-EXE layout and a CameraVision fake-point IPC smoke selected through `--provider`.
2. Run and record RED.
3. Publish CameraVision beside Radar and embed the same validated payload; retain the existing Radar simulation smoke.
4. Run the Gate B0 project tests, `dotnet test BlazeInteraction.sln`, `scripts/test.ps1`, Unity All + Samples, publish and embedded Bridge smoke.
5. Record commands, exact results, limitations and `REQUIRES HARDWARE VALIDATION`. Add `GATE B0 PASSED` only if every automated acceptance check and Radar regression is green.
