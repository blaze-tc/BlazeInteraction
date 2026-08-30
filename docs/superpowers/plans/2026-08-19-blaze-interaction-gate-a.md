# Blaze Interaction Gate A Implementation Plan

> **For Codex:** Execute this plan with `superpowers:subagent-driven-development`. Every production change must follow RED -> GREEN -> related full regression -> commit. Do not create CameraHand source, tests, manifests, packages, or placeholders.

**Goal:** Preserve RadarControl 1.2.10 behavior while routing the existing Radar pipeline through Interaction Core, an external Radar Provider, Interaction IPC, and the single Unity package `com.blaze.interaction`.

**Architecture:** The existing `Radar.*` protocol/device/processing/configuration assemblies remain the private Radar implementation. New provider-neutral contracts, loader/runtime, and IPC assemblies form the Bridge core. `Blaze.Provider.Radar.dll` adapts existing `PointerBatchPayload` output to `InteractionFrame`; it never opens the Unity pipe. `BlazeInteractionBridge.exe` is the only published executable and dynamically loads the Radar provider. Unity consumes only Interaction IPC; Radar compatibility types delegate to the new runtime.

**Tech Stack:** .NET 8, WPF, `AssemblyLoadContext`, `AssemblyDependencyResolver`, Named Pipes, `System.Text.Json`, xUnit, Unity 2021.3 LTS, Unity Test Framework 1.1.33.

## Global constraints

- Keep F10/F20 parsing, CRC, TCP reconnect, Simulation, Replay, Fusion, Tracking, Calibration, OutputRect, Touch, and Dwell algorithms unchanged.
- Core projects must not reference `Radar.*`; the Radar plugin may reference both Interaction abstractions and existing Radar assemblies.
- Runtime queues must be bounded/latest-value; async resources accept `CancellationToken` and dispose deterministically.
- V1 starts at most one provider, but all identities use `providerId`, `providerInstanceId`, `sourceId`, and `surfaceId`.
- Provider switching emits point cancellations before stopping the current provider and starting the next provider.
- Final publish contains one `BlazeInteractionBridge.exe`, one `com.blaze.interaction` package, and an external `Providers/Radar` directory.
- After every Radar-facing change, run the complete Radar .NET suite before proceeding.

## Task 0: Freeze and import RadarControl 1.2.10 baseline

**Files:**

- Modify: `README.md`
- Add: `BlazeInteraction_Codex执行入口.md`
- Add: `BlazeInteraction_统一感应设备平台_开发规格.md`
- Add: `docs/superpowers/plans/2026-08-19-blaze-interaction-gate-a.md`
- Add: `.superpowers/sdd/2026-08-19-blaze-interaction-gate-a/progress.md`

**Verification:**

1. Import `RadarControl/main@aeb9635` with history into the BlazeInteraction feature branch.
2. Run `dotnet clean RadarControl.sln -c Release`.
3. Run `powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release`; require 398/398.
4. Run `scripts/test-unity-package.ps1` with Unity 2021.3.45f1; record EditMode and PlayMode separately.
5. Commit `chore: import RadarControl 1.2.10 gate-a baseline`.

## Task 1: Define Interaction contracts and surface model

**Files:**

- Add: `src/Blaze.Interaction.Contracts/Blaze.Interaction.Contracts.csproj`
- Add: `src/Blaze.Interaction.Contracts/InteractionContracts.cs`
- Add: `src/Blaze.Interaction.Contracts/InteractionExtensions.cs`
- Add: `tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj`
- Add: `tests/Blaze.Interaction.Contracts.Tests/InteractionContractTests.cs`
- Modify: `BlazeInteraction.sln`

**RED tests:** Validate required provider/instance/source/surface identities, normalized and pixel positions, Hover/Down/Move/Up/Cancel phases, typed Radar extension helpers over raw JSON extensions, and deterministic JSON round trips.

**GREEN implementation:** Add immutable provider-neutral records/enums for `InteractionSurface`, `InteractionPoint`, `InteractionFrame`, `ProviderIdentity`, handedness, phases, and raw extensions. Do not reference Radar types.

