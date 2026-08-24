# Task 5 Project Scope Report

Branch: `codex/radar-project-state-footprint`
Base SHA: `953193292cb33420881855df3d907e81477bdc59`

## RED evidence

- `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode` was run after the new Unity tests were added. It was blocked before compilation because no Unity 2021.3 editor was found at `C:\Program Files\Unity\Hub\Editor`; the exact runner error was: `No Unity 2021.3 editor was found under 'C:\Program Files\Unity\Hub\Editor'. Install Unity 2021.3 or pass -UnityEditor <path>.`
- `dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --nologo` produced the expected stale-contract failure in `PackageIdentityTests.Launcher_UsesSupportedInteractionBridgeArgumentsAndInheritedUnityLifecycle`: its old assertion rejected the required `arguments += " --profile"` source.

## GREEN evidence

- The scope resolver source was compiled and executed with a minimal protocol stub. It produced `E:\ProjectA\Library\BlazeInteraction`, the uppercase 16-hex scoped Pipe suffix `A4980547FFAD7062`, a different Pipe for ProjectB, a Player-relative profile at `C:\PlayerData\Profiles\radar.json`, and an unchanged absolute Profile path.
- `dotnet test tests/Radar.Unity.Compatibility.Tests/Radar.Unity.Compatibility.Tests.csproj -c Release --nologo` passed after the stale source assertion was replaced with the Task 5 contract assertions. The project discovers 101 tests; the complete run emitted only passing test results.
- `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform EditMode` and `powershell -ExecutionPolicy Bypass -File scripts/test-unity-package.ps1 -TestPlatform PlayMode` were both rerun after implementation. Both remain blocked by the same missing Unity 2021.3 editor prerequisite, before test compilation or execution.

## Scope exception

The confirmed Task 5 launcher contract adds an optional quoted `--profile` argument and requires quoted `--data-root` plus a scoped Pipe. The pre-existing compatibility assertion prohibited `--profile`, contradicting that contract. Parent approval expanded this task only to update `tests/Radar.Unity.Compatibility.Tests/PackageIdentityTests.cs` so it verifies the scoped Pipe and required argument construction instead.
