# Multi-Screen Camera Routing

This sample is a reference three-wall CAVE for Blaze Radar SDK screen-to-Camera routing. It binds the logical screens `left`, `front`, and `right` to three explicitly authored Cameras and raycasts against the corresponding wall colliders.

## Run locally

1. Open `MultiScreenCameraRouting.unity` and enter Play Mode.
2. Leave **LOCAL SIMULATION** selected. The sample emits a deterministic 30 FPS batch with elliptical LEFT/RIGHT trajectories and two permanent crossing FRONT pointers.
3. Watch each live point table and the bounded diagnostic log. A log hit includes logical pixels, normalized coordinates, Camera pixels, ray origin/direction, collider, and world position.

## Run through RadarBridge

1. Click **BRIDGE IPC**. This stops local generation, emits/recycles active pointers, then connects the scene's `RadarFrameDispatcher` (`autoConnect` is disabled).
2. In RadarBridge, configure the three logical screens and start the desired simulated or real sensors.
3. Click **LOCAL SIMULATION** to disconnect IPC fully before the deterministic source starts again.

The scene uses these logical definitions:

| ScreenId | Display | Logical resolution | Primary |
| --- | --- | ---: | --- |
| `left` | LEFT | 1920 x 1440 | No |
| `front` | FRONT | 4096 x 1536 | Yes |
| `right` | RIGHT | 1920 x 1440 | No |

Particles are pooled by `(ScreenId, PointerId)`, prewarmed to 16 instances, and capped at 64. They recycle on Pointer `Up`, a sustained zero-pointer frame, source switching, or component disable. Continuous `Move` history is throttled to 10 Hz per key while the live point tables remain unthrottled. The history retains at most 300 lines.

> The three-screen scene is a reference topology, not an SDK limit. Production projects can author any valid screen collection and Camera binding layout.