**Verification:** Run the new contracts test project, then all Radar .NET tests. Commit `feat: add interaction contracts and surfaces`.

## Task 2: Define Provider API, catalog, and isolated loader

**Files:**

- Add: `src/Blaze.Interaction.Provider.Abstractions/Blaze.Interaction.Provider.Abstractions.csproj`
- Add: `src/Blaze.Interaction.Provider.Abstractions/ProviderContracts.cs`
- Add: `src/Blaze.Interaction.Runtime/Blaze.Interaction.Runtime.csproj`
- Add: `src/Blaze.Interaction.Runtime/ProviderManifest.cs`
- Add: `src/Blaze.Interaction.Runtime/ProviderCatalog.cs`
- Add: `src/Blaze.Interaction.Runtime/ProviderLoadContext.cs`
- Add: `src/Blaze.Interaction.Runtime/ProviderLoader.cs`
- Add: `tests/Blaze.Interaction.Runtime.Tests/Blaze.Interaction.Runtime.Tests.csproj`
- Add: `tests/Blaze.Interaction.Runtime.Tests/ProviderCatalogTests.cs`
- Add: `tests/Blaze.Interaction.Runtime.Tests/ProviderLoaderTests.cs`
- Add: `tests/TestProviders/ValidProvider/*`
- Add: `tests/TestProviders/DependencyV1/*`
- Add: `tests/TestProviders/DependencyV2/*`
- Modify: `BlazeInteraction.sln`

**RED tests:** Cover valid provider, missing/corrupt manifest, API mismatch, missing entry assembly/type, constructor/load error, one bad provider not blocking another, contract assemblies shared from default context, and two plugins resolving different versions of the same dependency.

**GREEN implementation:** Add the stable plugin/provider lifecycle interfaces, manifest validation, resilient catalog discovery, collectible per-provider `AssemblyLoadContext` with `AssemblyDependencyResolver`, shared contract assembly resolution, and provider-local managed/native dependency resolution.

**Verification:** Run Runtime tests, build test providers, then all Interaction and Radar .NET tests. Commit `feat: add provider catalog and isolated loader`.

## Task 3: Implement deterministic single-active ProviderManager

**Files:**

- Add: `src/Blaze.Interaction.Runtime/ProviderManager.cs`
- Add: `src/Blaze.Interaction.Runtime/ActivePointRegistry.cs`
- Add: `tests/Blaze.Interaction.Runtime.Tests/ProviderManagerTests.cs`

**RED tests:** Assert initialize/start/stop/dispose order, only one running provider, frame forwarding, future-safe instance identities, cancellation frames emitted before stop, reverse switching, failed initialization cleanup, and cancellation-token propagation.

**GREEN implementation:** Manage loaded provider instances as a collection with one active instance, track active points per provider-instance/surface, synthesize Cancel points during switch/stop, and expose provider/status/frame events without device-specific fields.

**Verification:** Run Runtime tests, then all Interaction and Radar .NET tests. Commit `feat: manage provider lifecycle and switching`.

## Task 4: Implement Interaction IPC protocol 1

**Files:**

- Add: `src/Blaze.Interaction.Ipc/Blaze.Interaction.Ipc.csproj`
- Add: `src/Blaze.Interaction.Ipc/InteractionIpcProtocol.cs`
- Add: `src/Blaze.Interaction.Ipc/InteractionMessages.cs`
- Add: `src/Blaze.Interaction.Ipc/InteractionFrameCodec.cs`
- Add: `src/Blaze.Interaction.Ipc/InteractionPipeServer.cs`
- Add: `tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj`
- Add: `tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs`
- Add: `tests/Blaze.Interaction.Ipc.Tests/InteractionPipeServerTests.cs`
- Modify: `BlazeInteraction.sln`

**RED tests:** Cover 4-byte little-endian UTF-8 framing, partial reads, invalid/oversized frames, protocol-1 Hello/HelloAck, surface topology, active provider/capabilities, InteractionFrame, Status, ProviderChanged, Ping/Pong, Shutdown/Error, reconnect, and bounded latest-frame delivery.

