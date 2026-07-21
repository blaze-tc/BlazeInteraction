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
        const string expectedVersion = "1.1.5";
        Assert.Equal(expectedVersion, packageJson.RootElement.GetProperty("version").GetString());
        Assert.Equal("Blaze Radar SDK", packageJson.RootElement.GetProperty("displayName").GetString());

        var unityVersionSource = File.ReadAllText(Path.Combine(packageRoot, "Runtime", "UnitySdkVersion.cs"));
        Assert.Contains($"Value = \"{expectedVersion}\"", unityVersionSource, StringComparison.Ordinal);

        var repositoryRoot = FindRepositoryRoot();
        var bridgeCoordinatorSource = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Radar.Bridge.Wpf", "BridgeVersion.cs"));
        Assert.Contains($"Value = \"{expectedVersion}\"", bridgeCoordinatorSource, StringComparison.Ordinal);
        var mainWindow = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        Assert.Contains($"Bridge {expectedVersion}", mainWindow, StringComparison.Ordinal);

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
        Assert.Contains("bridge-version.txt", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("UnitySdkVersion.Value", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(destinationDirectory, recursive: true)", buildProcessorSource, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256", buildProcessorSource, StringComparison.Ordinal);
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
