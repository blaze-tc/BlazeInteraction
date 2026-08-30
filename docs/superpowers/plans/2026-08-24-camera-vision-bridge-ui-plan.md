# CameraVision Bridge Selection and UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a project-scoped remembered Radar/Camera selector, a return-to-selector flow, generic connection status, and the minimal CameraVision configuration/preview UI.

**Architecture:** Bridge owns provider selection and its persisted `bridge-settings.json`; providers continue to own their settings views and configuration. A Bridge window coordinator decorates existing provider windows with a generic header, avoiding a Radar UI rewrite, while CameraVision exposes a testable control facade and WPF settings window.

**Tech Stack:** .NET 8 WPF, C# 12, xUnit, Provider API 1, existing Bridge host status and project-scoped storage.

**Spec:** `docs/superpowers/specs/2026-08-24-camera-vision-hand-mvp-0-1-design.md`

## Global Constraints

- Requires successful native-backend and provider-core checkpoints.
- First launch with no selection shows the selector normally even with `--minimized`.
- Later launch selects and connects the saved provider automatically.
- Returning to selection does not stop the current provider; switch occurs only after confirmation.
- Radar settings content and configuration format are preserved.
- Provider selection is scoped to `--data-root`; no registry or global AppData setting.
- UI thread never performs Camera capture or inference.

---

### Task 1: Persist the selected provider atomically

**Files:**
- Create: `src/Blaze.Interaction.Bridge.Wpf/BridgeSettingsStore.cs`
- Create: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeSettingsStoreTests.cs`

**Interfaces:**
- Produces: `BridgeSettings(int SchemaVersion, string? SelectedProviderId)` and `BridgeSettingsStore.LoadAsync`, `SaveAsync`, `ResetAsync` at `<DataRoot>/bridge-settings.json`.

- [ ] **Step 1: Write failing store tests**

Cover missing file returns `SelectedProviderId == null`; save/load round trip; two roots are isolated; save uses atomic replacement; malformed JSON reports `BridgeSettingsException` without overwriting source; blank provider ID is rejected; schema version other than 1 is rejected.

```csharp
[Fact]
public async Task DifferentDataRootsRememberDifferentProviders()
{
    await Store(rootA).SaveAsync(new BridgeSettings(1, "blaze.radar.f10f20"), None);
    await Store(rootB).SaveAsync(new BridgeSettings(1, "blaze.camera.vision"), None);
    Assert.Equal("blaze.radar.f10f20", (await Store(rootA).LoadAsync(None)).SelectedProviderId);
    Assert.Equal("blaze.camera.vision", (await Store(rootB).LoadAsync(None)).SelectedProviderId);
}
```

- [ ] **Step 2: Run focused tests and observe compile failure**

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter BridgeSettingsStoreTests --nologo
```

- [ ] **Step 3: Implement UTF-8 atomic JSON storage**

Use sibling temporary file, flush, and overwrite move. Do not reuse `BridgeProviderSettingsContext` as persistence.

- [ ] **Step 4: Run the entire Bridge WPF test project and commit**

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --nologo
git add src/Blaze.Interaction.Bridge.Wpf/BridgeSettingsStore.cs tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeSettingsStoreTests.cs
git commit -m "feat(bridge): persist selected provider per project"
```

### Task 2: Expose provider choices and pre-Hello selection safely

**Files:**
- Modify: `src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs`
- Modify: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs`

**Interfaces:**
- Produces: immutable `AvailableProviders` entries with Provider ID, instance ID, display name, category, and availability.
- Produces: `SelectProviderAsync(string providerId, CancellationToken)` that persists intent before Hello and switches when topology exists.
- Produces: UI events/snapshots for active provider, provider runtime status, and authenticated Unity status.

- [ ] **Step 1: Add failing host tests**

Assert selection before Hello changes the provider used by the first Hello; selection after Hello calls one safe switch; same-provider selection is idempotent; unknown/unavailable IDs fail without changing current selection; cancel-before-confirm does not call selection; ProviderChanged ordering remains Cancel then change; host status reports only authenticated Unity connection.

- [ ] **Step 2: Run the new host tests and verify failure**

- [ ] **Step 3: Replace readonly default selection with guarded preferred selection**

Resolve public Provider ID to its registered instance ID. Mutate selection under `_helloGate`; when `_initializationContext` is null only record intent, otherwise call `ProviderManager.SwitchAsync` and flush outbound messages. Do not bypass existing reliable-cancel behavior.

