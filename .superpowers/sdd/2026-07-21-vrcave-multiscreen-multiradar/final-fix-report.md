# Final whole-branch fix report

Status: **DONE_WITH_CONCERNS**. All eight final-review release blockers are implemented and all runnable .NET, real Named Pipe, publish-manifest and embedded-smoke gates pass. Unity remains explicitly blocked because the safe batch attempt produced no test-result XML; projector visual acceptance and the eight-hour field run remain manual.

## Scope and commits

- Whole-branch baseline: `7e8b3860a28f587eabd43da13a5b82df6b4c9b1c`.
- Rejected head and start of this fix wave: `47e8d15c1397ef9bfec145152cc14d73832d87ef`.
- Implementation, tests, release harness and regenerated payload: `480cab8d2e4dd0a656ad56e66545023ab574ec62` (`fix: close final review blockers`).
- This report is committed as the document-only successor; its exact SHA is recorded in the final handoff because a commit cannot contain its own SHA.
- No merge, tag or push was performed. The original worktree was not touched.

## 1. Zero-sensor Unity screens persist

Schema 2 now allows `screen.Sensors` to be null/empty and validates only sensors that exist. Enabled sensors still require a strict convex four-corner active polygon or valid calibration physical corners. The default sensor mapping was changed to a valid square so existing default construction remains valid.

The real-store/coordinator/Pipe integration test starts with the existing primary screen, sends a real Hello that adds a zero-sensor screen, receives HelloAck, reloads the persisted Profile and verifies stable screen IDs, resolution, order and primary status.

Design decision: zero sensors is a valid screen topology, but an enabled sensor without a usable physical-to-screen mapping is not.

## 2. WPF refreshes current configuration objects

`IRadarBridgeRuntime` now exposes `ConfigurationChanged`. `RadarBridgeCoordinator` raises it after successful topology reconciliation and configuration apply, outside the topology lock. `MainViewModel` captures its construction synchronization context, dispatches the refresh there, rebuilds `Screens` from the coordinator's current cloned objects, preserves selected screen/sensor IDs where possible, clears stale move-log state, and unsubscribes on dispose.

The integration test creates the VM before a real Hello, adds a screen through the real Pipe, verifies UI-thread collection refresh and selection preservation, edits the refreshed VM, executes Save/Apply, waits for command completion, and proves the current persisted configuration received the edit. The test was also hardened against reading the Profile during the store's short exclusive replace window.

Design decision: publish an explicit topology/configuration event instead of attempting to preserve every nested object identity across the coordinator's validated clone-and-swap boundary.

## 3. Associated-screen sensor workflow

- Associated screens now allow sensor add and remove.
- The last sensor may be removed, yielding the valid zero-sensor topology.
- Newly added sensors start disabled.
- Enabling is rejected unless the active polygon or calibration corners form a valid strict convex quadrilateral.
- Removing a sensor in Starting, Running or Reconnecting awaits `DisconnectSensorAsync` before removing it from configuration.

Tests cover associated add, zero-sensor removal, invalid enabled mapping, each active runtime state and disconnect-before-remove ordering.

## 4. Unity lifecycle-preserving buffering

The latest-only overwrite buffer was replaced with `LifecycleBatchBuffer`, a bounded 64-batch FIFO/coalescer:

- Down and Up batches remain ordered and are never overwritten.
- Adjacent Move/Hover/visual-only batches coalesce.
- At capacity a queued visual batch is evicted first; a new visual-only batch is deterministically dropped if the queue contains only lifecycle edges.
- When a new lifecycle edge reaches an all-edge full queue, the Pipe reader applies bounded backpressure until consumption or cancellation.
- `Clear` advances a generation and wakes blocked producers so pre-disconnect work cannot leak into a reconnect.

`RadarFrameDispatcher` drains at most eight batches per Unity frame. `RadarInputModule` uses its own bounded 64-frame queue, processes at most eight per `Process`, coalesces visual frames, and on an all-edge overflow processes exactly one oldest lifecycle frame synchronously on the Unity main thread before enqueueing the new edge. Disconnect clears pending frames and cancels active pointers.

Coverage includes the real .NET Pipe/client buffer sequence Down, Move, Up and zero after a simulated stall; buffer backpressure and Move-flood bounds; package dispatcher order; and input-module click lifecycle/high-rate Move tests.

## 5. Bounded recording writer

Each recording session now owns one bounded `Channel` of capacity 32 and exactly one writer task. The radar receive callback writes synchronously to the bounded channel, applying backpressure to TCP reads rather than allocating an unbounded fire-and-forget task per byte block. A producer gate prevents Stop races.

Normal Stop performs this deterministic sequence: stop accepting producers, await active producers, complete the channel, drain and await the sole writer task, dispose the writer, then dispose the stream. Source handlers are detached before pipeline shutdown. Writer I/O failure closes the queue, wakes blocked producers and is observed/logged; no writer task is abandoned.

