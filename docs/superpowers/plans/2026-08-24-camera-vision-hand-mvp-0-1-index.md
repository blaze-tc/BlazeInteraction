# CameraVision Hand MVP 0.1 Plan Index

**Spec:** `docs/superpowers/specs/2026-08-24-camera-vision-hand-mvp-0-1-design.md`

Execute these plans in order. A plan is not allowed to start until the previous plan's tests and checkpoint commit pass.

1. `2026-08-24-camera-vision-native-backend-plan.md`
   - Pin and build MediaPipe 0.10.35.
   - Prove the Windows x64 native C ABI and a real 21-landmark result.
   - Stop and write the required spike report if this gate is blocked.
2. `2026-08-24-camera-vision-provider-core-plan.md`
   - Implement hand math, calibration, smoothing, lightweight multi-hand IDs, configuration, and provider output using tests first.
3. `2026-08-24-camera-vision-bridge-ui-plan.md`
   - Implement the remembered provider selector, generic Bridge header, Camera settings/preview, and scoped persistence.
4. `2026-08-24-camera-vision-unity-release-plan.md`
   - Add typed Unity hand data, the skeleton demo, release validation, embedded payload, real hardware evidence, and the completion report.

Global stop conditions:

- Any Radar failure stops Camera development until Radar is repaired.
- Any unexpected second runtime executable stops packaging work.
- A blocked official MediaPipe Windows integration produces `docs/camera-vision/hand-backend-spike-report.md`; no Python runtime, worker process, or unapproved backend substitution is allowed.
- `CAMERA VISION HAND MVP 0.1 PASSED` is reserved for the completed real USB Camera to Unity chain.
