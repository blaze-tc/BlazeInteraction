# Changelog

## 1.2.2 - 2026-07-30

- Split each selected radar's diagnostics into an unmodified raw scan view and a separate transformed, box-filtered view used by screen fusion and Unity output.
- Add a high-contrast dark-theme CheckBox template with explicit checked, hover, keyboard-focus and disabled states.
- Add a Basic Interaction point-delegate binder that converts logical screen pixels through the selected Camera and emits a reusable world-space particle effect.
- Add a Chinese illustrated operator guide covering installation, upgrade, every Bridge work area, buttons, parameters, Sample verification and troubleshooting.

## 1.2.1 - 2026-07-28

- Recover rejected, malformed or unsupported Bridge configuration at startup after preserving the original bytes in a timestamped backup.
- Start every enabled radar pipeline automatically after a successful Unity topology handshake.
- Treat normal Named Pipe cold-start/reconnect timeouts as pending state and throttle identical actionable IPC errors across reconnect attempts.
- Throttle high-frequency sensor, fusion and IPC metrics while preserving pointer lifecycle and diagnostic events.
- Make the Multi-Screen Camera Routing sample launch the bundled RadarBridge automatically while keeping LOCAL/BRIDGE input selection explicit.

## 1.2.0 - 2026-07-28

- Add arbitrary Unity logical screens with stable IDs, logical resolutions, ordering and exactly one enabled primary screen.
- Add multiple independently configured F10/F20 sensors per screen, per-sensor output rectangles and per-screen fusion/tracking/interaction parameters.
- Fuse overlapping sensors within a screen and publish screen-addressed pointer batches over IPC protocol 2 with stable per-screen pointer IDs.
- Add independent Display, Camera `pixelRect` and RenderTexture routing plus the Multi-Screen Camera Routing LOCAL/BRIDGE sample.
- Tag Bridge diagnostics by screen/sensor and expand Unity logs with screen, sequence, count, drop and latency fields.
- Harden Windows player packaging with complete self-contained payload validation, version markers, SHA-256 identity and safe release publishing.
- Document 1.1.x upgrade/cache recovery, IPC v1/v2 incompatibility, projector display checks and the three-projector/four-radar 8-hour acceptance procedure.

## 1.1.5 - 2026-07-21

- Force WPF software rendering before window creation to prevent projector/GPU dirty-region corruption that could hide or blur controls.
- Add a persistent display-only radar range shared by both plots and reduce point radii so dense nearby scans remain readable without changing filtering.
- Use the editable four-corner active region as the Unity normalization map when no saved calibration exists.
- Send pointer frames even when they contain zero pointers, allowing Unity diagnostics to distinguish an empty filtered frame from a stalled IPC stream.
- Write throttled IPC/frame diagnostics to Unity `Player.log` and flush Bridge file logs while the process is running.

## 1.1.4 - 2026-07-21

- Always package RadarBridge from the currently resolved `com.blaze.radar` package instead of a machine-persistent legacy EditorPrefs path.
- Remove the previous player `RadarBridge` directory before copying so deleted or renamed Bridge files cannot survive incremental builds.
- Stamp embedded Bridge payloads with the package version and fail the Unity build when the package, SDK or Bridge versions differ.
- Verify the copied `RadarBridge.exe` against the package source with SHA-256 and report the packaged version and hash in the Unity Console.

## 1.1.3 - 2026-07-21

- Split RadarBridge visualization into a read-only raw sensor view and a separate transformed/filtered Unity output view.
- Keep transform, range/angle, polygon and mask processing on the output path used to generate Unity pointers while leaving the raw view unchanged.
- Add a 220 ms display-only point persistence trail to reduce scan-to-scan flicker without delaying or modifying IPC frames.
- Declare Per-Monitor V2 DPI awareness, use actual pixels-per-DIP for radar labels and enable rounded ClearType layout for projector and mixed-scaling PCs.
- Simplify the output plot to effective points, tracked targets and editable output regions instead of stacking raw, valid, cluster and blind-zone overlays.

## 1.1.2 - 2026-07-20

- Add an in-scene diagnostics panel for IPC state, Bridge/device identity, frame sequence, timing, dropped frames and per-pointer samples.
- Log standard UGUI, Physics and Physics2D EventSystem callbacks with pointer ID, pixel position, delta, press position and raycast target.
- Keep drag/scroll probes limited to controls that already implement those interactions, preserving native Button and Toggle behavior.
- Keep history bounded and throttle continuous frame/drag updates so long-running device tests remain readable and memory-safe.
- Preserve IPC envelope sequence and timestamp metadata on Unity pointer frames for end-to-end fault isolation.

## 1.1.1 - 2026-07-20

- Improve contrast across the RadarBridge WPF theme and Basic Interaction sample controls.
- Require a completed Hello/HelloAck handshake before Unity reports IPC connected and add response timeouts.
- Recover from malformed or short-lived Named Pipe clients without stopping the Bridge accept loop.
- Validate radar endpoints eagerly, isolate event subscriber failures and cancel active Unity pointers on IPC loss.
- Make launcher failures visible in the Unity Console and reject missing replay files at the API boundary.
- Replace the runtime-generated demo with an authored UGUI scene wired through persistent UnityEvents.
- Route radar pointers through standard Graphic, Physics and Physics2D raycasters, including pointer-over queries and clean module deactivation.
- Prevent overlapping Unity IPC loops during stop/restart and make pointer cancellation safe against reentrant event handlers.

## 1.1.0 - 2026-07-20

- Rename the UPM package to `com.blaze.radar` and its public Unity namespace to `Blaze.Radar`.
- Add an explicit Basic Interaction sample assembly definition that references the runtime and Unity UI assemblies.
- Add Unity 2021.3 compatibility for EventSystem selection handling and validate Runtime, Editor and Sample compilation in Unity 2021.3.45f1.

## 1.0.1 - 2026-07-20

- Embed the complete self-contained win-x64 RadarBridge publish directory in the UPM package.
- Resolve the embedded Bridge automatically in the Unity Editor and copy it beside Windows players.
- Fix WPF startup bindings for read-only CRC metrics and make Unity parent-process shutdown clean.

## 1.0.0 - 2026-07-20

- Initial F10-default/F20-compatible RadarBridge launcher and Named Pipe client.
- Multi-pointer UGUI, 3D and 2D EventSystem input.
- Radar-only and integrated mouse-debug modes.
- Build-time Bridge copy validation and Basic Interaction sample.
