using System.Text.Json;

namespace Radar.Unity.Compatibility.Tests;

public sealed class PackageIdentityTests
{
    [Fact]
    public void Package_UsesBlazeIdentityAndExplicitSampleAssemblyReference()
    {
        var packageRoot = Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.radar");
        Assert.True(Directory.Exists(packageRoot), $"Expected renamed package directory: {packageRoot}");

        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        Assert.Equal("com.blaze.radar", packageJson.RootElement.GetProperty("name").GetString());
        const string expectedReleaseVersion = "1.2.1";
        Assert.Equal(expectedReleaseVersion, packageJson.RootElement.GetProperty("version").GetString());
        Assert.Equal("Blaze Radar SDK", packageJson.RootElement.GetProperty("displayName").GetString());

        var unityVersionSource = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "UnitySdkVersion.cs"));
        Assert.Contains($"Value = \"{expectedReleaseVersion}\"", unityVersionSource, StringComparison.Ordinal);

        var repositoryRoot = FindRepositoryRoot();
        var bridgeCoordinatorSource = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Radar.Bridge.Wpf", "BridgeVersion.cs"));
        Assert.Contains($"Value = \"{expectedReleaseVersion}\"", bridgeCoordinatorSource, StringComparison.Ordinal);
        var mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        Assert.Contains("Bridge 1.2.1 · IPC 2 · Windows x64", mainWindow, StringComparison.Ordinal);

        var bridgeProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Radar.Bridge.Wpf", "Radar.Bridge.Wpf.csproj"));
        Assert.Contains("<Version>1.2.1</Version>", bridgeProject, StringComparison.Ordinal);

        var embeddedVersion = File.ReadAllText(Path.Combine(
            packageRoot, "Bridge~", "win-x64", "bridge-version.txt")).Trim();
        Assert.Equal(expectedReleaseVersion, embeddedVersion);

        using var runtimeAssembly = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(packageRoot, "Runtime", "Blaze.Radar.Runtime.asmdef")));
        Assert.Equal("Blaze.Radar.Runtime", runtimeAssembly.RootElement.GetProperty("name").GetString());
        Assert.Equal("Blaze.Radar", runtimeAssembly.RootElement.GetProperty("rootNamespace").GetString());

        using var sampleAssembly = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(packageRoot, "Samples~", "BasicInteraction", "Blaze.Radar.Sample.BasicInteraction.asmdef")));
        var references = sampleAssembly.RootElement
            .GetProperty("references")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray();
        Assert.Contains("Blaze.Radar.Runtime", references);
        Assert.Contains("Unity.ugui", references);

        var loggerSource = File.ReadAllText(
            Path.Combine(packageRoot, "Samples~", "BasicInteraction", "RadarDemoLogger.cs"));
        Assert.Contains("RadarFrameDispatcher", loggerSource, StringComparison.Ordinal);
        Assert.Contains("maxLogEntries", loggerSource, StringComparison.Ordinal);
        Assert.Contains("frameHistoryIntervalSeconds", loggerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Yuexin.Radar.Unity", loggerSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageCSharpSources_DoNotUseLegacyUnityNamespace()
    {
        var packageRoot = Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.radar");
        Assert.True(Directory.Exists(packageRoot), $"Expected renamed package directory: {packageRoot}");

        var legacySources = Directory
            .EnumerateFiles(packageRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("Yuexin.Radar.Unity", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(packageRoot, path))
            .ToArray();

        Assert.Empty(legacySources);
    }

    [Fact]
    public void BasicInteractionSample_UsesAnAuthoredSceneAndPersistentUguiEvents()
    {
        var sampleRoot = Path.Combine(
            FindRepositoryRoot(), "UnityPackage", "com.blaze.radar", "Samples~", "BasicInteraction");
        var scripts = Directory.EnumerateFiles(sampleRoot, "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        var scene = File.ReadAllText(Path.Combine(sampleRoot, "BasicInteraction.unity"));

        Assert.All(scripts, source =>
        {
            Assert.DoesNotContain("DefaultControls", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new GameObject", source, StringComparison.Ordinal);
        });
        Assert.Contains("m_Name: Demo Canvas", scene, StringComparison.Ordinal);
        Assert.Contains("m_Name: EventSystem", scene, StringComparison.Ordinal);
        Assert.Contains("m_Name: 3D Physics Target", scene, StringComparison.Ordinal);
        Assert.Contains("m_Name: 2D Physics Target", scene, StringComparison.Ordinal);
        Assert.Contains("m_MethodName: OnRadarButtonClicked", scene, StringComparison.Ordinal);
        Assert.Contains("m_MethodName: OnToggleChanged", scene, StringComparison.Ordinal);
        Assert.Contains("m_MethodName: OnSliderChanged", scene, StringComparison.Ordinal);
        Assert.Contains("m_MethodName: OnScrollChanged", scene, StringComparison.Ordinal);
        Assert.Contains("m_MethodName: ClearLog", scene, StringComparison.Ordinal);
        Assert.Contains("m_Name: Live Frame Data", scene, StringComparison.Ordinal);
        Assert.Contains("m_Name: Detailed Event Log", scene, StringComparison.Ordinal);

        var pointerProbe = File.ReadAllText(Path.Combine(sampleRoot, "RadarPointerEventProbe.cs"));
        var dragProbe = File.ReadAllText(Path.Combine(sampleRoot, "RadarDragEventProbe.cs"));
        Assert.DoesNotContain("IDragHandler", pointerProbe, StringComparison.Ordinal);
        Assert.DoesNotContain("IScrollHandler", pointerProbe, StringComparison.Ordinal);
        Assert.Contains("IDragHandler", dragProbe, StringComparison.Ordinal);
        Assert.Contains("IScrollHandler", dragProbe, StringComparison.Ordinal);

        foreach (var scriptName in new[]
                 {
                     "BasicInteractionPresenter.cs",
                     "RadarDemoLogger.cs",
                     "RadarDragEventProbe.cs",
                     "RadarPointerEventProbe.cs",
                     "SamplePointerTarget.cs"
                 })
        {
            var guidLine = File.ReadLines(Path.Combine(sampleRoot, scriptName + ".meta"))
                .Single(line => line.StartsWith("guid: ", StringComparison.Ordinal));
            var guid = guidLine["guid: ".Length..];
            Assert.Contains($"guid: {guid}", scene, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MultiScreenSample_AutoStartsBridgeButKeepsIpcSelectionExplicit()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.radar");
        var sampleRoot = Path.Combine(packageRoot, "Samples~", "MultiScreenCameraRouting");
        var scene = File.ReadAllText(Path.Combine(sampleRoot, "MultiScreenCameraRouting.unity"));
        var settings = File.ReadAllText(Path.Combine(sampleRoot, "MultiScreenRadarSettings.asset"));
        var launcherGuid = File.ReadLines(Path.Combine(packageRoot, "Runtime", "RadarBridgeLauncher.cs.meta"))
            .Single(line => line.StartsWith("guid: ", StringComparison.Ordinal))["guid: ".Length..];
        var settingsGuid = File.ReadLines(Path.Combine(sampleRoot, "MultiScreenRadarSettings.asset.meta"))
            .Single(line => line.StartsWith("guid: ", StringComparison.Ordinal))["guid: ".Length..];

        Assert.Contains("  autoStart: 1", settings, StringComparison.Ordinal);
        Assert.Contains($"m_Script: {{fileID: 11500000, guid: {launcherGuid}, type: 3}}", scene, StringComparison.Ordinal);
        Assert.Contains($"settings: {{fileID: 11400000, guid: {settingsGuid}, type: 2}}", scene, StringComparison.Ordinal);
        Assert.Contains("  AutoStart: 1", scene, StringComparison.Ordinal);
        Assert.Contains("  autoConnect: 0", scene, StringComparison.Ordinal);
        Assert.Contains("  startMode: 0", scene, StringComparison.Ordinal);
    }

    [Fact]
    public void UnityLifecycleCallbacks_GuardBridgeStartupFailures()
    {
        var launcherSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "UnityPackage",
            "com.blaze.radar",
            "Runtime",
            "RadarBridgeLauncher.cs"));

        Assert.Contains("catch (OperationCanceledException)", launcherSource, StringComparison.Ordinal);
        Assert.Contains("catch (Exception exception)", launcherSource, StringComparison.Ordinal);
        Assert.Contains("RadarBridge startup failed", launcherSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RadarFrameDispatcher_IsolatesUnityEventSubscriberFailures()
    {
        var dispatcherSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "UnityPackage",
            "com.blaze.radar",
            "Runtime",
            "RadarFrameDispatcher.cs"));

        Assert.Contains("InvokeSafely(ConnectionChanged", dispatcherSource, StringComparison.Ordinal);
        Assert.Contains("InvokeSafely(ErrorReceived", dispatcherSource, StringComparison.Ordinal);
        Assert.Contains("InvokeSafely(PointerFrameReceived", dispatcherSource, StringComparison.Ordinal);
        Assert.Contains("GetInvocationList()", dispatcherSource, StringComparison.Ordinal);
        Assert.Contains("[Blaze Radar] IPC", dispatcherSource, StringComparison.Ordinal);
        Assert.Contains("Pointer frame", dispatcherSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerBuild_UsesCurrentPackageBridgeAndVerifiesCopiedPayload()
    {
        var buildProcessorSource = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "UnityPackage",
            "com.blaze.radar",
            "Editor",
            "RadarBuildProcessor.cs"));

        Assert.Contains("PackageInfo.FindForAssembly", buildProcessorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorPrefs.GetString", buildProcessorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BridgeSourceEditorPreference", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("BridgePayloadValidator.Validate", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("UnitySdkVersion.Value", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(destinationDirectory, recursive: true)", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256", buildProcessorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageTopology_RemainsUnity2021CompatibleAndPreservesSerializedSettings()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.radar");
        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        Assert.Equal("com.blaze.radar", packageJson.RootElement.GetProperty("name").GetString());
        Assert.Equal("1.2.1", packageJson.RootElement.GetProperty("version").GetString());
        Assert.Equal("2021.3", packageJson.RootElement.GetProperty("unity").GetString());

        var definitionSource = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "RadarScreenDefinition.cs"));
        var validatorSource = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "RadarScreenTopologyValidator.cs"));
        Assert.Contains("namespace Blaze.Radar", definitionSource, StringComparison.Ordinal);
        Assert.Contains("namespace Blaze.Radar", validatorSource, StringComparison.Ordinal);

        var settingsSource = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "RadarRuntimeSettings.cs"));
        foreach (var serializedField in new[]
                 {
                     "autoStart", "exitBridgeWithUnity", "pipeName", "editorBridgeExecutable", "profilePath",
                     "connectTimeoutMilliseconds", "reconnectDelayMilliseconds", "inputMode", "showDebugOverlay"
                 })
        {
            Assert.Contains($" {serializedField}", settingsSource, StringComparison.Ordinal);
        }

        Assert.Contains("private List<RadarScreenDefinition> screens", settingsSource, StringComparison.Ordinal);
        Assert.Contains("screenTopologySchemaVersion", settingsSource, StringComparison.Ordinal);
        Assert.Contains("ReadOnlyCollection<RadarScreenDefinition>", settingsSource, StringComparison.Ordinal);
        Assert.Contains("PrimaryScreen", settingsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocumentation_UsesTheTagged121PackageUrlWithoutLegacy11xUrls()
    {
        const string taggedUrl =
            "https://github.com/blaze-tc/RadarControl.git?path=/UnityPackage/com.blaze.radar#v1.2.1";
        var repositoryRoot = FindRepositoryRoot();
        var documentationPaths = new[]
        {
            "README.md",
            "INSTALL.md",
            Path.Combine("docs", "unity-integration.md"),
            Path.Combine("UnityPackage", "com.blaze.radar", "README.md"),
            Path.Combine("UnityPackage", "com.blaze.radar", "Documentation~", "index.md")
        };

        foreach (var relativePath in documentationPaths)
        {
            var source = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            Assert.Contains(taggedUrl, source, StringComparison.Ordinal);
            Assert.DoesNotMatch(@"RadarControl\.git\?path=/UnityPackage/com\.blaze\.radar#v1\.1\.\d+", source);
        }
    }

    [Fact]
    public void PlayerBuild_ValidatesScreenTopologyBeforeRetainingCopySafeguards()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "UnityPackage", "com.blaze.radar", "Editor", "RadarBuildProcessor.cs"));

        Assert.Contains("IPreprocessBuildWithReport", source, StringComparison.Ordinal);
        Assert.Contains("IPostprocessBuildWithReport", source, StringComparison.Ordinal);
        Assert.Contains("OnPreprocessBuild", source, StringComparison.Ordinal);
        Assert.Contains("RadarScreenTopologyValidator.Validate", source, StringComparison.Ordinal);
        Assert.Contains("BuildFailedException", source, StringComparison.Ordinal);
        Assert.Contains("PackageInfo.FindForAssembly", source, StringComparison.Ordinal);
        Assert.Contains("UnitySdkVersion.Value", source, StringComparison.Ordinal);
        Assert.Contains("BridgePayloadValidator.Validate", source, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(destinationDirectory, recursive: true)", source, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UnityPackageTestRunner_UsesSafeVersionedTemporaryProjectAndStrictFailureChecks()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "test-unity-package.ps1"));

        Assert.Contains("tmp\\unity-package-tests", source, StringComparison.Ordinal);
        Assert.Contains("com.blaze.radar\": \"file:../../../UnityPackage/com.blaze.radar", source, StringComparison.Ordinal);
        Assert.Contains("com.unity.test-framework\": \"1.1.33", source, StringComparison.Ordinal);
        Assert.Contains("com.unity.ugui\": \"1.0.0", source, StringComparison.Ordinal);
        Assert.Contains("com.unity.nuget.newtonsoft-json\": \"3.0.2", source, StringComparison.Ordinal);
        Assert.Contains("\"testables\": [\"com.blaze.radar\"]", source, StringComparison.Ordinal);
        Assert.Contains("$selectedVersion.Major -ne 2021", source, StringComparison.Ordinal);
        Assert.Contains("$selectedVersion.Minor -ne 3", source, StringComparison.Ordinal);
        Assert.Contains("Parse-UnityVersion", source, StringComparison.Ordinal);
        Assert.Contains("Select-UnityEditorVersion", source, StringComparison.Ordinal);
        Assert.Contains("ExplicitlyRequested", source, StringComparison.Ordinal);
        Assert.Contains("must expose a parseable ProductVersion", source, StringComparison.Ordinal);
        Assert.Contains("explicit editor ProductVersion rejection", source, StringComparison.Ordinal);
        Assert.Contains("2021.3.9f10", source, StringComparison.Ordinal);
        Assert.Contains("2021.3.45f1c1", source, StringComparison.Ordinal);
        Assert.Contains("2021.3.46f1", source, StringComparison.Ordinal);
        Assert.Contains("ProductVersion", source, StringComparison.Ordinal);
        Assert.Contains("$root.Name -ne \"test-run\"", source, StringComparison.Ordinal);
        Assert.Contains("test-run", source, StringComparison.Ordinal);
        Assert.Contains("GetAttribute(\"result\")", source, StringComparison.Ordinal);
        Assert.Contains("inconsistent", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("packageJson.version", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Samples/Blaze Radar SDK/1.1.5", source, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $temporaryProject", source, StringComparison.Ordinal);
        Assert.Contains("malformed test result XML", source, StringComparison.Ordinal);
        Assert.Contains("inconclusive", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("error CS", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnityEditorTopologyTests_ExerciseSerializedOperationsAndRealPrebuildValidation()
    {
        var packageRoot = Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.radar");
        var editorTestsRoot = Path.Combine(packageRoot, "Tests", "Editor");
        using var assembly = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(editorTestsRoot, "Blaze.Radar.Editor.Tests.asmdef")));
        Assert.Equal("Blaze.Radar.Editor.Tests", assembly.RootElement.GetProperty("name").GetString());
        Assert.Contains("Editor", assembly.RootElement.GetProperty("includePlatforms")
            .EnumerateArray().Select(value => value.GetString()));

        var source = File.ReadAllText(Path.Combine(editorTestsRoot, "RadarSettingsProviderEditorTests.cs"));
        Assert.Contains("new SerializedObject", source, StringComparison.Ordinal);
        Assert.Contains("AddScreen", source, StringComparison.Ordinal);
        Assert.Contains("DuplicateScreen", source, StringComparison.Ordinal);
        Assert.Contains("RemoveScreen", source, StringComparison.Ordinal);
        Assert.Contains("MoveScreen", source, StringComparison.Ordinal);
        Assert.Contains("SetPrimary", source, StringComparison.Ordinal);
        Assert.Contains("MAIN-COPY", source, StringComparison.Ordinal);
        Assert.Contains("new RadarBuildProcessor().OnPreprocessBuild(null)", source, StringComparison.Ordinal);
        Assert.Contains("AssetDatabase.MoveAsset", source, StringComparison.Ordinal);
        Assert.Contains("BuildFailedException", source, StringComparison.Ordinal);

        var providerSource = File.ReadAllText(Path.Combine(packageRoot, "Editor", "RadarSettingsProvider.cs"));
        Assert.DoesNotContain("AssetDatabase.SaveAssets()", providerSource, StringComparison.Ordinal);
        Assert.Contains("AssetDatabase.SaveAssetIfDirty(settings)", providerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AssetDatabase.SaveAssets()", source, StringComparison.Ordinal);
        Assert.Contains("AssetDatabase.SaveAssetIfDirty(testSettings)", source, StringComparison.Ordinal);
        Assert.Contains("EditorUtility.IsDirty(existingSettings)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate RadarControl.sln from the test output directory.");
    }
}
