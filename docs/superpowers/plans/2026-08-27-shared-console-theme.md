# Shared Console Theme Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the device selector, provider header, CameraVision settings window, and Radar console use one Radar-derived WPF design system without adding a runtime presentation DLL.

**Architecture:** Store the shared ResourceDictionary once under the Bridge project and link that XAML page into the Camera and Radar assemblies at build time. Each executable/provider loads its local embedded copy through a stable pack URI, while Radar-specific styles remain in `RadarTheme.xaml`.

**Tech Stack:** .NET 8, WPF ResourceDictionary, MSBuild linked Page items, xUnit XML assertions

**Spec:** `docs/superpowers/specs/2026-08-27-console-ui-performance-stability-design.md`

## Global Constraints

- The shared source file is the only maintenance source for colors and generic controls.
- Preserve Radar's approved palette: `#08111D`, `#0E1B2B`, `#132337`, `#263B52`, `#38D3D6`, `#FBA84C`, `#EAF2FA`, `#93A8BC`, and `#F55D5B`.
- Do not change Radar data processing, configuration, IPC, or visualization behavior in this plan.
- Write a failing test before each production edit and run the complete Radar WPF suite after every Radar XAML change.

---

### Task 1: Establish the shared theme resource contract

**Files:**
- Create: `src/Blaze.Interaction.Bridge.Wpf/Resources/InteractionConsoleTheme.xaml`
- Create: `tests/Blaze.Interaction.Bridge.Wpf.Tests/InteractionConsoleThemeTests.cs`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/Blaze.Interaction.Bridge.Wpf.csproj`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj`
- Modify: `src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj`

**Interfaces:**
- Produces: embedded `InteractionConsoleTheme.xaml` in assemblies `BlazeInteractionBridge`, `Blaze.Provider.CameraVision`, and `RadarBridge`.
- Produces resource keys: `AppBackgroundBrush`, `PanelBrush`, `PanelRaisedBrush`, `BorderBrush`, `PrimaryBrush`, `AccentBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `DangerBrush`, `CardStyle`, `SectionTitleStyle`, `CaptionStyle`, and `PrimaryButtonStyle`.

- [ ] **Step 1: Write the failing resource-contract test**

```csharp
[Fact]
public void SharedTheme_DefinesApprovedPaletteAndControlStyles()
{
    var root = FindRepositoryRoot();
    var path = Path.Combine(root, "src", "Blaze.Interaction.Bridge.Wpf",
        "Resources", "InteractionConsoleTheme.xaml");
    Assert.True(File.Exists(path));
    var xaml = File.ReadAllText(path);
    foreach (var value in new[]
             {
                 "#08111D", "#0E1B2B", "#132337", "#263B52", "#38D3D6",
                 "#FBA84C", "#EAF2FA", "#93A8BC", "#F55D5B"
             })
        Assert.Contains(value, xaml, StringComparison.OrdinalIgnoreCase);
    foreach (var key in new[]
             {
                 "CardStyle", "SectionTitleStyle", "CaptionStyle", "PrimaryButtonStyle"
             })
        Assert.Contains($"x:Key=\"{key}\"", xaml, StringComparison.Ordinal);
    foreach (var target in new[]
             {
                 "Window", "Button", "ComboBox", "TextBox", "CheckBox",
                 "TabControl", "ListBox"
             })
        Assert.Contains($"TargetType=\"{target}\"", xaml, StringComparison.Ordinal);
}

private static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Unable to locate RadarControl.sln.");
}
```

- [ ] **Step 2: Run the test and verify the missing resource fails**

Run: `dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter InteractionConsoleThemeTests`

Expected: FAIL because `InteractionConsoleTheme.xaml` does not exist.

- [ ] **Step 3: Move the approved Radar dictionary into the shared source location**

Use `apply_patch` to add `src/Blaze.Interaction.Bridge.Wpf/Resources/InteractionConsoleTheme.xaml` with the complete current contents of `src/Radar.Bridge.Wpf/RadarTheme.xaml`, then remove the old file in the same patch. This preserves every existing color, brush, validation template, and control template as the initial shared dictionary. The patch targets are:

```text
Add: src/Blaze.Interaction.Bridge.Wpf/Resources/InteractionConsoleTheme.xaml
Delete: src/Radar.Bridge.Wpf/RadarTheme.xaml
```

Verify its root and first stable keys remain:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Color x:Key="AppBackgroundColor">#08111D</Color>
    <Color x:Key="PanelColor">#0E1B2B</Color>
    <Color x:Key="PanelRaisedColor">#132337</Color>
    <Color x:Key="BorderColor">#263B52</Color>
    <Color x:Key="PrimaryColor">#38D3D6</Color>
    <Color x:Key="AccentColor">#FBA84C</Color>
    <Color x:Key="TextPrimaryColor">#EAF2FA</Color>
    <Color x:Key="TextSecondaryColor">#93A8BC</Color>
    <Color x:Key="DangerColor">#F55D5B</Color>
</ResourceDictionary>
```

