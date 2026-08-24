# CameraVision Hand MVP 0.1 Design

**Date:** 2026-08-24  
**Status:** Approved for implementation planning  
**Baseline:** `c0bee25e42eb7d912c11dfb16dadd14e59321985`  
**Provider:** `blaze.camera.vision`  
**Target:** .NET 8, Windows x64, Unity package `com.blaze.interaction`

## 1. Objective

Deliver the fastest demonstrable, real USB-camera hand sensing path that:

1. detects every hand returned by the configured backend;
2. exposes all 21 MediaPipe hand landmarks to Unity;
3. computes an Index Tip or Palm Center tracking point;
4. maps the tracking point and landmarks into the current Interaction Surface;
5. publishes one standard `InteractionPoint` per tracked hand through Interaction IPC 1;
6. renders the hand skeleton in the Unity BasicInteraction sample; and
7. preserves the completed Radar behavior and regression baseline.

The hardware acceptance target is at least eight simultaneous hands in one camera frame. The code and configuration do not impose a business maximum of two or eight hands. The practical maximum remains constrained by the MediaPipe backend, image quality, and available CPU resources.

## 2. Scope Decisions

### 2.1 Included

- Official MediaPipe Tasks Hand Landmarker through an in-process Windows x64 native integration.
- A small stable native C ABI in `Blaze.HandTracking.Native.dll` if the official API cannot be consumed safely without it.
- Configurable positive `MaxHands`, with a default of 8 for this MVP.
- 21 landmarks for every detected hand.
- Index Tip and Palm Center tracking modes; Index Tip remains the initial default.
- Lightweight position-based hand track IDs.
- Four-point homography calibration for one Interaction Surface.
- EMA smoothing, default factor 0.35.
- A minimal CameraVision WPF configuration and preview UI.
- A generic Bridge provider-selection page with remembered selection.
- Project- and Player-sandbox-scoped Bridge and provider configuration.
- A typed Unity hand-extension reader and an optional multi-hand skeleton presenter.
- Fake hand backend tests, real USB-camera validation, performance recording, packaging validation, and all Radar regressions.

### 2.2 Excluded

- Left/right hand classification as a product feature.
- Identifying which person owns a hand.
- Permanent identity across severe occlusion or hand crossings.
- Gesture, pinch, fist, open palm, drag, swipe, or pointer-down inference.
- Color marker, HSV, YOLO, generic object detection, or camera-specific IPC messages.
- Multi-camera, simultaneous providers, Radar and Camera fusion, or a second business process.
- A fabricated hand outline. `Fp` remains a contour field and stays empty until a backend supplies a real contour.
- Visual polish beyond a usable diagnostic UI.

## 3. Existing Components Reused Without Rewrite

- `OpenCvCameraCaptureBackend` and `CameraCaptureService`.
- `LatestFrameSlot<CameraFrame>` and its drop-old-frame ownership rules.
- Provider API 1 discovery, loading, and lifecycle management.
- `ProviderManager.SwitchAsync`.
- Interaction IPC 1 and its existing named-pipe transport.
- Unity `InteractionFrameDispatcher` and provider-neutral cursor behavior.
- Radar Provider, Radar control UI, Radar configuration, and Radar processing chain.
- Project-scoped Unity `InteractionProjectScopeResolver`.

## 4. Runtime Architecture

```text
OpenCvSharp camera capture thread
  -> LatestFrameSlot<CameraFrame>
  -> CameraVision inference loop
  -> BGR-to-RGB conversion
  -> IHandDetectionBackend
  -> MediaPipeHandBackend
  -> native MediaPipe Tasks Hand Landmarker
  -> lightweight HandTrackAssigner
  -> TrackingPointCalculator
  -> HomographySurfaceMapper
  -> EmaPositionFilter
  -> InteractionPoint / InteractionFrame
  -> Interaction IPC 1
  -> Unity InteractionFrameDispatcher
  -> generic cursor + optional HandSkeletonPresenter
```

Camera capture and inference remain independent background operations. WPF only observes immutable snapshots suitable for preview and status display. It never performs inference on the UI thread.

There is no inference queue. The inference loop atomically takes the latest available frame. If capture publishes newer frames before inference takes the current one, `LatestFrameSlot` disposes the replaced frames and increments the dropped-frame count.

## 5. Managed Hand Backend Boundary

`CameraVisionProvider` depends only on a managed backend abstraction. It must not reference MediaPipe types.

Conceptual contract:

```csharp
internal interface IHandDetectionBackend : IAsyncDisposable
{
    HandBackendStatus Status { get; }
    Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken);
    ValueTask<HandDetectionResult> DetectAsync(
        RgbFrameView frame,
        long timestampUnixMs,
        CancellationToken cancellationToken);
}
```