The controlled slow-stream test sends 40 ordered one-byte entries, observes the queue at its exact 32-entry bound and the producer blocked, verifies one writer task, releases storage, calls Stop, reads the resulting bytes through `RadarRecordingReader`, and verifies every byte in order with an empty queue and disposed writer afterward.

## 6. Pointer and log-cache retirement

After each fusion tick, position/pressed state is removed for any pointer no longer owned by `PointerStateMachine`. This covers Touch, HoverOnly, Dwell and EnterTrigger while retaining a Touch pointer until a required minimum-press Up has actually been emitted.

Both coordinator and WPF Move-log throttle state now remove entries on Up, expire entries after five minutes, and enforce a deterministic 1,024-entry cap. WPF also clears the cache on configuration refresh and removes screen keys when an orphaned screen is deleted. Churn tests exercise 500 unique tracks in all four interaction modes and verify state returns to zero; log tests cover Up, TTL, capacity and normal throttle semantics.

## 7. Replay speed contract and UI

Runtime, configuration and editor now share the inclusive finite range `0.1` through `8.0`. Values are not snapped to a small enum. The WPF panel exposes an editable validated speed field and Replay Loop checkbox, and the replay file command uses the current selected sensor values.

Tests accept `0.1`, `4.0`, `4.25` and `8.0`, and reject below-range, above-range, NaN and infinities in runtime/configuration/editor validation.

## 8. Named Pipe client identity

The Pipe is created with `PipeOptions.CurrentUserOnly`. After connection, Windows `GetNamedPipeClientProcessId` and `GetNamedPipeClientSessionId` populate a verified authentication context. Before application authentication the server requires:

1. real client PID equals `Hello.UnityProcessId`;
2. client Session equals the Bridge Session;
3. when `--parent-pid` was supplied, real client PID also equals that expected Unity/Player PID.

PID/session resolution errors and unauthorized/mismatched clients close only that connection; the accept loop continues. Tests prove claimed-PID mismatch, expected-parent mismatch, verified current-user/session context, a malicious first connection followed by a legitimate HelloAck, and normal authentication. `App` accepts only a positive parent PID, passes it to the coordinator/server, and retains the existing parent-exit monitor.

Manual Bridge launch has a documented weaker boundary: CurrentUserOnly, same Session and verified real PID/Hello equality prevent PID spoofing across the Pipe, but another process under the same interactive user may honestly identify itself. Auto-launch adds the expected-parent check.

## RED evidence

- Zero-sensor real-store integration initially returned topology reconciliation failure under the old `sensors must contain at least one` rule.
- The pre-Hello VM integration initially had no added screen and edited orphaned configuration objects because there was no configuration-change event.
- Associated add/remove and last-sensor tests initially failed command eligibility; enabled unmapped sensors passed configuration validation; active removal did not disconnect first.
- The real client-stall lifecycle regression observed only sequence `[13]` from the old latest-only buffer instead of `[10, 11, 12, 13]`; new buffer/package tests also failed to compile before the lifecycle queue APIs existed.
- The slow-writer test failed to compile before the injected stream, queue-capacity, pending-count and single-writer APIs existed, reflecting the prior task-per-block design.
- Fusion churn initially left one `_pointerPositions` entry after HoverOnly/Dwell retirement; Move-log tests retained Up/missing-edge keys without a bound.
- Replay focused RED had 9 failures out of 13 because runtime still allowed only `0.5`, `1.0` or `2.0` and the editable UI contract was absent.
- Pipe identity tests failed to compile before the verified client context/PID APIs and three-argument authentication callback existed.
- After the security implementation, the first exact embedded smoke correctly failed with `unexpected_client_process`: the old harness advertised helper PID `43612` while outer PowerShell PID `48612` sent Hello. This exposed a real release-harness integration issue. The helper was changed to be both the advertised parent and actual Pipe client; the executable smoke then passed.

## GREEN verification

### Focused and full .NET

- `dotnet test tests\Radar.Unity.Compatibility.Tests\Radar.Unity.Compatibility.Tests.csproj -c Release --no-restore`: **93/93 passed**.
- `dotnet test tests\Radar.Ipc.Tests\Radar.Ipc.Tests.csproj -c Release --no-restore`: **24/24 passed**.
- Focused executable smoke behavior tests, including injected failure cleanup and nine-second setup delay: **3/3 passed**.
- First complete build/test run: `dotnet test RadarControl.sln -c Release --no-restore`: **368/368 passed**.
- Final no-build confirmation after publish and smoke-harness fix: `dotnet test RadarControl.sln -c Release --no-restore --no-build`: **368/368 passed**:
  - Protocol 17
  - IPC 24
  - Device 15
  - Processing 57
  - Configuration 44
  - Bridge WPF 115
  - Unity compatibility 93
  - End-to-End 3

