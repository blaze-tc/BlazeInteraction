# Gate A SDD Progress

Plan: `docs/superpowers/plans/2026-08-19-blaze-interaction-gate-a.md`

| Task | Status | Commit | Verification |
|---|---|---|---|
| 0. Radar 1.2.10 baseline | Complete | pending | .NET 398/398; Unity EditMode 6/6; PlayMode 68/68 |
| 1. Interaction contracts | Complete | e9c744b + c5491dd + 07bd238 + current JSON matching fix | Contracts 39/39; full solution 437/437; Radar .NET 398/398 |
| 2. Provider API/catalog/loader | Complete | 27eb51c + hardening pending | Runtime 34/34; full solution 471/471; Radar .NET 398/398 |
| 3. Provider manager | Complete | c247463 + 559fbd3 + 2a9673f + this commit | Runtime 72/72; full solution 509/509; Radar .NET 398/398 |
| 4. Interaction IPC | Complete | facaaaf + 2f00daa + de74f99 + current session-hardening fix | IPC 50/50 Release and Debug; full solution 559/559; Radar .NET 398/398 |
| 5. Radar provider | Complete | this commit | Provider 19/19 Release and Debug; Radar .NET 400/400; full solution 580/580; Unity EditMode 6/6; PlayMode 68/68 |
| 6. Bridge/publish | Pending | pending | pending |
| 7. Unity core runtime | Pending | pending | pending |
| 8. Unity input/compatibility | Pending | pending | pending |
| 9. Final docs/verification | Pending | pending | pending |

## Baseline evidence

- Source: `RadarControl/main@aeb9635d755ea431c6f55a3fa959f31fdb3e75a2`
- Current-repo .NET run: 398 passed, 0 failed, 0 skipped.
- Current-repo Unity EditMode run: 6 passed, 0 failed, 0 inconclusive.
- Live Unity 2021.3.45f1 PlayMode run with both package Samples imported: 68 passed, 0 failed, 0 skipped, 0 inconclusive.
- Unity Skills 1.8.4 lost its PlayMode job record across Domain Reload, so an ephemeral test-host callback persisted the authoritative NUnit XML to `tmp/live-unity-playmode-results.xml`; the callback script was deleted after the run.

## Task 1 evidence

- RED: `dotnet test tests\Blaze.Interaction.Contracts.Tests\Blaze.Interaction.Contracts.Tests.csproj -c Release --nologo` exited 1 because the Contracts project and required interaction types did not exist.
- GREEN: the same targeted command passed 9/9 after adding the provider-neutral contracts, raw/typed extension model, and deterministic JSON codec.
- Radar regression: `powershell -ExecutionPolicy Bypass -File scripts\test.ps1 -Configuration Release` passed 398/398 (17 Protocol, 15 Device, 24 IPC, 61 Processing, 47 Configuration, 134 Bridge WPF, 97 Unity compatibility, 3 end-to-end).
- Review RED: the expanded 27-test boundary suite failed 11 tests against `e9c744b`, covering mutable JSON options, aliased point collections, invalid identities/values/enums, and malformed typed extensions.
- Review GREEN: the expanded Contracts suite passed 27/27 after freezing JSON options, snapshotting points, validating construction/JSON boundaries, and hardening typed extension parsing.
- Full solution: `dotnet test BlazeInteraction.sln -c Release --nologo` passed 425/425 (27 Interaction Contracts plus 398 Radar tests).
- Radar recheck: one full-script run exposed the pre-existing HelloAck/active-pipe timing race in `Server_PublishesOnlyAfterHelloAckIsWritten`; no Radar diff existed, the isolated test passed 10/10, and a fresh complete `scripts/test.ps1` rerun passed 398/398.
- Payload review RED: the expanded 34-test suite failed exactly 5 new cases: null point at object/JSON boundaries and missing x, missing y, or both coordinates in `Vector2Data` JSON.
- Payload review GREEN: Contracts passed 34/34 with null-element rejection and an explicit x/y converter; `(0,0)` and valid typed Hand tracking-point payloads remain accepted.
- Payload full verification: `dotnet test BlazeInteraction.sln -c Release --nologo` passed 432/432, and a fresh complete `scripts/test.ps1` passed the Radar baseline 398/398.
- JSON matching RED: the expanded 39-test suite failed exactly 4 cases for PascalCase/mixed-case coordinates and x/X or y/Y duplicate detection; exact matching with case-insensitivity disabled already passed.
- JSON matching GREEN: Contracts passed 39/39 after honoring `JsonSerializerOptions.PropertyNameCaseInsensitive`; full solution passed 437/437 and a fresh Radar script passed 398/398.

