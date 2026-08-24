# CameraVision Unity and Release Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver typed 21-landmark hand data to Unity, demonstrate every tracked hand in BasicInteraction, package the native backend in the single-EXE Bridge payload, and complete automated and real USB-camera evidence.

**Architecture:** The existing InteractionPoint extension channel carries hand schema version 1 without any Camera branch in Unity core. A typed helper validates landmarks and a sample presenter pools joint/bone visuals. Release scripts validate and embed the CameraVision native/model assets while preserving the one-business-EXE rule.

**Tech Stack:** .NET 8, Unity 2021.3-compatible C#, Newtonsoft.Json, Unity UGUI, Unity Test Framework, PowerShell release scripts.

**Spec:** `docs/superpowers/specs/2026-08-24-camera-vision-hand-mvp-0-1-design.md`

## Global Constraints

- Requires all prior plan checkpoints.
- Unity Runtime core must not branch on CameraVision Provider ID.
- `PixelPosition`/`NormalizedPosition` remain the standard hand tracking point.
- `Fp` remains contour-only and empty for CameraVision MVP.
- `Extensions["hand"].landmarks` contains exactly 21 ordered landmarks.
- The Unity package remains compatible with Unity 2021.3.
- Final payload contains exactly one business executable.
- Do not mark the MVP passed before real USB Camera to Unity validation.

---

### Task 1: Extend the .NET typed hand contract

**Files:**
- Modify: `src/Blaze.Interaction.Contracts/InteractionExtensions.cs`
- Modify: `tests/Blaze.Interaction.Contracts.Tests/InteractionContractTests.cs`
- Modify: `tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs`

**Interfaces:**
- Produces: `HandLandmarkExtension` and schema-versioned `HandInteractionExtension` with `TrackingPoint` and 21 `Landmarks`.
- Preserves: raw unknown extension fields and Radar extension behavior.

- [ ] **Step 1: Write failing contract tests**

Construct schema version 1 with 21 entries and assert helper round trip preserves index, normalized/pixel positions, and Z. Add malformed tests for wrong schema, 20/22 landmarks, duplicate/out-of-order indices, non-finite Z, and missing positions. Assert raw extensions remain accessible after typed parse failure.

```csharp
[Fact]
public void HandTypedHelperReadsTwentyOneOrderedLandmarks()
{
    var point = PointWithHandExtension(Enumerable.Range(0, 21).Select(HandLandmark));
    Assert.True(point.TryGetHandExtension(out var hand));
    Assert.Equal(21, hand!.Landmarks.Count);
    Assert.Equal(8, hand.Landmarks[8].Index);
}
```

- [ ] **Step 2: Run Contracts tests and observe failure against the old handedness-only model**

```powershell
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release --filter Hand --nologo
```

- [ ] **Step 3: Implement immutable schema validation**

Remove handedness from the new schema contract. Snapshot all 21 entries. Validate schema 1, ascending indices 0 through 20, non-null positions, and finite Z. `TryGetHandExtension` catches JSON/argument failures and returns false.

- [ ] **Step 4: Add IPC round-trip coverage and run both projects**

```powershell
dotnet test tests/Blaze.Interaction.Contracts.Tests/Blaze.Interaction.Contracts.Tests.csproj -c Release --nologo
dotnet test tests/Blaze.Interaction.Ipc.Tests/Blaze.Interaction.Ipc.Tests.csproj -c Release --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add src/Blaze.Interaction.Contracts/InteractionExtensions.cs tests/Blaze.Interaction.Contracts.Tests/InteractionContractTests.cs tests/Blaze.Interaction.Ipc.Tests/InteractionFrameCodecTests.cs
git commit -m "feat(contracts): type hand landmark extensions"
```

### Task 2: Add Unity typed hand models and parsing

**Files:**
- Modify: `UnityPackage/com.blaze.interaction/Runtime/Contracts/InteractionMessageModels.cs`
- Create: `UnityPackage/com.blaze.interaction/Runtime/Contracts/HandInteractionModels.cs`
- Create: `UnityPackage/com.blaze.interaction/Tests/Runtime/HandInteractionExtensionTests.cs`
- Modify: `UnityPackage/com.blaze.interaction/Tests/Runtime/InteractionFrameDispatcherTests.cs`

