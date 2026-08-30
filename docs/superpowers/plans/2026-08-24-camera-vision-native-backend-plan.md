# CameraVision Native Hand Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and prove an in-process Windows x64 MediaPipe Hand Landmarker backend that returns every configured hand with 21 normalized XYZ landmarks.

**Architecture:** A pinned MediaPipe 0.10.35 source build produces `Blaze.HandTracking.Native.dll` with a Blaze-owned, exception-safe C ABI. `MediaPipeHandBackend` loads that ABI behind `IHandDetectionBackend`; CameraVision and tests never reference MediaPipe structs.

**Tech Stack:** .NET 8, C# 12, C++20/MSVC, Bazel/Bazelisk, MediaPipe Tasks 0.10.35, xUnit, OpenCvSharp 4.13.

**Spec:** `docs/superpowers/specs/2026-08-24-camera-vision-hand-mvp-0-1-design.md`

## Global Constraints

- Runtime platform is Windows x64.
- Pin MediaPipe tag `v0.10.35`, commit `f8ef212d5c962c0e853db7e59d217056b187084b`.
- Final runtime has one business executable: `BlazeInteractionBridge.exe`.
- Python/Bazel are build tools only; Python is forbidden in the published runtime.
- Native exceptions never cross the ABI.
- Results contain no handedness requirement and exactly 21 landmarks per valid hand.
- `MaxHands` is any positive `int`; default 8, without a 2/8 business cap.
- Do not begin provider integration if the real native spike fails.

---

### Task 1: Freeze native provenance and the C ABI

**Files:**
- Create: `eng/mediapipe-hand.json`
- Create: `native/Blaze.HandTracking.Native/blaze_hand_tracking.h`
- Create: `native/Blaze.HandTracking.Native/BUILD.bazel`
- Create: `tests/Blaze.Provider.CameraVision.Tests/NativeAbiContractTests.cs`

**Interfaces:**
- Produces: ABI version 1 exports consumed by `NativeHandLibrary` in Task 4.
- Produces: `eng/mediapipe-hand.json` fields `repository`, `tag`, `commit`, `modelUrl`, `modelSha256`, `abiVersion`.

- [ ] **Step 1: Add the failing ABI source-contract test**

```csharp
[Fact]
public void NativeHeader_DeclaresVersionedExceptionSafeAbi()
{
    var header = File.ReadAllText(RepositoryPath("native/Blaze.HandTracking.Native/blaze_hand_tracking.h"));
    Assert.Contains("BLAZE_HAND_ABI_VERSION 1", header, StringComparison.Ordinal);
    foreach (var export in new[]
             {
                 "blaze_hand_get_abi_version", "blaze_hand_create", "blaze_hand_process_frame",
                 "blaze_hand_get_hand_count", "blaze_hand_copy_hand", "blaze_hand_destroy",
                 "blaze_hand_get_last_error"
             })
        Assert.Contains(export, header, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused test and verify it fails because the header does not exist**

Run:

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter NativeAbiContractTests --nologo
```

Expected: FAIL at the missing `blaze_hand_tracking.h` path.

- [ ] **Step 3: Add the pinned manifest and exact ABI declarations**

Use repository `https://github.com/google-ai-edge/mediapipe.git`, tag `v0.10.35`, commit `f8ef212d5c962c0e853db7e59d217056b187084b`, and official model URL `https://storage.googleapis.com/mediapipe-models/hand_landmarker/hand_landmarker/float16/1/hand_landmarker.task`. Download the model once to an explicit temporary file, compute SHA-256 with `Get-FileHash`, write that exact hash to `eng/mediapipe-hand.json`, then delete only that validated temporary file. All later downloads must match the frozen hash before replacement.

The header must use `extern "C"`, `__cdecl`, and `__declspec(dllexport)` on Windows. Define these stable layouts:

```cpp
#define BLAZE_HAND_ABI_VERSION 1
#define BLAZE_HAND_LANDMARK_COUNT 21
typedef void* BlazeHandHandle;
typedef struct BlazeHandOptions {
  uint32_t struct_size;
  int32_t max_hands;
  float min_detection_confidence;
  float min_tracking_confidence;
  const char* model_path_utf8;
} BlazeHandOptions;
typedef struct BlazeHandLandmark { float x; float y; float z; } BlazeHandLandmark;
typedef struct BlazeHandResult {
  float confidence;
  BlazeHandLandmark landmarks[BLAZE_HAND_LANDMARK_COUNT];
} BlazeHandResult;
```

Every operational export returns `int32_t`: zero for success, a stable nonzero error code otherwise. `blaze_hand_destroy` accepts null. `blaze_hand_get_last_error` copies bounded UTF-8 text into a caller buffer.