- [ ] **Step 4: Run all Bridge and Runtime tests**

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --nologo
dotnet test tests/Blaze.Interaction.Runtime.Tests/Blaze.Interaction.Runtime.Tests.csproj -c Release --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add src/Blaze.Interaction.Bridge.Wpf/BridgeHost.cs tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeHostTests.cs
git commit -m "feat(bridge): select providers before or after Unity hello"
```

### Task 3: Implement a testable provider selector view model

**Files:**
- Create: `src/Blaze.Interaction.Bridge.Wpf/ViewModels/ProviderSelectorViewModel.cs`
- Create: `src/Blaze.Interaction.Bridge.Wpf/ViewModels/ProviderChoiceViewModel.cs`
- Create: `tests/Blaze.Interaction.Bridge.Wpf.Tests/ProviderSelectorViewModelTests.cs`

**Interfaces:**
- Produces: `Choices`, `SelectedChoice`, `ConfirmCommand`, `CancelCommand`, `CanCancel`, `ErrorMessage`, and `IsBusy`.
- Consumes: an `IBridgeProviderSelection` facade implemented by `BridgeHost`/coordinator.

- [ ] **Step 1: Write failing first-run and return-flow tests**

Assert Radar and Camera descriptors display as sensing modes without type checks; no saved choice means confirmation required; saved available provider preselects; missing saved provider returns to first-run mode with an error; first-run cannot cancel; return flow can cancel; double-click/command reentry cannot trigger two switches; failed selection leaves current provider active.

- [ ] **Step 2: Run focused tests and observe compile failure**

- [ ] **Step 3: Implement commands with serialized async execution**

Use a single in-flight operation and marshal property changes through an injected dispatcher abstraction. Do not block with `.Result` or `.Wait()`.

- [ ] **Step 4: Run Bridge tests and commit**

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --nologo
git add src/Blaze.Interaction.Bridge.Wpf/ViewModels tests/Blaze.Interaction.Bridge.Wpf.Tests/ProviderSelectorViewModelTests.cs
git commit -m "feat(bridge): add sensing device selector model"
```

### Task 4: Build the generic Bridge shell and preserve Radar settings

**Files:**
- Create: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderSelectorView.xaml`
- Create: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderSelectorView.xaml.cs`
- Create: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderHeaderView.xaml`
- Create: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderHeaderView.xaml.cs`
- Create: `src/Blaze.Interaction.Bridge.Wpf/BridgeWindowCoordinator.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/App.xaml.cs`
- Create: `tests/Blaze.Interaction.Bridge.Wpf.Tests/BridgeWindowCoordinatorTests.cs`
- Modify: `tests/Blaze.Provider.Radar.Ui.Tests/RadarSettingsViewFactoryTests.cs`

**Interfaces:**
- Produces: selector window; provider window decoration; `ReturnToSelectionCommand`; Bridge-owned Unity/provider status header.

- [ ] **Step 1: Write failing navigation/lifecycle tests**

Use fake `Window`/content adapters rather than showing real windows. Assert first-run selector overrides minimized; saved provider respects minimized; provider content is wrapped once; returning hides settings but does not stop provider; cancelling restores the same settings instance; confirmed switch closes old view without app shutdown; user X closes Bridge; Radar factory still returns its original settings window type/content.

- [ ] **Step 2: Run Bridge and Radar UI tests and verify failures**

- [ ] **Step 3: Implement window coordination without reparenting Radar internals unnecessarily**

Decorate a provider `Window` by replacing its root content with a Bridge-owned `DockPanel` whose top child is `ProviderHeaderView` and whose remaining child is the original content. Keep the original provider window and code-behind alive. Track navigation-triggered close separately from user-triggered close.

- [ ] **Step 4: Update startup flow**

Load `BridgeSettingsStore` before choosing `PreferredProviderId`. When no selection exists, show selector normally. Persist a confirmed choice, then let first Hello start it or switch immediately if Hello already occurred. Surface discovery/config errors in the window instead of reverting to the placeholder Bridge text.

- [ ] **Step 5: Run complete Bridge, Radar UI, and Radar hard gate**

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --nologo
dotnet test tests/Blaze.Provider.Radar.Ui.Tests/Blaze.Provider.Radar.Ui.Tests.csproj -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: all tests PASS; Radar remains 447/447.

- [ ] **Step 6: Commit**

```powershell
git add src/Blaze.Interaction.Bridge.Wpf tests/Blaze.Interaction.Bridge.Wpf.Tests tests/Blaze.Provider.Radar.Ui.Tests
git commit -m "feat(bridge): add provider selection shell"
```