**Interfaces:**
- Produces: `InteractionPoint.TryGetHandExtension(out HandInteractionExtension)` and fixed `HandSkeletonConnections.All`.

- [ ] **Step 1: Write failing Unity EditMode tests**

Assert valid 21-point JSON parses; indices/positions/Z survive; eight points in a frame each preserve independent hand extensions; malformed schema returns false; raw `JObject` remains; dispatcher update and cancel do not mutate landmark lists; fixed connections reference only 0 through 20.

- [ ] **Step 2: Run Unity EditMode tests and verify compile failure**

Use UnitySkills `unity-test` against the open test project, filtering the package Runtime tests. Expected: compile/test failure because the hand models do not exist.

- [ ] **Step 3: Implement Unity 2021.3-compatible serializable models**

Use explicit properties and `JsonProperty` attributes; avoid records, required members, or APIs unavailable to Unity's compiler. Validate after deserialization before returning true. Keep generic `TryGetExtension<T>` unchanged.

- [ ] **Step 4: Run Unity package Runtime tests and .NET compatibility tests**

```powershell
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --nologo
```

Then rerun the Unity EditMode filter. Expected: both PASS.

- [ ] **Step 5: Commit**

```powershell
git add UnityPackage/com.blaze.interaction/Runtime/Contracts UnityPackage/com.blaze.interaction/Tests/Runtime
git commit -m "feat(unity): read typed hand landmarks"
```

### Task 3: Add pooled multi-hand skeleton visuals

**Files:**
- Create: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/HandSkeletonPresenter.cs`
- Create: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/HandSkeletonVisualPool.cs`
- Modify: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/README.md`
- Modify through Unity Editor API: `UnityPackage/com.blaze.interaction/Samples~/BasicInteraction/BasicInteraction.unity`
- Create: `UnityPackage/com.blaze.interaction/Tests/Runtime/HandSkeletonGeometryTests.cs`
- Create: `UnityPackage/com.blaze.interaction/Runtime/HandSkeletonGeometry.cs`

**Interfaces:**
- Produces: pure surface-normalized joint/bone geometry used by the sample presenter.
- Consumes: standard point events and `TryGetHandExtension`; never checks Provider ID.

- [ ] **Step 1: Write failing geometry/cleanup tests**

Assert 21 joints and all fixed connections are generated; pixel and normalized consistency validation rejects malformed values; separate track IDs create separate geometry; cancel removes one track; disconnect clears all; Radar points without hand extension create no skeleton geometry.

- [ ] **Step 2: Run Unity EditMode tests and observe compile failure**

- [ ] **Step 3: Implement pure geometry and pooled UGUI presenter**

Pool joint `Image` objects and bone `Image`/`RectTransform` objects. Key visuals by ProviderInstanceId, SurfaceId, and point ID. Update on PointAdded and PointUpdated; remove on PointRemoved; clear on disconnect/disable. Assign color from a deterministic track-ID palette with no semantic meaning.

- [ ] **Step 4: Attach the presenter with UnitySkills, not raw YAML editing**

Open BasicInteraction, add `HandSkeletonPresenter` to `Blaze Interaction Runtime` or a dedicated child, wire the existing Canvas/root, save the scene, and capture the Unity Console. Preserve `BasicInteractionPresenter` Radar footprint particles and cursor.

- [ ] **Step 5: Run Unity tests and live fake-frame validation**

Inject or replay an InteractionFrame with eight hand extensions. Verify 168 joint visuals plus the expected bone visuals appear, update without unbounded object creation, and disappear after Cancel/disconnect. Verify Unity Console has zero errors.

- [ ] **Step 6: Commit**

```powershell
git add UnityPackage/com.blaze.interaction/Runtime UnityPackage/com.blaze.interaction/Tests/Runtime UnityPackage/com.blaze.interaction/Samples~/BasicInteraction
git commit -m "feat(unity): demonstrate multi hand skeletons"
```

### Task 4: Validate native/model release layout

**Files:**
- Modify: `scripts/publish-interaction-bridge.ps1`
- Modify: `scripts/bridge-release-functions.ps1`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/Blaze.Interaction.Bridge.Wpf.csproj` only if explicit content-copy metadata is required
- Modify: `UnityPackage/com.blaze.interaction/Editor/InteractionBridgePayloadValidator.cs`
- Modify: `tests/Blaze.Interaction.Release.Tests/InteractionPublishLayoutTests.cs`
- Modify: `tests/Radar.Unity.Compatibility.Tests/BridgePayloadValidatorTests.cs`
- Modify: `tests/Radar.Unity.Compatibility.Tests/EmbeddedBridgePayloadTests.cs`