- [ ] **Step 4: Run the focused test and validate the provenance JSON**

Run the focused test above, then:

```powershell
Get-Content eng/mediapipe-hand.json -Raw | ConvertFrom-Json | Format-List
git diff --check
```

Expected: test PASS; manifest parses; diff check emits nothing.

- [ ] **Step 5: Commit the ABI contract**

```powershell
git add eng/mediapipe-hand.json native/Blaze.HandTracking.Native tests/Blaze.Provider.CameraVision.Tests/NativeAbiContractTests.cs
git commit -m "test(camera): define native hand ABI"
```

### Task 2: Build the official MediaPipe wrapper reproducibly

**Files:**
- Create: `native/Blaze.HandTracking.Native/blaze_hand_tracking.cc`
- Create: `scripts/build-hand-native.ps1`
- Create: `scripts/verify-hand-native.ps1`
- Modify: `native/Blaze.HandTracking.Native/BUILD.bazel`
- Create on successful build: `providers/CameraVision/Blaze.Provider.CameraVision/runtimes/win-x64/native/Blaze.HandTracking.Native.dll`
- Create on successful model verification: `providers/CameraVision/Blaze.Provider.CameraVision/models/hand_landmarker.task`

**Interfaces:**
- Consumes: Task 1 ABI and `eng/mediapipe-hand.json`.
- Produces: a loadable native DLL and model whose SHA-256 matches the provenance manifest.

- [ ] **Step 1: Make native verification fail on missing artifacts**

`scripts/verify-hand-native.ps1` must verify the DLL and model exist, their SHA-256 values match `eng/mediapipe-hand.json`, and the DLL exports all seven ABI symbols. Invoke it before building.

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-hand-native.ps1
```

Expected: FAIL naming the missing DLL or model.

- [ ] **Step 2: Implement the wrapper with complete exception containment**

`blaze_hand_create` validates `struct_size`, positive `max_hands`, confidence ranges, and UTF-8 model path; it creates MediaPipe Hand Landmarker in video mode with CPU inference. `blaze_hand_process_frame` validates pointer, dimensions, stride, and strictly increasing timestamp; it copies or wraps RGB data only for the duration permitted by MediaPipe, performs detection, and snapshots results inside the handle. `blaze_hand_copy_hand` rejects invalid indices/capacity and copies one complete `BlazeHandResult`.

Every export has this outer shape:

```cpp
try {
  return DoOperation(...);
} catch (const std::exception& error) {
  StoreError(handle, error.what());
  return BLAZE_HAND_NATIVE_FAILURE;
} catch (...) {
  StoreError(handle, "Unknown native failure.");
  return BLAZE_HAND_UNKNOWN_FAILURE;
}
```

- [ ] **Step 3: Implement the pinned build script**

The script must:

1. read `eng/mediapipe-hand.json`;
2. clone/fetch only into `artifacts/native-src/mediapipe`;
3. checkout and verify the exact commit;
4. copy the Blaze wrapper sources into an isolated overlay directory under that checkout;
5. build with Bazel Windows, C++20, Release/opt, and `MEDIAPIPE_DISABLE_GPU=1`;
6. copy only the resulting DLL and required dependent DLLs into the provider runtime directory;
7. download the official model to a temporary file, verify SHA-256, then atomically move it into `models/`; and
8. run `verify-hand-native.ps1`.

The script must not edit tracked MediaPipe source outside the temporary checkout and must not copy Python into the provider.

- [ ] **Step 4: Build and verify the native artifact**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-hand-native.ps1
powershell -ExecutionPolicy Bypass -File scripts/verify-hand-native.ps1
```

Expected: both commands exit 0 and print the MediaPipe commit plus DLL/model hashes.

- [ ] **Step 5: If blocked, write the required report and stop this plan**

Create `docs/camera-vision/hand-backend-spike-report.md` with the exact commands and unabridged error log, then commit only the report and reproducible spike inputs. Do not proceed to Task 3 or substitute a backend.

- [ ] **Step 6: Commit a successful native spike**

```powershell
git add eng/mediapipe-hand.json native/Blaze.HandTracking.Native scripts/build-hand-native.ps1 scripts/verify-hand-native.ps1 providers/CameraVision/Blaze.Provider.CameraVision/runtimes providers/CameraVision/Blaze.Provider.CameraVision/models
git commit -m "build(camera): add MediaPipe hand native runtime"
```