`HandDetectionOptions` contains at least:

- model path;
- positive `MaxHands`;
- detection confidence;
- tracking confidence.

`HandDetectionResult` contains zero or more `DetectedHand` values. Each hand contains detection confidence and exactly 21 normalized landmarks in MediaPipe index order. Each landmark contains finite `X`, `Y`, and `Z`. Handedness is neither required nor published.

Invalid hand results are rejected as a unit before tracking or publication. A result is invalid when its landmark count is not 21, a coordinate is non-finite, or its confidence is outside 0 through 1.

`FakeHandDetectionBackend` is dependency-injected for automated tests only. Production does not fall back to fake output.

## 6. Native MediaPipe Integration

The preferred implementation is the current official MediaPipe Tasks Hand Landmarker C API, wrapped when necessary by `Blaze.HandTracking.Native.dll`.

The wrapper ABI remains small and versioned. Its conceptual operations are:

```text
Create(options, out handle, error)
ProcessFrame(handle, rgbPointer, width, height, stride, timestampMs, error)
GetResult(handle, outputBuffer, outputCapacity, out handCount, error)
Destroy(handle)
```

Rules:

- Native exceptions never cross the ABI.
- All methods return explicit status codes and bounded error text.
- Input is a packed or explicitly-strided RGB buffer whose lifetime extends through `ProcessFrame`.
- Output copies only stable Blaze structures: hand confidence and 21 normalized XYZ landmarks.
- Native result memory is not retained by managed code.
- Handles are safe to destroy after partial initialization and are destroyed exactly once.
- The MediaPipe version, native binaries, model version, and SHA-256 hashes are fixed in the release report.
- Python and Bazel may be build tools but are not runtime dependencies.

Before the main backend implementation, a Windows x64 spike must prove:

```text
Create -> RGB frame -> detect -> one or more 21-point results -> destroy
```

If the official Windows integration has a blocking build or runtime defect, implementation stops and creates `docs/camera-vision/hand-backend-spike-report.md` before any fallback is selected. The report includes version, build method, DLLs, model, successes, failures, exact logs, compatibility findings, and compliant alternatives.

## 7. Frame Ownership and Color Conversion

Gate B0 continues to own camera capture, mirror, and rotation. The inference loop:

1. takes ownership of the latest `CameraFrame`;
2. converts its OpenCV BGR image to an owned RGB buffer;
3. passes a pinned pointer, dimensions, stride, and monotonic timestamp to the backend;
4. keeps the buffer alive until the native call returns;
5. publishes a preview snapshot without exposing a mutable `Mat`; and
6. deterministically disposes the RGB buffer and source frame.

Mirror and rotation are applied once in the existing capture path. Neither calibration nor the backend applies them again.

## 8. Tracking Point and Lightweight Multi-Hand IDs

Tracking modes:

- `IndexTip`: landmark 8.
- `PalmCenter`: arithmetic mean of landmarks 0, 5, 9, 13, and 17.

The lightweight `HandTrackAssigner` matches current detections to active tracks by nearest tracking-point distance, with a configurable distance gate and a small configurable lost-frame tolerance. It uses no handedness or person identity.

- A continuing visible hand retains its `InteractionPoint.Id` under ordinary motion.
- A new unmatched hand receives a monotonically increasing positive ID.
- A missing hand remains eligible for recovery only through the configured lost-frame tolerance.
- When the tolerance is exceeded, the provider emits one `Cancel` point and removes the track.
- Re-entry after removal may receive a new ID.
- Severe occlusion or hand crossing may exchange IDs and is an accepted MVP limitation.

## 9. Surface Calibration, Mapping, and Smoothing

Each configured Interaction Surface stores four camera-pixel calibration points in this order:

1. P1 Left Top;
2. P2 Right Top;
3. P3 Right Bottom;
4. P4 Left Bottom.

OpenCV computes a homography from those camera pixels to the unit-square Interaction Surface. Calibration is valid only when all four points are finite, non-degenerate, and produce a usable transform.

The tracking point and all 21 landmarks use the same homography. The tracking point must lie within the calibrated polygon and map to the inclusive 0 through 1 surface interval. Otherwise no live `InteractionPoint` is emitted for that hand. Landmarks are mapped for display and may lie outside the unit square when the tracking point is valid.

EMA applies to each track's mapped tracking point:

```text
smoothed = previous + (current - previous) * smoothingFactor
```

`SmoothingFactor` is finite and within 0 through 1, defaulting to 0.35. No Kalman filter, One Euro filter, or prediction is introduced.

## 10. Interaction Contract

Each active tracked hand produces one standard `InteractionPoint`:

- `ProviderId`: `blaze.camera.vision`.
- `ProviderInstanceId`: `camera-vision-main`.
- `SourceId`: stable text derived from the lightweight track ID.
- `Id`: lightweight track ID.
- `SurfaceId`: selected Interaction Surface.
- `Phase`: `Hover` while present and `Cancel` exactly once when removed.
- `NormalizedPosition`: smoothed tracking point in surface coordinates.
- `PixelPosition`: smoothed tracking point multiplied by logical surface dimensions.
- `Confidence`: backend hand confidence.
- `Fp`: empty for this MVP.
- `Extensions["hand"]`: typed hand data described below.

Hand extension schema version 1:

```json
{
  "hand": {
    "schemaVersion": 1,
    "trackingPoint": "IndexTip",
    "landmarks": [
      {
        "index": 0,
        "normalizedPosition": { "x": 0.5, "y": 0.5 },
        "pixelPosition": { "x": 960.0, "y": 540.0 },
        "z": -0.03
      }
    ]
  }
}
```

The `landmarks` array contains exactly 21 entries in ascending index order. The fixed MediaPipe hand connection table lives in the managed/Unity typed helper and is not repeated in every IPC frame.

The existing `Fp` contract remains a contour list. It is not overloaded with landmarks. Radar payloads and Radar typed extensions remain unchanged.

## 11. Unity Consumption and Demo

Unity core continues to deserialize and dispatch provider-neutral `InteractionPoint` objects. It must not branch on `ProviderId == blaze.camera.vision`.

The package adds typed optional hand models and a `TryGetHandExtension` helper. Malformed or unknown hand schema versions return `false` without dropping the underlying point or raw extension.

An optional `HandSkeletonPresenter` in the BasicInteraction sample:

- subscribes to standard point added, updated, and removed events;
- renders only points with a valid hand extension;
- displays 21 joints and the fixed hand bone connections;
- displays the standard tracking point;
- assigns a deterministic display color per current track ID;
- makes no left/right or person claim; and
- removes visuals on `Cancel`, disconnect, provider switch, or component disable.

The existing Radar cursor and footprint-particle demonstration remain active and unchanged for Radar points.

## 12. Bridge Provider Selection Shell

The Bridge adds a generic provider-selection page without referencing Radar or Camera implementation types.

First launch for a project scope:

- if no saved provider selection exists, show the selector normally even when Unity passed `--minimized`;
- list successfully loaded providers from their descriptors;
- show Radar and Camera as user-facing sensing modes; and
- persist the selected provider before starting it.

Later launches:

- load the saved provider ID;
- use it as the preferred provider for the first Unity Hello handshake;
- automatically connect that provider; and
- honor minimized startup.

Every provider settings surface gets a Bridge-owned header showing:

- Unity connected/disconnected;
- current sensing device;
- device/provider status;
- a `Return to device selection` command.

Returning to selection does not stop the active provider. Confirming a different selection calls the existing `ProviderManager.SwitchAsync`, flushes the provider transition through IPC, then presents the new settings view. Cancelling returns to the current provider.

The existing Radar settings window is not rewritten. The Bridge wraps or decorates it with the generic navigation header and preserves its lifecycle and content.

## 13. Project-Scoped Persistence

The existing Unity launcher continues to pass a scoped `--data-root`:

```text
Editor: <Unity project>/Library/BlazeInteraction/
Player: <Application.persistentDataPath>/BlazeInteraction/
```

Storage layout:

```text
BlazeInteraction/
|-- bridge-settings.json
`-- Providers/
    |-- blaze.radar.f10f20/
    |   `-- existing Radar configuration
    `-- blaze.camera.vision/
        `-- camera-vision.json
```

`bridge-settings.json` stores only host-owned selection state, including the last selected provider ID. `camera-vision.json` stores camera capture, hand, tracking, smoothing, and per-surface calibration settings.

Rules:

- Selecting a provider loads its existing configuration or creates and atomically saves defaults.
- Radar keeps its current configuration format and migration behavior.
- Valid Camera changes are saved and applied explicitly; calibration point changes save immediately.
- Atomic replacement prevents partially written JSON from becoming the active configuration.
- Invalid configuration is not silently overwritten. The UI reports the path and validation error and offers an explicit reset-to-default action.
- The in-memory `BridgeProviderSettingsContext` is not the source of truth for persistent device configuration.

## 14. CameraVision Settings and Preview UI

The minimal WPF view contains:

### Camera

- Device
- Resolution
- FPS
- Mirror
- Rotation

### Hand

- Enabled
- Max Hands, positive integer, default 8
- Detection Confidence
- Tracking Confidence
- Tracking Point: Index Tip or Palm Center
- Lost Frame Tolerance

### Calibration

- Set P1, P2, P3, P4 from preview clicks
- Reset
- selected Interaction Surface

### Smoothing