### Task 5: Expose a CameraVision control facade and status snapshots

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/ICameraVisionControl.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionStatusSnapshot.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionControlTests.cs`

**Interfaces:**
- Produces: current configuration, device list, preview/status event, `ApplyAsync`, `ReconnectAsync`, `SetCalibrationPointAsync`, and `ResetCalibrationAsync`.

- [ ] **Step 1: Write failing facade tests**

Assert apply validates before saving; valid apply saves then restarts only affected resources; failed restart reports error while preserving saved/visible state consistently; calibration click converts preview coordinates to camera pixels and saves immediately; reset removes only selected surface calibration; status snapshot contains Camera/Inference/Output FPS, latency, detected count, coordinates, dropped frames, and Unity status.

- [ ] **Step 2: Run focused tests and observe compile failure**

- [ ] **Step 3: Implement serialized control operations**

Protect apply/reconnect/calibration with one async lifecycle gate shared with provider start/stop. Publish immutable snapshots and isolate observer exceptions.

- [ ] **Step 4: Run Camera tests and commit**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
git add providers/CameraVision/Blaze.Provider.CameraVision/UI providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionProvider.cs tests/Blaze.Provider.CameraVision.Tests/CameraVisionControlTests.cs
git commit -m "feat(camera): expose settings and preview control"
```

### Task 6: Implement the CameraVision settings view model

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewModel.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraPreviewOverlayBuilder.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewModelTests.cs`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraPreviewOverlayBuilderTests.cs`

**Interfaces:**
- Produces bindable Camera, Hand, Calibration, Smoothing, Preview, and Status properties plus async Apply/Reconnect/Reset commands.

- [ ] **Step 1: Write failing binding-model tests**

Assert device/resolution/FPS/mirror/rotation round trip; MaxHands accepts 1, 8, and 32 but rejects nonpositive input; no handedness property/label exists; tracking mode toggles; EMA/threshold validation; Apply busy/error state; snapshot updates are dispatcher-marshalled; dispose unsubscribes.

- [ ] **Step 2: Write failing overlay tests**

Assert every valid hand produces 21 joints and the fixed 21 MediaPipe bone segments, tracking point and polygon; eight hands are all retained; track colors are deterministic; invalid extensions never crash; overlays scale from camera frame pixels to preview viewport.

- [ ] **Step 3: Run both test filters and observe compile failure**

- [ ] **Step 4: Implement the view model and pure overlay builder**

Keep image conversion and overlay geometry out of XAML code-behind. Throttle UI preview updates by replacing a pending snapshot, never queueing them.

- [ ] **Step 5: Run Camera tests and commit**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
git add providers/CameraVision/Blaze.Provider.CameraVision/UI tests/Blaze.Provider.CameraVision.Tests
git commit -m "feat(camera): add settings view model and overlays"
```

### Task 7: Build the minimal CameraVision WPF window

**Files:**
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml.cs`
- Create: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsViewFactory.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/CameraVisionPlugin.cs`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj`
- Create: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewFactoryTests.cs`

**Interfaces:**
- Produces: non-null `CameraVisionPlugin.SettingsViewFactory` returning a provider-owned `Window` suitable for Bridge decoration.

- [ ] **Step 1: Write failing factory and XAML contract tests**

Assert factory rejects wrong provider, returns a window for CameraVision, disposes the view model on close, and XAML contains every required control/status label but no handedness label.

- [ ] **Step 2: Run focused tests and observe current null factory failure**

- [ ] **Step 3: Enable WPF and implement the test-focused layout**

Use a two-column layout: preview/overlay on the left; scrollable grouped controls/status on the right. Calibration buttons arm P1/P2/P3/P4, then consume the next preview click. Apply is explicit; calibration saves immediately.

- [ ] **Step 4: Run Camera, Bridge, and Radar UI tests**

```powershell
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --nologo
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --nologo
dotnet test tests/Blaze.Provider.Radar.Ui.Tests/Blaze.Provider.Radar.Ui.Tests.csproj -c Release --nologo
```

- [ ] **Step 5: Commit**

```powershell
git add providers/CameraVision/Blaze.Provider.CameraVision tests/Blaze.Provider.CameraVision.Tests
git commit -m "feat(camera): add hand camera control window"
```

### Task 8: Live Bridge UI checkpoint

**Files:** None unless validation exposes a defect.

- [ ] **Step 1: Publish a temporary Bridge and run it with a fresh data root**

```powershell
$testRoot = Join-Path $env:TEMP ('BlazeCameraUi-' + [guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $testRoot 'publish'
powershell -ExecutionPolicy Bypass -File scripts/publish-interaction-bridge.ps1 -OutputDirectory $publishRoot
& (Join-Path $publishRoot 'BlazeInteractionBridge.exe') --data-root (Join-Path $testRoot 'data')
```

Verify selector is visible, Camera can be selected, return/cancel works, and `bridge-settings.json` is created under the temporary data root. Close the Bridge before removing the temporary directory.

- [ ] **Step 2: Repeat with the saved data root**

Verify Camera auto-selects. Switch to Radar and confirm the existing Radar control appears with the Bridge header and its existing project-scoped configuration.

- [ ] **Step 3: Run complete automated regression**

```powershell
dotnet test BlazeInteraction.sln -c Release --nologo
powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

- [ ] **Step 4: Confirm clean checkpoint**

```powershell
git status --short
```

Expected: clean. Then begin `2026-08-24-camera-vision-unity-release-plan.md`.