## Task 2 evidence

- RED: `dotnet test tests\Blaze.Interaction.Runtime.Tests\Blaze.Interaction.Runtime.Tests.csproj -c Release --nologo` exited 1 because the Provider Abstractions and Runtime projects and requested loader APIs did not exist.
- GREEN: the same targeted command passed 14/14 after adding provider-neutral lifecycle interfaces, resilient manifest discovery, typed load failures, and one collectible `AssemblyLoadContext` plus `AssemblyDependencyResolver` per provider.
- Isolation fixtures: two providers loaded `Blaze.TestProviders.SharedDependency` versions 1.0.0.0 and 2.0.0.0 concurrently from different collectible contexts; Contracts and Provider Abstractions resolved from `AssemblyLoadContext.Default`.
- Native resolution: the provider-local `native/<rid>` probe was exercised through the real unmanaged-load override and selected the fixture-local DLL path.
- Clean-run test-fixture correction: the first clean solution run exposed a parallel xUnit temporary-directory cleanup race, not a loader assertion failure; process-unique roots removed the race and the Runtime suite returned to 14/14.
- Full solution: `dotnet test BlazeInteraction.sln -c Release --nologo` passed 451/451 (53 Interaction plus 398 Radar tests).
- Radar regression: `powershell -ExecutionPolicy Bypass -File scripts\test.ps1 -Configuration Release` passed 398/398.
- Quality-review RED: the expanded boundary suite did not compile against `27eb51c` because duplicate-ID, manifest-size, and descriptor-mismatch diagnostics did not exist; after exposing those test contracts, the old implementation also bound mismatched shared assembly identities and trusted path/reparse inputs.
- Quality-review GREEN: Runtime passed 34/34 in both Release and Debug after enforcing canonical provider-local files, rejecting provider/manifest/entry reparse escapes, validating plugin descriptors, using exact shared-assembly identities, restricting native names, and making `LoadedProvider.Dispose` atomic and collectible.
- Quality-review lifecycle evidence: plugin and load-context `WeakReference` instances became dead while the disposed `LoadedProvider` itself remained alive; 500 parallel Dispose/Plugin operations produced only valid values or `ObjectDisposedException`.
- Quality-review full verification: `dotnet test BlazeInteraction.sln -c Release --nologo` passed 471/471 (73 Interaction plus 398 Radar tests), and a fresh `scripts/test.ps1 -Configuration Release` passed Radar 398/398.

## Task 3 evidence

