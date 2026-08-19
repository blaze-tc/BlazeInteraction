# Gate A SDD Progress

Plan: `docs/superpowers/plans/2026-08-19-blaze-interaction-gate-a.md`

| Task | Status | Commit | Verification |
|---|---|---|---|
| 0. Radar 1.2.10 baseline | Complete | pending | .NET 398/398; Unity EditMode 6/6; PlayMode 68/68 |
| 1. Interaction contracts | Complete | pending | Contracts 9/9; Radar .NET 398/398 |
| 2. Provider API/catalog/loader | Pending | pending | pending |
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
