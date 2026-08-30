# Radar Project State and Footprint Test Report

- Date: 2026-08-24
- Verified commit: `0f8d96f48b9020c47405bd848d8a04f10e5c60e5`
- Scope: Gate A only (`RadarControl -> Interaction Core -> Radar Provider -> Interaction IPC -> com.blaze.interaction`)
- Unity project: `E:/UnityProject/BlazeInteraction-Test`
- Unity version: `2021.3.45f1`

## Automated Results

| Gate | Exact command | Passed | Failed | Result |
| --- | --- | ---: | ---: | --- |
| Full solution | `dotnet test BlazeInteraction.sln -c Release --nologo` | 750 | 0 | PASS |
| Radar hard gate | `powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release` | 445 | 0 | PASS |
| Release layout | `dotnet test tests/Blaze.Interaction.Release.Tests/Blaze.Interaction.Release.Tests.csproj -c Release --nologo` | 1 | 0 | PASS |
| Embedded Bridge smoke | `powershell -ExecutionPolicy Bypass -File scripts/test-embedded-bridge.ps1 -StartupTimeoutSeconds 20` | 2 | 0 | PASS |
| Unity EditMode, open Editor | UnitySkills `test_run(testMode=EditMode)` | 6 | 0 | PASS |
| Unity PlayMode, open Editor | Unity TestRunner with `Gate9PlayModeResult.json` callback evidence | 58 | 0 | PASS |

All listed gates completed with zero skipped or inconclusive tests. The embedded smoke covered the Radar provider and the unchanged CameraVision provider only to verify package completeness; no CameraVision B1-B6 work was performed.

## Live Unity Evidence

- Loaded scene: `BasicInteraction` in `E:/UnityProject/BlazeInteraction-Test/Assets`.
- Project sandbox configuration: `E:/UnityProject/BlazeInteraction-Test/Library/BlazeInteraction/Providers/blaze.radar.f10f20/config.json`.
- Project isolation: the configuration is under the current Unity project's `Library/BlazeInteraction` root. A different Unity project or built player's sandbox resolves to a different data root.
- Unity state transition:
  - detached Bridge: `Unity：未连接`;
  - authenticated Interaction IPC 1 `Hello/HelloAck` in Play Mode: `Unity：已连接`, runtime `IPC: CONNECTED`, provider `blaze.radar.f10f20`;
  - leaving Play Mode terminated the Unity-owned Bridge process.
- Radar aggregate: one configured Radar row was reflected as `雷达：1/1 已连接`; simulation then produced `main / 1 / Move` through the unified Radar provider and Interaction IPC path.
- Save/reload validation:
  - before: `fusionDistancePixels=80`, `OutputRect=(X=0, Width=1920)`;
  - edited and saved through the visible WPF controls: `fusionDistancePixels=81`, `OutputRect=(X=1, Width=1919)`;
  - disk after save: `81`, `X=1`, `Width=1919`;
  - after a full Bridge restart, the visible controls reloaded the exact values `81`, `X=1`, `Width=1919` without manual re-entry;
  - final restored state: `sourceMode=real`, `fusionDistancePixels=80`, `OutputRect=(X=0, Width=1920)`, four active-polygon points.
- Live simulation frame:
  - provider/instance: `blaze.radar.f10f20 / radar-main`;
  - point: `id=1`, `surfaceId=main`, `phase=Move`;
  - center `PixelPosition=(1275.1536865234375, 886.7918701171875)`;
  - normalized position `(0.6641425490379334, 0.821103572845459)`;
  - `Fp.Count=9`;
  - source synthetic cluster count `9`, so every actual cluster point was mapped into `InteractionPoint.Fp`.
- Evidence files retained in the test project's sandbox:
  - `E:/UnityProject/BlazeInteraction-Test/Library/BlazeInteraction/Gate9PlayModeResult.json`;
  - `E:/UnityProject/BlazeInteraction-Test/Library/BlazeInteraction/Gate9RuntimePoint.json`;
  - `E:/UnityProject/BlazeInteraction-Test/Library/BlazeInteraction/Gate9SaveReload.json`.
- Final Unity state: not playing, not compiling, not updating.
- Final Unity Console: 7 logs, 0 warnings, 0 errors.

## Hardware Status

Real Radar validation: REQUIRES HARDWARE VALIDATION.

The configured F10 device at `192.168.0.100:8487` was not physically available during this run. Real device identity, real scan transport, and real-world point-cloud geometry therefore still require validation on the target Radar hardware. Simulation and all automated Radar gates passed; this status does not claim a real-hardware pass.

## Execution Limitation

The standalone command `scripts/test-unity-package.ps1 -UnityEditor "D:\Developer\2021.3.45f1\Editor\Unity.exe" -TestPlatform All -IncludeSamples` was not launched from the Codex terminal because that Unity installation requires elevation while the terminal is non-administrative. The already-open elevated Unity Editor compiled the final package with zero errors and supplied the reported EditMode and PlayMode results.