- [ ] **Step 4: Link the shared XAML page into Camera and Radar**

Add this item to the Camera and Radar project files; the Bridge project uses its local `Page` automatically:

```xml
<ItemGroup>
  <Page Include="..\..\..\src\Blaze.Interaction.Bridge.Wpf\Resources\InteractionConsoleTheme.xaml"
        Link="Resources\InteractionConsoleTheme.xaml" />
</ItemGroup>
```

For `src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj`, use its correct relative path:

```xml
<ItemGroup>
  <Page Include="..\Blaze.Interaction.Bridge.Wpf\Resources\InteractionConsoleTheme.xaml"
        Link="Resources\InteractionConsoleTheme.xaml" />
</ItemGroup>
```

- [ ] **Step 5: Run resource and Radar regression tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter InteractionConsoleThemeTests
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 6: Commit the shared resource contract**

```powershell
git add src/Blaze.Interaction.Bridge.Wpf/Resources/InteractionConsoleTheme.xaml `
  src/Blaze.Interaction.Bridge.Wpf/Blaze.Interaction.Bridge.Wpf.csproj `
  providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj `
  src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj `
  tests/Blaze.Interaction.Bridge.Wpf.Tests/InteractionConsoleThemeTests.cs
git commit -m "feat(ui): share console theme resources"
```

### Task 2: Load the shared theme in all three UI hosts

**Files:**
- Modify: `src/Blaze.Interaction.Bridge.Wpf/App.xaml`
- Create: `src/Radar.Bridge.Wpf/RadarTheme.xaml`
- Modify: `src/Radar.Bridge.Wpf/App.xaml`
- Modify: `providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml`
- Modify: `tests/Blaze.Interaction.Bridge.Wpf.Tests/InteractionConsoleThemeTests.cs`
- Modify: `tests/Radar.Bridge.Wpf.Tests/ThemeContrastTests.cs`
- Modify: `tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewFactoryTests.cs`

**Interfaces:**
- Consumes: embedded `Resources/InteractionConsoleTheme.xaml` from Task 1.
- Produces: application-level implicit styles in the Bridge and Radar standalone host; Camera window-level implicit styles in the provider host.

- [ ] **Step 1: Write failing pack-URI tests**

```csharp
[Theory]
[InlineData("src/Blaze.Interaction.Bridge.Wpf/App.xaml",
    "/BlazeInteractionBridge;component/Resources/InteractionConsoleTheme.xaml")]
[InlineData("src/Radar.Bridge.Wpf/RadarTheme.xaml",
    "/RadarBridge;component/Resources/InteractionConsoleTheme.xaml")]
[InlineData("providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml",
    "/Blaze.Provider.CameraVision;component/Resources/InteractionConsoleTheme.xaml")]
public void UiHost_MergesSharedTheme(string relativePath, string source)
{
    var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
        relativePath.Replace('/', Path.DirectorySeparatorChar)));
    Assert.Contains(source, xaml, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run: `dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter UiHost_MergesSharedTheme`

Expected: FAIL because the pack URIs are not present.

- [ ] **Step 3: Merge the dictionary in each host**

Bridge `App.xaml`:

```xml
<Application.Resources>
  <ResourceDictionary>
    <ResourceDictionary.MergedDictionaries>
      <ResourceDictionary Source="/BlazeInteractionBridge;component/Resources/InteractionConsoleTheme.xaml" />
    </ResourceDictionary.MergedDictionaries>
  </ResourceDictionary>
</Application.Resources>
```

Radar `RadarTheme.xaml` merges `/RadarBridge;component/Resources/InteractionConsoleTheme.xaml` before Radar-only resources. Camera window resources merge `/Blaze.Provider.CameraVision;component/Resources/InteractionConsoleTheme.xaml`.

- [ ] **Step 4: Remove the duplicate generic resources from Radar App.xaml**

Keep only Radar application resources that are not provided by the shared dictionary, including `FlexibleNumericTextConverter`. Load `RadarTheme.xaml` once rather than duplicating the palette and control templates.

- [ ] **Step 5: Run all affected UI tests**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Blaze.Provider.CameraVision.Tests/Blaze.Provider.CameraVision.Tests.csproj -c Release --filter "CameraVisionSettingsViewFactoryTests"
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
```

Expected: PASS without missing resource exceptions.

- [ ] **Step 6: Commit host theme loading**