- RED: `dotnet test tests\Blaze.Interaction.Runtime.Tests\Blaze.Interaction.Runtime.Tests.csproj -c Release --no-restore` exited 1 with CS0246 because the requested `ProviderManager` API did not exist.
- GREEN: the Runtime suite passed 48/48 after adding the instance-keyed, single-active manager, active-point cancellation registry, serialized switch/stop lifecycle, provider-neutral events, failure cleanup, and event isolation.
- Rollback RED: the focused replacement-failure test failed 1/1 because a failed candidate left consumers without the required old-provider-to-inactive state transition.
- Rollback GREEN: Runtime passed 49/49 after publishing deterministic inactive rollback only when a replacement fails after the old provider has stopped.
- Stale-callback RED: the focused captured-status test failed 1/1 because a callback captured before unsubscription could still cross the provider-instance boundary after switching.
- Stale-callback GREEN: Runtime passed 50/50 after filtering status callbacks against the manager's current instance under the state lock.
- Full solution: `dotnet test BlazeInteraction.sln -c Release --no-restore --nologo` passed 487/487 (89 Interaction plus 398 Radar tests).
- Radar regression: `powershell -ExecutionPolicy Bypass -File scripts\test.ps1 -Configuration Release` passed 398/398.
- Boundary-review RED: four focused tests failed against `c247463`: admitted frame/status callbacks did not delay Switch, a throwing Cancel subscriber aborted deactivation, and Stop failure restored the old provider to Running. Factory registration, diagnostics, and fault-state tests also failed to compile because those APIs did not exist.
- Boundary-review GREEN: Runtime passed 60/60 after adding generation-scoped callback admission/drain, per-subscriber exception isolation with diagnostics, fail-closed Stop/Dispose behavior, factory-created fresh instances, disposal retry for unresolved resources, and faulted-manager switch rejection.
- Boundary-review order evidence: successful replacement is `close admission -> await in-flight callbacks -> Cancel -> unsubscribe -> Stop -> Dispose -> create -> initialize -> start -> ProviderChanged`; captured callbacks from the retired generation cannot forward frames, status, or rejection events.
- Subscription-cleanup RED: a focused custom-event test failed because a Status event add accessor throwing after the Frame subscription left one handler attached and skipped Stop/Dispose.
- Subscription-cleanup GREEN: Runtime passed 61/61 after moving subscription into the activation cleanup boundary and isolating event remove accessor failures through diagnostics.
- Boundary-review full verification: `dotnet test BlazeInteraction.sln -c Release --no-restore --nologo` passed 498/498 (100 Interaction plus 398 Radar tests), and a fresh `scripts/test.ps1 -Configuration Release` passed Radar 398/398.
- Callback-ordering review RED: a synchronous `StopAsync(...).GetAwaiter().GetResult()` from `FrameReceived` exceeded the bounded 750 ms assertion against the previous manager; the sequence tests did not compile without an explicit non-increasing rejection reason, and the two factory-identity tests exposed `DisposeCount == 0` after a throwing getter plus a second getter read while formatting a mismatch.
- Callback-ordering review GREEN: Runtime passed 69/69 after adding an outbound-dispatch `AsyncLocal` reentrancy guard before every lifecycle semaphore acquisition, serializing sequence validation, registry mutation, and all frame publication per active provider/surface, and routing the single instance-ID read through deterministic cleanup.
- Callback-ordering evidence: Frame, Status, ProviderChanged, and Diagnostic synchronous lifecycle reentry all fail fast without deadlock and are isolated as diagnostics; an external `Task.Run` switch after callback completion remains valid. Sequence 2 arriving before sequence 1 publishes only the monotonic sequence 2 and generated Cancel sequence 3, while sequence 1 is rejected as `NonIncreasingSequence`.
- Callback-ordering full verification: targeted Runtime passed 69/69; `dotnet format BlazeInteraction.sln whitespace --verify-no-changes --no-restore --include ...` exited 0; `dotnet test BlazeInteraction.sln -c Release --no-restore --nologo` passed 506/506 (108 Interaction plus 398 Radar tests); a fresh `scripts/test.ps1 -Configuration Release` passed Radar 398/398.
- Nested-dispatch review RED: after the provider's synchronous `Emit` had returned, an async continuation captured by a frame subscriber still failed `StopAsync` as false reentry; direct nested sequence-2 emission recursively entered frame subscribers at depth 2 and delivered Up before Down to the later consumer; an unbounded nested burst published all 130 frames without rejection or diagnostics.
- Nested-dispatch review GREEN: Runtime passed 72/72 after replacing the copied `AsyncLocal` depth with linked, shared dispatch-state tokens that deactivate when their subscriber scope returns, and replacing the reentrant monitor publication path with one per-session dispatcher over a capacity-64 FIFO. Nested frames return after enqueue, every consumer completes the current frame before the next is published, concurrent callers retain synchronous completion, and Stop/Switch callback draining includes every accepted queue item.
- Nested-dispatch capacity evidence: bursts beyond 64 pending frames are not queued; each overflow reports `CapacityExceeded` through `FrameRejected` and `FrameQueueCapacityExceeded` through `Diagnostic`, while the unique dispatcher continues draining accepted frames without recursion.
- Nested-dispatch full verification: targeted concurrency/reentry tests passed 9/9; Runtime passed 72/72; whitespace verification exited 0; full solution passed 509/509 (111 Interaction plus 398 Radar tests); a fresh Radar script passed 398/398.

