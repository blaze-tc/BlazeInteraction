# Blaze Radar SDK 1.2.7

The package connects Unity to the self-contained Windows x64 `RadarBridge.exe` through IPC protocol 2 on a Named Pipe. Unity never opens the radar TCP socket.

## Install or upgrade

Use the reviewed tag:

```text
https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.7
```

Remove any old Git URL before adding this URL. Select Blaze Radar SDK in Package Manager and confirm version `1.2.7`; its Resolved Path must be the newly resolved `Library/PackageCache/com.blaze.radar@...`, not an old cache or local override. If Unity remains stale, close the Editor, remove only this package's cache entry and `Packages/packages-lock.json` entry, then reopen and resolve the tagged URL.

## Screen topology and Bridge setup

1. Open **Project Settings > Blaze Radar**. Add any number of enabled logical screens, give each a stable unique Screen ID and logical resolution, set ordering, and select exactly one Primary screen.
2. Use **GameObject > Blaze Radar > Create Runtime**. Keep one enabled EventSystem input module.
3. After IPC connects, select a Unity screen in Bridge. Add or remove sensors per screen; configure every sensor's model, radar/local NIC endpoint, transform, active/masked areas, calibration and output rectangle.
4. Tune fusion distance/data age/output rate and tracking association/confirmation/loss/smoothing per screen.
5. A representative cave is LEFT with L1, FRONT with overlapping F1/F2 output rectangles, and RIGHT with R1. Overlap is fused within FRONT; pointer IDs are stable within a screen only.

## Samples and camera routing

- **Basic Interaction**: run without hardware using Bridge simulation, then repeat through IPC. Verify Button, Toggle, Slider, Scroll, 2D and 3D targets and preserve the on-screen log plus `Player.log`.
- **Multi-Screen Camera Routing**: use LOCAL first, then BRIDGE IPC. Bind each screen to an independent Display, a Camera `pixelRect`, or a RenderTexture. Confirm screenId and camera routing remain one-to-one and per-camera world particles appear on the intended wall.

Every Canvas needs a `GraphicRaycaster`; add `PhysicsRaycaster` or `Physics2DRaycaster` to the relevant Camera. Use `RadarAndMouseDebug` only for local mouse comparison and `RadarOnly` for deployment.

## Build and diagnostics

`RadarBuildProcessor` copies the complete `Bridge~/win-x64` directory beside the Player as `RadarBridge/`. It removes stale output first and rejects mismatched package/SDK/`bridge-version.txt` identities, incomplete payloads, or an EXE SHA-256 mismatch. Record the Player-side `RadarBridge.exe` version marker and SHA-256 for the field build.

Bridge logs use `[SCREEN/SENSOR]` tags. Match their timestamps with `Player.log` fields for SDK/Bridge/IPC versions, screenId, batch/frame sequence, pointer count, dropped count and latency. IPC v1 and v2 are incompatible: close all old Bridge processes, remove the old package URL/cache, reinstall the tag, verify both versions are 1.2.7/IPC 2, then reconnect.

RadarBridge forces GPU-independent WPF software rendering. On the projection computer still click, drag, resize, minimize/restore and change projector focus; no control may disappear or become blurry. Complete the repository `INSTALL.md` three-projector/four-radar 8-hour checklist before site acceptance.