```powershell
git add src/Blaze.Interaction.Bridge.Wpf/App.xaml src/Radar.Bridge.Wpf/App.xaml `
  src/Radar.Bridge.Wpf/RadarTheme.xaml `
  providers/CameraVision/Blaze.Provider.CameraVision/UI/CameraVisionSettingsWindow.xaml `
  tests/Blaze.Interaction.Bridge.Wpf.Tests/InteractionConsoleThemeTests.cs `
  tests/Radar.Bridge.Wpf.Tests/ThemeContrastTests.cs `
  tests/Blaze.Provider.CameraVision.Tests/CameraVisionSettingsViewFactoryTests.cs
git commit -m "refactor(ui): load shared console theme"
```

### Task 3: Restyle the provider selector and provider header

**Files:**
- Modify: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderSelectorView.xaml`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/Views/ProviderHeaderView.xaml`
- Modify: `src/Blaze.Interaction.Bridge.Wpf/BridgeWindowCoordinator.cs`
- Create: `tests/Blaze.Interaction.Bridge.Wpf.Tests/ProviderViewLayoutTests.cs`

**Interfaces:**
- Consumes: shared theme brushes and styles from Task 1.
- Produces named selector elements `ProviderChoiceList`, `ConfirmProviderButton`, and named header elements `UnityConnectionStatus`, `ProviderConnectionStatus`, `ReturnToProviderSelectionButton`.

- [ ] **Step 1: Write failing structural/style tests**

```csharp
[Fact]
public void Selector_UsesSharedCardsAndPrimaryAction()
{
    var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src",
        "Blaze.Interaction.Bridge.Wpf", "Views", "ProviderSelectorView.xaml"));
    Assert.Contains("x:Name=\"ProviderChoiceList\"", xaml);
    Assert.Contains("StaticResource CardStyle", xaml);
    Assert.Contains("x:Name=\"ConfirmProviderButton\"", xaml);
    Assert.Contains("StaticResource PrimaryButtonStyle", xaml);
    Assert.DoesNotContain("#667085", xaml, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("#D0D5DD", xaml, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void ProviderHeader_UsesNamedConnectionStatesAndSharedResources()
{
    var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src",
        "Blaze.Interaction.Bridge.Wpf", "Views", "ProviderHeaderView.xaml"));
    Assert.Contains("x:Name=\"UnityConnectionStatus\"", xaml);
    Assert.Contains("x:Name=\"ProviderConnectionStatus\"", xaml);
    Assert.Contains("DynamicResource PrimaryBrush", xaml);
    Assert.Contains("x:Name=\"ReturnToProviderSelectionButton\"", xaml);
}

private static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory is not null;
         directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            return directory.FullName;
    throw new DirectoryNotFoundException("Unable to locate RadarControl.sln.");
}
```

- [ ] **Step 2: Run and verify the layout tests fail**

Run: `dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release --filter ProviderViewLayoutTests`

Expected: FAIL on missing names and shared-resource references.

- [ ] **Step 3: Implement the dark card selector**

Use `AppBackgroundBrush` for the root, `CardStyle` for each provider choice, `TextSecondaryBrush` for category text, `DangerBrush` for errors, and `PrimaryButtonStyle` for `ConfirmProviderButton`. Preserve existing bindings and commands exactly.

- [ ] **Step 4: Implement the unified provider header**

Use `PanelBrush` and `BorderBrush` for the bar, `PrimaryBrush` for the Unity state, `TextPrimaryBrush` for provider name, and `TextSecondaryBrush` for provider state. Preserve `ReturnToSelectionCommand`.

- [ ] **Step 5: Set selector window background and sizing from shared resources**

In `WpfBridgeWindowFactory.CreateSelectorWindow`, keep the existing title and behavior, set `SizeToContent = SizeToContent.Manual`, and let the implicit Window theme supply background and foreground. Do not add hard-coded colors.

- [ ] **Step 6: Run Bridge and Radar UI suites**

Run:

```powershell
dotnet test tests/Blaze.Interaction.Bridge.Wpf.Tests/Blaze.Interaction.Bridge.Wpf.Tests.csproj -c Release
dotnet test tests/Radar.Bridge.Wpf.Tests/Radar.Bridge.Wpf.Tests.csproj -c Release
```

Expected: PASS.

- [ ] **Step 7: Commit the selector/header redesign**

```powershell
git add src/Blaze.Interaction.Bridge.Wpf/Views/ProviderSelectorView.xaml `
  src/Blaze.Interaction.Bridge.Wpf/Views/ProviderHeaderView.xaml `
  src/Blaze.Interaction.Bridge.Wpf/BridgeWindowCoordinator.cs `
  tests/Blaze.Interaction.Bridge.Wpf.Tests/ProviderViewLayoutTests.cs
git commit -m "feat(ui): align provider selection with radar console"
```