The only build/publish warning was `NU1900` because `https://api.nuget.org/v3/index.json` vulnerability metadata was unreachable. Existing restored dependencies were used and no build/test failed.

### Real Named Pipe E2E and soak

`dotnet test tests\Radar.EndToEnd.Tests\Radar.EndToEnd.Tests.csproj -c Release --no-restore --no-build`: **3/3 passed**. This includes `ThreeScreensFourSensors_ThirtyHertzSoakRemainsBoundedDuringFrontSensorReconnects(30)`, the real 30 Hz/30-second Named Pipe soak.

### Fresh self-contained artifact

Command: `powershell -ExecutionPolicy Bypass -File scripts\publish-bridge.ps1`.

- Published root: `artifacts\publish\RadarBridge\win-x64`.
- Embedded root: `UnityPackage\com.blaze.radar\Bridge~\win-x64`.
- Published file count: **491**.
- Embedded file count: **491**.
- Independent manifest comparison: all **491 relative paths** and all **491 per-file SHA-256 values** matched exactly.
- Published and embedded `RadarBridge.exe` SHA-256: `DE0E5B0924A7117CF978696B4A22C42B2AB3AF8A3C0F190EA94D9771D8C2A0EC`.
- The publish validator passed the complete deps-derived self-contained manifest, both included frameworks, marker `1.2.0`, and both Schema 2 Profiles before and after embedding.

### Exact embedded smoke

Command: `powershell -ExecutionPolicy Bypass -File scripts\test-embedded-bridge.ps1 -StartupTimeoutSeconds 20`.

Result:

```text
Embedded RadarBridge top-level window passed: RadarBridge · 多屏雷达控制台
IPC v2 Hello/HelloAck passed with Bridge version 1.2.0.
Parent-process shutdown passed with exit code 0.
```

The committed smoke client now exercises, rather than bypasses, the expected-parent PID boundary.

### Repository hygiene

- `git diff --check`: pass; only Git's configured LF-to-CRLF informational warnings were printed.
- `git diff --cached --check` before the implementation commit: pass.
- No generated test XML was represented as a Unity pass.

## Unity package gate: BLOCKED

Safe attempted command:

```powershell
$env:__COMPAT_LAYER='RunAsInvoker'
powershell -ExecutionPolicy Bypass -File scripts\test-unity-package.ps1 `
  -UnityEditor 'D:\Developer\2021.3.45f1\Editor\Unity.exe' `
  -TestPlatform EditMode
```

Unity ran for approximately 112 seconds and its log ended with batchmode return code 0, but the harness produced no parseable result XML and no test-run marker. The log contains zero `error CS` matches, which is insufficient for the required gate.

- Log: `E:\WindowApp\RadarControl\.worktrees\codex-vrcave-multiscreen\tmp\unity-package-tests\TestResults\editmode-unity.log`.
- Expected EditMode XML: `E:\WindowApp\RadarControl\.worktrees\codex-vrcave-multiscreen\tmp\unity-package-tests\TestResults\editmode-results.xml` — absent.
- PlayMode XML — absent.
- XML count under TestResults: **0**.
- Exact environmental blockers:
  - log line 66: `CreateDirectory 'C:/Users/Tancheng/AppData/Roaming/Unity' failed: 拒绝访问。`
  - log line 70: `Error opening a HTTP REST server port between 38000 and 38100`

A final safe `All -IncludeSamples` retry was refused before launch because three user Unity processes (`1444`, `7328`, `27844`) and UnityPackageManager `19224`, all started on 2026-07-27, were still running. Per the safety requirement, none was killed or modified, no registry setting was changed, and no user project was touched.

Therefore Unity EditMode/PlayMode is **BLOCKED**, not passed.

## Self-review and remaining concerns

- Queue/caches have explicit bounds: Pipe lifecycle 64, dispatcher eight batches/frame, input 64/eight per Process, recording 32 with producer backpressure, log caches 1,024 plus five-minute TTL.
- Lifecycle ordering is preserved across both Pipe-client and input-module buffering; disconnect clears pending generations and cancels active pointers.
- Recording Stop drains before disposal; the single task is always awaited and writer failure wakes blocked producers.
- The identity checks use OS-reported PID/Session and keep the accept loop available after rejection. Manual-launch trust remains weaker by design and is documented.
- Task 14 version `1.2.0`, IPC `2`, publish validation and full self-contained payload semantics remain intact; the smoke harness was minimally adapted to the stronger identity contract.
- Task 7 `FormattedText` allocation remains the agreed non-blocking Minor; no unrelated rendering scope was added.
- Human WPF/projector sharpness, focus/DPI operations and the real three-projector/four-radar eight-hour field acceptance were not performed and must not be inferred from automated results.
- Unity XML remains the only automated release gate that could not be completed in this environment.
