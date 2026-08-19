# Gate A SDD Progress

Plan: `docs/superpowers/plans/2026-08-19-blaze-interaction-gate-a.md`

| Task | Status | Commit | Verification |
|---|---|---|---|
| 0. Radar 1.2.10 baseline | Complete | pending | .NET 398/398; Unity EditMode 6/6; PlayMode 68/68 |
| 1. Interaction contracts | Complete | e9c744b + c5491dd + 07bd238 + current JSON matching fix | Contracts 39/39; full solution 437/437; Radar .NET 398/398 |
| 2. Provider API/catalog/loader | Complete | 27eb51c + hardening pending | Runtime 34/34; full solution 471/471; Radar .NET 398/398 |
| 3. Provider manager | Pending | pending | pending |
| 4. Interaction IPC | Pending | pending | pending |
| 5. Radar provider | Pending | pending | pending |
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