**Interfaces:**
- Produces: release validation requiring native DLL/model and rejecting forbidden executables/runtimes.

- [ ] **Step 1: Write failing release-layout tests**

Assert publish contains exactly one `.exe`, named `BlazeInteractionBridge.exe`; CameraVision contains `Blaze.HandTracking.Native.dll` and `models/hand_landmarker.task`; hashes match `eng/mediapipe-hand.json`; missing/duplicate assets fail; names containing Python/CameraWorker/MediaPipeWorker/ProviderHost fail; OpenCvSharp requirements remain.

- [ ] **Step 2: Run Release and compatibility tests and observe missing validation failures**

```powershell
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release --nologo
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --filter "BridgePayloadValidator|EmbeddedBridgePayload" --nologo
```

- [ ] **Step 3: Implement manifest-driven validation**

Read `eng/mediapipe-hand.json` during repository-side publish tests; copy its required hashes into a provider-local runtime manifest for standalone payload validation. Scan recursively for forbidden `.exe` and Python runtime names. Preserve exactly-one-EXE checks already present.

- [ ] **Step 4: Publish to a fresh explicit directory**

```powershell
$publishRoot = Join-Path $env:TEMP ('BlazeHandRelease-' + [guid]::NewGuid().ToString('N'))
powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 -OutputDirectory $publishRoot
Get-ChildItem $publishRoot -Filter *.exe -File -Recurse | Select-Object FullName
```

Expected: publish succeeds and prints only `BlazeInteractionBridge.exe`.

- [ ] **Step 5: Run release tests and commit**

```powershell
dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release --nologo
dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --nologo
git add scripts src/Blaze.Interaction.Bridge.Wpf/Blaze.Interaction.Bridge.Wpf.csproj UnityPackage/com.blaze.interaction/Editor tests/Blaze.Interaction.Release.Tests tests/Radar.Unity.Compatibility.Tests
git commit -m "build(camera): validate hand runtime release assets"
```

### Task 5: Embed the validated Bridge and run automated gates

**Files:**
- Update generated validated payload: `UnityPackage/com.blaze.interaction/Bridge~/win-x64/`

**Interfaces:**
- Produces: Unity-importable package containing the same tested Bridge, Radar Provider, CameraVision Provider, native DLLs, and model.

- [ ] **Step 1: Publish and embed from a clean temporary output**

```powershell
$publishRoot = Join-Path $env:TEMP ('BlazeHandEmbed-' + [guid]::NewGuid().ToString('N'))
powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 -OutputDirectory $publishRoot -EmbedUnityPackage
```