## Task 4 evidence

- Framing/message RED: the new IPC test project failed to compile because `Blaze.Interaction.Ipc` and the protocol/message/codec APIs did not exist.
- Framing/message GREEN: 16/16 tests passed for Protocol 1 constants, deterministic 4-byte little-endian UTF-8 JSON frames, partial reads, EOF, cancellation, negative/zero/oversized lengths, malformed/unknown JSON, all nine message types, Hello topology/Unity metadata, and HelloAck provider/capabilities.
- Pipe-server RED: the expanded test project failed to compile because the Interaction pipe server and bounded outbound queue did not exist.
- Pipe-server GREEN: IPC passed 32/32 after adding Hello-first authentication, Ack-before-connected publication, Ping/Pong, protocol/topology errors, reconnect reset, Shutdown, deterministic single-client serialization, idempotent cancellation/disposal, bounded control backpressure, and latest-value frame coalescing.
- Active-dispose RED/GREEN: disposing the server with an authenticated client initially raced `InteractionPipeSession.RunAsync` and threw `ObjectDisposedException`; the server now cancels/deactivates, waits for the run loop's deterministic cleanup, and only then disposes shared cancellation state. The focused regression passed 1/1 and the IPC suite passed 33/33.
- Malformed-session review RED: three real Named Pipe tests sent an oversized length prefix, a malformed JSON frame, and a Ping payload with an invalid numeric value after a successful Hello/HelloAck. Each test observed `InteractionPipeServer.RunAsync` fault instead of preserving the accept loop.
- Malformed-session review GREEN: the authenticated session boundary now isolates only expected client/protocol `InvalidDataException`, `JsonException`, and existing transport/EOF/cancellation failures; all three tests proved state reset, a second Hello/HelloAck, a still-running accept loop, and clean final cancellation. Listener failures and fatal exceptions remain unhandled.
- Writer-fault review RED: two internal post-handshake session tests injected `InvalidDataException` and direct `JsonException` from the server write path; the session returned normally, proving the broad session-level protocol catches hid server program faults.
- Writer-fault review GREEN: recoverable protocol handling now lives only around `ReadAsync` length/decode validation and Ping payload deserialization, producing an explicit `ProtocolError` session outcome and cancelling the writer. Session aggregation no longer catches protocol/JSON exceptions, so both injected writer faults propagate unchanged while the three malformed-client reconnect tests remain green.
- Session-hardening RED: real-pipe null-surface and duplicate-order Hello tests either faulted the accept loop or authenticated invalid topology; claimed and expected PID mismatch tests authenticated because the server did not verify the OS pipe client; handshake/heartbeat/send timeout tests did not compile because finite timeout options did not exist; and deterministic continual-control replenishment starved a pending latest frame beyond nine dequeues.
- Session-hardening GREEN: Hello validation rejects null/invalid/duplicate topology without broad exception catches; `CurrentUserOnly` pipes verify the claimed PID against `GetNamedPipeClientProcessId`, the Windows session against `GetNamedPipeClientSessionId`, and an optional expected PID before Ack/Connected publication. Identity lookup fails closed and bad sessions remain reconnectable.
- Liveness/backpressure GREEN: positive finite handshake, heartbeat, and send timeouts release silent, inactive, and non-reading clients; only valid inbound Ping/Pong refreshes heartbeat, outbound frame publication does not; send timeout deactivates the session, unblocks all bounded control producers, observes the abandoned reader, and permits a second Hello/Ack. Queue disposal waits for every pending enqueue cancellation before disposing synchronization primitives, eliminating the multi-waiter teardown race.
- Queue-fairness GREEN: control messages retain bounded FIFO backpressure and latest-only frames retain coalescing, while a maximum burst of eight controls forces a pending frame to be selected within nine dequeues under continual control replenishment.
- Provider-neutral boundary: `Blaze.Interaction.Ipc.csproj` references only `Blaze.Interaction.Contracts`; production IPC paths contain no `Radar.*` or `Yuexin` references and no Provider API creates a pipe.
- Session-hardening verification: IPC passed 50/50 in Release and Debug; `dotnet test BlazeInteraction.sln -c Release --no-restore --nologo` passed 559/559 (161 Interaction plus 398 Radar tests); a fresh `scripts/test.ps1 -Configuration Release` passed Radar 398/398.
- V1 risk/decision: the named pipe intentionally allows one active client; a second client waits until the authenticated session disconnects. Control messages wait on a bounded queue instead of being dropped, while only pending `InteractionFrame` messages are coalesced to the latest value.