### Task 3: Define managed hand results independently of MediaPipe

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/HandDetectionContracts.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/HandDetectionContractTests.cs`

**Interfaces:**
- Produces: `IHandDetectionBackend`, `HandDetectionOptions`, `HandLandmark`, `DetectedHand`, `HandDetectionResult`, `RgbFrameView`, and `HandBackendStatus`.

- [ ] **Step 1: Write failing validation tests**

Cover positive MaxHands values including 8 and 32, rejection of zero/negative values, exactly 21 landmarks, non-finite XYZ rejection, confidence outside 0 through 1, and immutable snapshots.

Example:

```csharp
[Fact]
public void Result_AllowsMoreThanTwoHandsWithoutBusinessCap()
{
    var result = new HandDetectionResult(Enumerable.Range(0, 8).Select(_ => ValidHand()));
    Assert.Equal(8, result.Hands.Count);
}
```

- [ ] **Step 2: Run and observe compile failure**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter HandDetectionContractTests --nologo
```

Expected: FAIL because the managed hand types do not exist.

- [ ] **Step 3: Implement the smallest immutable contracts**

`RgbFrameView` stores pointer, width, height, stride, and validates RGB row capacity. `DetectedHand` snapshots exactly 21 values. The backend method signature must match the design spec and include cancellation.

- [ ] **Step 4: Run the entire CameraVision test project**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
```

Expected: all CameraVision tests PASS.

- [ ] **Step 5: Commit managed contracts**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision/Hand tests/Blaze.Provider.CameraVision.Tests/HandDetectionContractTests.cs
git commit -m "feat(camera): add hand backend contracts"
```

### Task 4: Implement and test the managed native loader

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/NativeHandLibrary.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/Hand/MediaPipeHandBackend.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/MediaPipeHandBackendTests.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj`

**Interfaces:**
- Consumes: Task 1 native exports and Task 3 contracts.
- Produces: `MediaPipeHandBackend : IHandDetectionBackend`.

- [ ] **Step 1: Write failing lifecycle and mapping tests**

Use an injectable `INativeHandLibrary` test double to cover create failure, ABI mismatch, detect success with eight hands, native error propagation, cancellation before native entry, monotonically increasing timestamps, and exactly-once destroy.

```csharp
[Fact]
public async Task DetectAsync_CopiesAllEightHandsAndTwentyOneLandmarks()
{
    var native = NativeLibraryStub.WithHands(8);
    await using var backend = new MediaPipeHandBackend(native);
    await backend.InitializeAsync(Options(maxHands: 8), CancellationToken.None);
    var result = await backend.DetectAsync(ValidRgbFrame(), 1000, CancellationToken.None);
    Assert.Equal(8, result.Hands.Count);
    Assert.All(result.Hands, hand => Assert.Equal(21, hand.Landmarks.Count));
}
```

- [ ] **Step 2: Run and verify compile failure**

Run the `MediaPipeHandBackendTests` filter. Expected: FAIL because the loader/backend do not exist.

- [ ] **Step 3: Implement dynamic library loading and SafeHandle ownership**

Resolve the provider-local runtime path, load with `NativeLibrary.Load`, verify ABI version 1 before create, resolve all required delegates once, and hold the native instance in a `SafeHandle`. Convert every nonzero status to `HandBackendException` containing the numeric code and bounded native error text.

- [ ] **Step 4: Add provider output-copy rules**

Update the CameraVision project so the native directory and `models/hand_landmarker.task` copy to build and publish output. Do not add a second executable or Python asset.

- [ ] **Step 5: Run unit tests and the real native smoke test**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter MediaPipeHandBackendTests --nologo
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter MediaPipeNativeSmoke --nologo
```

The real smoke test uses an Apache-compatible attributed hand test image, asserts at least one hand and exactly 21 finite landmarks, and disposes the backend before deleting its temporary frame buffer.

- [ ] **Step 6: Run Camera and Radar checkpoints**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: Camera suite PASS; Radar 447/447 PASS.

- [ ] **Step 7: Commit the native managed backend**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision tests/Blaze.Provider.CameraVision.Tests
git commit -m "feat(camera): integrate MediaPipe hand backend"
```

### Task 5: Native plan final verification

**Files:**
- Modify: `docs/camera-vision/hand-backend-spike-report.md` only when the spike had a blocking issue

**Interfaces:**
- Produces: a green prerequisite for the provider-core plan or an explicit blocked report.

- [ ] **Step 1: Scan published inputs for forbidden runtimes**

```powershell
Get-ChildItem providers/CameraVision/Blaze.Provider.CameraVision -Recurse -File | Where-Object { $_.Name -match 'python|worker|\.exe$' }
```

Expected: no output.

- [ ] **Step 2: Run the full solution baseline**

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
```

Expected: all tests PASS with no reduction from the 752-test baseline plus the new tests.

- [ ] **Step 3: Record the checkpoint**

```powershell
git status --short
git log -5 --oneline
```

Expected: clean worktree and the native backend commits visible. Only then begin `2026-08-24-camera-vision-provider-core-plan.md`.