- [ ] **Step 2: Run full .NET, Radar, release, and embedded smoke gates**

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -Configuration Release
```

Expected: all tests PASS; Radar is at least 447/447; release and embedded smoke pass; no second business executable exists.

- [ ] **Step 3: Import the local package in the external Unity test project**

Set the package dependency to the local package path for development validation, let Unity recompile, run all package EditMode tests, and verify Console zero errors. Do not change the user's unrelated project assets.

- [ ] **Step 4: Run live Radar regression in BasicInteraction**

Start Radar simulation, verify standard cursor plus ring `Fp` particles, Unity connection status, Radar connection status, and saved Radar configuration. Any failure blocks Camera validation.

- [ ] **Step 5: Commit the embedded payload**

```powershell
git add UnityPackage/com.blaze.interaction/Bridge~/win-x64
git commit -m "build(unity): embed camera hand bridge payload"
```

### Task 6: Real USB Camera single- and multi-hand validation

**Files:**
- Create: `docs/camera-vision/camera-vision-hand-mvp-0.1-report.md`
- Create: `docs/camera-vision/evidence/camera-vision-hand-mvp-0.1-metrics.json`
- Create: `docs/camera-vision/evidence/camera-vision-hand-mvp-0.1-hardware-checklist.md`

**Interfaces:**
- Produces: final hardware and performance evidence; does not change runtime behavior unless a failing test first reproduces a defect.

- [ ] **Step 1: Start from a fresh project-scoped Camera configuration**

Launch Unity BasicInteraction and Bridge. Select Camera, choose the real USB device, set supported resolution/FPS, and verify Camera/Unity connected states plus live preview.

- [ ] **Step 2: Validate one hand completely**

Verify 21 preview joints, Index Tip, Palm Center, current X/Y, four-point calibration, five target positions, outside-surface suppression, Cancel on exit, and recovery on re-entry. Verify Unity cursor and skeleton correspond.

- [ ] **Step 3: Validate multiple hands progressively**

Test 2, 4, then at least 8 simultaneous real hands in frame. For each level record detected hand count, Unity `InteractionPoint` count, each point's 21-landmark count, ID continuity during ordinary motion, and observed crossing/occlusion limitations. If eight real hands cannot be arranged, record the gate as pending and do not mark PASSED.

- [ ] **Step 4: Record performance for the accepted configuration**

Write `camera-vision-hand-mvp-0.1-metrics.json` with camera model, resolution, requested/actual Camera FPS, inference FPS, output FPS, average inference latency, dropped frames, Bridge CPU, and working-set memory during a representative multi-hand interval. Write each numbered hardware observation and its PASS/FAIL result to `camera-vision-hand-mvp-0.1-hardware-checklist.md`.

- [ ] **Step 5: Validate provider switching and persistence**

Switch Camera to Radar and back. Verify each provider reloads its prior project-scoped configuration. Restart Unity and verify the last selected provider auto-connects. Repeat in a second Unity project or Player sandbox and verify settings do not leak.

- [ ] **Step 6: Write the report with an honest gate status**

The report includes all required versions, hashes, camera model, measurements, Unity evidence, test totals, Radar result, and limitations. Only write `CAMERA VISION HAND MVP 0.1 PASSED` when every real hardware step succeeds.

- [ ] **Step 7: Commit evidence/report**

```powershell
git add docs/camera-vision/camera-vision-hand-mvp-0.1-report.md docs/camera-vision/evidence
git commit -m "docs(camera): report hand MVP hardware validation"
```

### Task 7: Final verification and handoff

**Files:** None unless verification finds a defect.

- [ ] **Step 1: Run final clean automated verification**

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -Configuration Release
powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -Configuration Release
git diff --check
```

- [ ] **Step 2: Run Unity EditMode and live scene verification through UnitySkills**

Record test totals, Console error count, active provider, active point count, and skeleton count. Exit Play Mode and stop the Bridge process cleanly.

- [ ] **Step 3: Review the diff against the design spec**

Confirm every included requirement has implementation/tests and every excluded feature remains absent. Search for forbidden Camera Provider branching in Unity core and forbidden runtime names.

```powershell
rg -n "CameraVision|blaze\.camera\.vision" UnityPackage/com.blaze.interaction/Runtime
rg -n -i "python|CameraWorker|MediaPipeWorker|ProviderHost" UnityPackage/com.blaze.interaction/Bridge~/win-x64
```

Expected: Camera text appears only in optional typed data/docs where justified; forbidden runtime search has no executable/runtime hits.

- [ ] **Step 4: Request independent code review and fix findings with tests first**

Review native ownership/ABI, frame lifetime, Provider lifecycle, selection persistence, Radar preservation, Unity cleanup, and release contents. Any correction starts with a failing regression test and reruns the affected complete gate.

- [ ] **Step 5: Confirm clean branch and report exact commits/results**

```powershell
git status --short --branch
git log --oneline c0bee25..HEAD
```

Do not push, tag, or create a public release unless the user requests that external action.