## Task 5 evidence

- Coordinator provider-mode RED: the focused Radar Bridge test did not compile because `RadarBridgeCoordinator` had no explicit provider-mode switch. A second focused test then failed because provider mode could silently start without an output seam.
- Coordinator provider-mode GREEN: `enableLegacyIpc: false` starts the existing scheduler and `PointerBatchPayload` output callback without constructing or listening on `RadarPipeServer`; the default `true` path and legacy Hello/HelloAck behavior remain unchanged. Provider mode now rejects a missing output callback at construction.
- Adapter RED/GREEN: the Radar Provider tests first failed because `RadarFrameAdapter` did not exist, then passed after mapping each `RadarScreenPointerFrame` to one `InteractionFrame` while preserving surface/screen ID, frame sequence/timestamp, pointer ID, normalized/pixel coordinates, confidence, point timestamp, and exact Hover/Down/Move/Up phases. Unknown phases and invalid contract ranges fail closed.
- Source semantics: `ProviderId` is `blaze.radar.f10f20`, the stable instance is `radar-main`, and every existing fused PointerBatch point uses `SourceId=radar-fused-output` plus Radar extension `sensorId=fused-output`. These values deliberately identify the established per-screen fusion stream and do not claim false F1/F2 single-sensor provenance that `PointerBatchPayload` does not contain.
- Provider lifecycle RED/GREEN: missing plugin/provider/runtime types failed the new tests before implementation. The provider now initializes the existing coordinator from `ProviderInitializationContext.Surfaces` through the old Hello topology, loads `profiles/default-profile.json` when present or accepts test/host services, self-constructs the established pipeline factory and logger when absent, and implements ordered status, cancellation, failure cleanup, idempotent Stop/Dispose, per-surface frame publication, and subscriber-exception isolation.
- Radar behavior boundary: Simulation and Replay commands delegate to the existing `RadarBridgeCoordinator`; the provider does not copy or edit F10/F20 protocol, device, processing, configuration, fusion, tracking, calibration, OutputRect, Touch, or Dwell algorithms.
- Manifest/loader RED/GREEN: the real build directory was initially undiscoverable without `provider.json`; after adding the exact Radar manifest, `ProviderCatalog` and `ProviderLoader` load the built provider in an independent collectible ALC, share Contracts/Provider Abstractions from the default context, and complete initialize/start/stop/dispose.
- Single-host decision: referencing the existing WPF WinExe initially copied `RadarBridge.exe` into the provider build output and failed the release-layout test. The provider project now removes the legacy apphost/runtimeconfig after build while retaining `RadarBridge.dll` and its private `Radar.*` dependencies; both Release and Debug provider outputs contain no executable. Task 6 remains responsible for composing the one final `BlazeInteractionBridge.exe` host.
- Targeted verification: Radar Provider passed 19/19 in Release and Debug; affected Radar Bridge passed 136/136. Whitespace verification and `git diff --check` exited 0.
- Full .NET verification: `scripts/test.ps1 -Configuration Release` passed 400/400 (the frozen 398 tests plus two provider-mode boundary tests), and `dotnet test BlazeInteraction.sln -c Release --no-restore --nologo` passed 580/580.
- Unity Radar regression: live Unity 2021.3.45f1 passed EditMode 6/6 and reload-safe PlayMode 68/68 from a fresh result XML (`result=Passed`, `failed=0`). The temporary callback, package/sample junctions, empty host directories, and Unity-generated sample metadata were removed after the run.