- EMA Smoothing Factor, default 0.35

### Preview overlays

- current camera frame
- 21 landmarks and fixed bone connections for every detected hand
- tracking point
- calibrated surface polygon
- confidence
- bounding area only if supplied by the backend

No handedness label is shown.

### Status

- Unity connection
- Camera connection
- Camera FPS
- Inference FPS
- Output FPS
- average inference latency
- detected hand count
- current X/Y per track
- dropped frames
- backend error state

## 15. Lifecycle and Failure Behavior

- Camera unavailable: Gate B0 reconnect behavior continues and the provider does not publish fake points.
- Native DLL, model, or dependency missing: initialization fails with an actionable error containing the missing path or native error code.
- One inference-frame failure: discard the frame, increment diagnostics, and keep capture/UI responsive.
- Persistent backend failure: stop valid output, cancel active hand points, expose a fault status, and allow explicit reconnect or settings apply.
- Configuration apply: stop and dispose affected capture/backend resources, atomically replace configuration, then restart using the new values.
- Provider switch and Bridge shutdown: cancel loops, await completion, dispose frames, camera backends, native handles, and UI subscriptions deterministically.
- No native error, WPF observer exception, or Unity observer exception may terminate an unrelated provider or transport callback.

## 16. Packaging

Final runtime layout contains exactly one business executable:

```text
BlazeInteractionBridge.exe
Providers/CameraVision/Blaze.Provider.CameraVision.dll
Providers/CameraVision/Blaze.HandTracking.Native.dll
Providers/CameraVision/hand_landmarker.task
Providers/CameraVision/<required native runtime DLLs>
```

The exact MediaPipe DLL arrangement is finalized by the native spike. Release validation rejects Python executables/runtimes, Camera or Provider workers, a second Bridge, and any other business executable. The Unity embedded Bridge payload includes the same validated CameraVision native assets and model.

## 17. Test Strategy and Gates

Every implementation batch follows red-green-refactor:

1. add a failing focused test;
2. run it and record the expected failure;
3. implement the smallest production behavior;
4. run the complete related test project;
5. run the applicable Radar regression before moving to the next integration boundary; and
6. commit the independently reviewable result.

Automated coverage includes:

- Palm Center and Index Tip calculations;
- invalid landmark count and non-finite coordinates;
- confidence filtering;
- configurable multi-hand results above two hands;
- lightweight track continuity, loss, cancel, and re-entry;
- mirror and all supported rotations;
- homography P1, P2, P3, P4, center, invalid calibration, and outside surface;
- EMA behavior and reset per track;
- backend initialize/detect/dispose and unavailable backend;
- Provider initialize/start/stop/restart, camera unavailable, detected hand, and lost hand;
- latest-frame replacement and frame/buffer disposal;
- first-run provider selection, remembered startup, return, cancel, switch, missing provider, and corrupt settings;
- Camera configuration isolation across two Unity project roots and two Player sandboxes;
- hand extension codec round trip and malformed extension tolerance;
- Unity dispatcher preservation of landmarks and skeleton presenter cleanup;
- exactly-one-business-EXE release layout and forbidden runtime scanning.

Mandatory final automated commands include:

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

They also include the Unity EditMode suite, embedded Bridge payload tests, release tests, and the complete current Radar Gate A suite. The current latest baseline is 752/752 full solution tests and 447/447 Radar hard-gate tests. Any Radar regression blocks further Camera work until resolved.

## 18. Real USB-Camera Acceptance

The final hardware gate validates:

1. first-run Camera selection and later automatic Camera startup;
2. real preview and Camera connected status;
3. one through at least eight simultaneous hands, subject to arranging enough real hands in view;
4. 21 visible landmarks for each accepted hand;
5. Index Tip and Palm Center switching;
6. P1 through P4 calibration;
7. left-top, right-top, right-bottom, left-bottom, and center Unity movement;
8. no valid point outside the calibrated surface;
9. correct cancel when a hand leaves and recovery on re-entry;
10. switching to Radar, loading existing Radar configuration, and switching back to the saved Camera configuration; and
11. Camera FPS, inference FPS, output FPS, average latency, dropped frames, CPU, and memory recording.

The completion report is `docs/camera-vision/camera-vision-hand-mvp-0.1-report.md` and records the real camera model, MediaPipe/backend version, configuration, performance, Unity evidence, automated tests, Radar regressions, native/model hashes, and known limitations.

The project may declare `CAMERA VISION HAND MVP 0.1 PASSED` only after the real chain succeeds:

```text
USB Camera
-> Hand Landmarker
-> 21 landmarks and Tracking Point
-> Surface Mapping
-> InteractionPoint
-> Interaction IPC 1
-> com.blaze.interaction
-> Unity cursor and hand skeleton
```