**GREEN implementation:** Reuse the proven Radar framing approach under provider-neutral models and pipe name `Blaze.InteractionBridge`; never reference `Radar.Contracts` or `Radar.Ipc`.

**Verification:** Run IPC tests, then all Interaction and Radar .NET tests. Commit `feat: add interaction ipc protocol`.

## Task 5: Wrap the existing Radar pipeline as an external provider

**Files:**

- Modify: `src/Radar.Bridge.Wpf/Services/RadarBridgeCoordinator.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/RadarBridgeCoordinatorTests.cs`
- Add: `providers/Radar/Blaze.Provider.Radar/Blaze.Provider.Radar.csproj`
- Add: `providers/Radar/Blaze.Provider.Radar/RadarPlugin.cs`
- Add: `providers/Radar/Blaze.Provider.Radar/RadarInteractionProvider.cs`
- Add: `providers/Radar/Blaze.Provider.Radar/RadarFrameAdapter.cs`
- Add: `providers/Radar/Blaze.Provider.Radar/provider.json`
- Add: `tests/Blaze.Provider.Radar.Tests/Blaze.Provider.Radar.Tests.csproj`
- Add: `tests/Blaze.Provider.Radar.Tests/RadarFrameAdapterTests.cs`
- Add: `tests/Blaze.Provider.Radar.Tests/RadarInteractionProviderTests.cs`
- Modify: `BlazeInteraction.sln`

**RED tests:** First prove coordinator provider mode does not start legacy Radar IPC. Then cover PointerBatch-to-InteractionFrame mapping, screen-to-surface mapping, provider/source identities, phases and Radar extensions, topology initialization, Simulation/Replay lifecycle, provider stop/dispose, and manifest/loader integration.

**GREEN implementation:** Add only an optional legacy-IPC switch/output seam to the coordinator. Implement the plugin/adapter around the existing coordinator and `PointerBatchPayload`; do not duplicate or edit Radar algorithms.

**Verification:** Run Radar Provider tests; run all 398+ Radar .NET tests; run old Unity package EditMode/PlayMode. Any Radar failure stops work. Commit `feat: adapt radar pipeline as interaction provider`.

## Task 6: Add the single BlazeInteractionBridge composition root and publish layout

**Files:**

