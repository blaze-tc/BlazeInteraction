# Console UI and Camera runtime final verification

Captured on 2026-08-29 for the CameraVision console stabilization and MediaPipe
out-of-bounds ROI correction.

## Reported failure

The native MediaPipe graph could fail while a tracked hand crossed a frame edge:

`ImageToTensorCalculator failed: Invalid crop coordinates.`

Shutdown then awaited the already-faulted processing task again, causing the
same runtime failure to be shown a second time as a provider-disposal popup.

## Corrections

- Float tracking ROIs that extend beyond the camera frame now use the existing
  border-aware warp path. In-bounds and integer-aligned frames retain the
  optimized FrameBuffer path.
- The native build applies and verifies the out-of-bounds ROI patch, and the
  checked-in native runtime hash is pinned in `eng/mediapipe-hand.json`.
- Disposal suppresses only the processing fault already exposed through the
  authoritative completion task; independent cleanup failures remain visible.
- The shared provider header now receives its view model before XAML command
  bindings are initialized, so **返回选择感应设备** invokes navigation reliably.
- Radar provider test repository discovery now supports Git worktrees, where
  `.git` is a file rather than a directory.

## Automated verification

| Scope | Result |
| --- | ---: |
| CameraVision provider | 160/160 |
| Bridge WPF | 96/96 |
| RadarControl full regression | 484/484 |
| BlazeInteraction solution | 966/966 |
| Unity EditMode | 6/6 |
| Unity PlayMode | 70/70 |
| Native ABI exports | 7/7 |

The native smoke test moves a real attributed hand image across all four frame
edges and verifies that processing completes without invalid crop coordinates.

## Live Unity verification

- Unity 2021.3.45f1 project: `E:\UnityProject\BlazeInteraction-Test`.
- Final packaged Bridge ran in Play Mode for approximately 3 minutes 20 seconds.
- The CameraVision console displayed the real 1280 x 720 camera feed, detected a
  hand, drew its center and 21 landmarks, and rendered the final Unity output
  preview.
- Unity reported zero errors during the run.
- A 10-second process sample measured 125.16% of one logical core and a 333.6 MB
  working set while camera capture and native inference were active. The process
  remained responsive.
- Stopping Play Mode terminated the Bridge cleanly. No duplicate disposal popup
  or residual Bridge window/process remained.

Status: **CAMERA RUNTIME GATE PASS** for the reproduced crop-failure path and
the current demonstration workflow.
