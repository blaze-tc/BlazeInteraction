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
- The CameraVision workspace now keeps only two always-visible previews: the
  aspect-fit camera feed with the four draggable calibration handles overlaid,
  and the final Unity point preview. The separate warped preview was removed
  from the live render path.
- CameraVision output X/Y flips are persisted as schema 3 settings and are
  applied after calibration but before Unity surface scaling to the hand center,
  every footprint landmark, and the hand extension payload consistently.
- The CameraVision console displays the active Unity surface resolution as a
  read-only value and uses a dark heading color that remains legible on the
  white page background.
- The top camera preview now carries the complete 21-connection hand skeleton
  into the live drawing model. Calibration save/validation failures are shown
  through the normal UI error state and restore the last persisted overlay.

## Automated verification

| Scope | Result |
| --- | ---: |
| CameraVision provider | 169/169 |
| Bridge WPF | 96/96 |
| RadarControl full regression | 484/484 |
| BlazeInteraction solution | 976/976 |
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
- A second live Unity run verified the revised two-panel CameraVision window,
  overlaid calibration polygon and handles, dark console heading, `Unity 已连接`
  state, and responsive `CameraVision RGB Camera | Running` state. Unity Console
  again reported zero warnings and zero errors.
- The embedded Radar and CameraVision IPC smoke passed against the refreshed
  package payload. The published `BlazeInteractionBridge.exe` SHA-256 is
  `44c712ca19a8b01f84596150dd460a446c998fad7807272e9c420e47709a9ca2`.
- The final local Unity package contains 638 files. Its SHA-256 is
  `77e1016e1a75b6510aad4d3cf2471aeba2c8db97cbc523f7dc151f7e90da7ab3`.

Status: **CAMERA RUNTIME GATE PASS** for the reproduced crop-failure path and
the current demonstration workflow.