- Add: `src/Blaze.Interaction.Bridge.Wpf/Blaze.Interaction.Bridge.Wpf.csproj`
- Add: `src/Blaze.Interaction.Bridge.Wpf/App.xaml`
- Add: `src/Blaze.Interaction.Bridge.Wpf/App.xaml.cs`
- Add: `src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs`
- Add: `src/Blaze.Interaction.Bridge.Wpf/BridgeCommandLine.cs`
- Add: `tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj`
- Add: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs`
- Add: `scripts/publish-interaction-bridge.ps1`
- Add: `tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj`
- Add: `tests/Blaze.Interaction.Release.Tests/InteractionPublishLayoutTests.cs`
- Modify: `BlazeInteraction.sln`

**RED tests:** Cover provider scan failure isolation, Hello topology before Radar start, frame routing provider-to-pipe, point cancel before ProviderChanged, parent-PID lifecycle, manual mode persistence, provider manifest validation, and publish output containing exactly one executable plus `Providers/Radar`.

**GREEN implementation:** Compose Runtime, IPC, and dynamic providers without compile-time Radar references. Publish `BlazeInteractionBridge.exe`; provider assets remain external. Do not add a second host executable.

**Verification:** Run Bridge/Release tests, publish win-x64, smoke Hello/HelloAck/Simulation frames, then all Interaction and Radar .NET tests. Commit `feat: host providers in blaze interaction bridge`.

## Task 7: Create `com.blaze.interaction` contracts, pipe, dispatcher, and manager

**Files:**

- Add: `UnityPackage/com.blaze.interaction/package.json`
- Add: `UnityPackage/com.blaze.interaction/Runtime/Blaze.Interaction.Runtime.asmdef`
- Add: `UnityPackage/com.blaze.interaction/Runtime/Contracts/InteractionMessageModels.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/Internal/LengthPrefixedFrameDecoder.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/Internal/LatestValueBuffer.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionPipeClient.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionFrameDispatcher.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionManager.cs`
- Add: `UnityPackage/com.blaze.interaction/Tests/Runtime/*`
- Modify: `tests/Radar.Unity.Compatibility.Tests/PackageIdentityTests.cs`

**RED tests:** Require package identity 1.0.0/Unity 2021.3, Protocol 1 framing and handshake, bounded latest-frame behavior, stable point collection/events, ProviderChanged cancellation, reconnect reset, and APIs `Points`, `IsConnected`, `ActiveProvider`.

**GREEN implementation:** Port the verified Radar client/decoder/dispatcher mechanics to provider-neutral models and one Interaction connection. Do not reference provider DLLs.

**Verification:** Run source-level compatibility tests, then Unity EditMode tests through the live Unity 2021.3 test host. Commit `feat: add interaction unity runtime`.

## Task 8: Port EventSystem/camera routing, launcher, settings, build, samples, and Radar compatibility

**Files:**

- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionInputModule.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionCameraRouter.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionBridgeLauncher.cs`
- Add: `UnityPackage/com.blaze.interaction/Runtime/InteractionRuntimeSettings.cs`
- Add: `UnityPackage/com.blaze.interaction/Editor/*`
- Add: `UnityPackage/com.blaze.interaction/Compatibility/Radar/*`
- Add: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/*`
- Add: `UnityPackage/com.blaze.interaction/Samples~/MultiSurfaceRouting/*`
- Add: `UnityPackage/com.blaze.interaction/Bridge~/win-x64/*`
- Add: `UnityPackage/com.blaze.interaction/Tests/Editor/*`
- Add: `UnityPackage/com.blaze.interaction/Tests/Runtime/*`
- Modify: `scripts/test-unity-package.ps1`
- Modify: `scripts/bridge-release-functions.ps1`
- Delete after replacement is proven: `UnityPackage/com.blaze.radar/`

**RED tests:** Port UGUI/Physics2D/Physics3D pointer lifecycle tests, multi-surface Display/pixelRect/RenderTexture routing, launcher reuse/parent PID, build copy/delete/version/manifest/SHA-256, sample compilation, and compatibility wrappers. Assert compatibility has no Pipe Client or second Launcher implementation.

**GREEN implementation:** Rename/port the existing verified Unity implementation into Interaction runtime and make `Blaze.Radar` compatibility wrappers delegate to it. Embed the complete single-exe Bridge plus `Providers/Radar`. Keep no second EventSystem or IPC stack.

**Verification:** Run Unity EditMode and PlayMode tests, sample compilation, build-copy tests, old Radar source compatibility tests, then all .NET and Radar tests. Commit `feat: complete gate-a unity package and radar compatibility`.

## Task 9: Gate A documentation and final verification

**Files:**

- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/protocol.md`
- Modify: `docs/unity-integration.md`
- Add: `docs/provider-development.md`
- Add: `docs/radar-migration.md`
- Add: `docs/gate-a-test-report.md`
- Modify: `.superpowers/sdd/2026-08-19-blaze-interaction-gate-a/progress.md`

**Verification:**

1. Clean and run the complete `BlazeInteraction.sln` suite.
2. Run the full imported Radar suite; require no regression from 398/398.
3. Run Unity EditMode and PlayMode through Unity 2021.3.45f1 and verify both samples compile.
4. Publish and smoke `BlazeInteractionBridge.exe` with Simulation through Interaction IPC into the Unity sample.
5. Verify published file inventory has one `.exe`, the Radar provider is external, and `com.blaze.interaction` is the only shipped UPM package.
6. Search production paths for CameraHand artifacts; require none.
7. Complete task-spec review and final code-quality review; fix findings with tests first.
8. Commit `docs: record gate-a architecture and verification`.
