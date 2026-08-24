using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radar.Unity.Compatibility.Tests;

public sealed class PackageIdentityTests
{
    [Fact]
    public void InteractionPackage_OwnsTheOnlyUnityRuntimeAndRadarCompatibilityDelegates()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.interaction");
        var requiredFiles = new[]
        {
            Path.Combine("Runtime", "InteractionInputModule.cs"),
            Path.Combine("Runtime", "InteractionCameraRouter.cs"),
            Path.Combine("Runtime", "InteractionBridgeLauncher.cs"),
            Path.Combine("Runtime", "InteractionRuntimeSettings.cs"),
            Path.Combine("Editor", "Blaze.Interaction.Editor.asmdef"),
            Path.Combine("Editor", "InteractionSettingsProvider.cs"),
            Path.Combine("Editor", "InteractionBuildProcessor.cs"),
            Path.Combine("Editor", "InteractionSceneSetupMenu.cs"),
            Path.Combine("Compatibility", "Radar", "Blaze.Radar.Compatibility.asmdef"),
            Path.Combine("Samples~", "BasicInteraction", "BasicInteraction.unity"),
            Path.Combine("Samples~", "MultiSurfaceRouting", "MultiSurfaceRouting.unity")
        };

        Assert.All(requiredFiles, relativePath =>
            Assert.True(File.Exists(Path.Combine(packageRoot, relativePath)), relativePath));
        Assert.False(Directory.Exists(Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.radar")));
    }

    [Fact]
    public void PackageIdentity_VersionAndSamplesAreProviderNeutral()
    {
        var packageRoot = PackageRoot();
        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageRoot, "package.json")));
        var root = packageJson.RootElement;
        Assert.Equal("com.blaze.interaction", root.GetProperty("name").GetString());
        Assert.Equal("1.0.0", root.GetProperty("version").GetString());
        Assert.Equal("2021.3", root.GetProperty("unity").GetString());
        Assert.Equal(
            new[] { "Samples~/BasicInteraction", "Samples~/MultiSurfaceRouting" },
            root.GetProperty("samples").EnumerateArray()
                .Select(sample => sample.GetProperty("path").GetString()).ToArray());

        foreach (var sample in new[] { "BasicInteraction", "MultiSurfaceRouting" })
        {
            var sampleRoot = Path.Combine(packageRoot, "Samples~", sample);
            var source = string.Join(Environment.NewLine,
                Directory.EnumerateFiles(sampleRoot, "*.cs").Select(File.ReadAllText));
            Assert.Contains("Blaze.Interaction", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Blaze.Radar", source, StringComparison.Ordinal);
            Assert.StartsWith("%YAML 1.1", File.ReadAllText(Path.Combine(sampleRoot, sample + ".unity")));
        }

        var routingSource = File.ReadAllText(Path.Combine(
            packageRoot,
            "Samples~",
            "MultiSurfaceRouting",
            "MultiSurfaceRoutingPresenter.cs"));
        Assert.Contains("InteractionManager.Instance.Surfaces", routingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CeilToInt(point.PixelPosition", routingSource, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicInteractionSample_RendersStandardPointsAsAProviderNeutralCursor()
    {
        var source = File.ReadAllText(Path.Combine(
            PackageRoot(),
            "Samples~",
            "BasicInteraction",
            "BasicInteractionPresenter.cs"));

        Assert.Contains("EnsureCursor", source, StringComparison.Ordinal);
        Assert.Contains("UpdateCursor", source, StringComparison.Ordinal);
        Assert.Contains("InteractionManager.Instance.Points", source, StringComparison.Ordinal);
        Assert.Contains("cursorImage.raycastTarget = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CameraVision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NamedPipe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BasicInteractionScene_WiresItsVisibleInteractionDiagnostics()
    {
        var sampleRoot = Path.Combine(PackageRoot(), "Samples~", "BasicInteraction");
        var presenterMetaPath = Path.Combine(sampleRoot, "BasicInteractionPresenter.cs.meta");
        Assert.True(File.Exists(presenterMetaPath), "BasicInteractionPresenter.cs.meta");

        var presenterGuid = File.ReadLines(presenterMetaPath)
            .Select(line => line.Trim())
            .Single(line => line.StartsWith("guid: ", StringComparison.Ordinal))["guid: ".Length..];
        var scene = File.ReadAllText(Path.Combine(sampleRoot, "BasicInteraction.unity"));
        var presenter = FindYamlObjectByScriptGuid(scene, presenterGuid);
        var statusReference = Regex.Match(
            presenter,
            @"^  statusText: \{fileID: (?<fileId>\d+)\}$",
            RegexOptions.Multiline);

        Assert.True(statusReference.Success, "BasicInteractionPresenter.statusText must be serialized.");
        Assert.NotEqual("0", statusReference.Groups["fileId"].Value);

        var statusText = FindYamlObjectByAnchor(scene, "114", statusReference.Groups["fileId"].Value);
        Assert.Contains(
            "m_Script: {fileID: 11500000, guid: 5f7201a12d95ffc409449d95f23cf332, type: 3}",
            statusText,
            StringComparison.Ordinal);
        Assert.Contains("m_Text: IPC: DISCONNECTED", statusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Runtime_IsProtocolOneProviderNeutralAndHasNoCameraHandArtifacts()
    {
        var runtimeRoot = Path.Combine(PackageRoot(), "Runtime");
        var source = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("public const int Version = 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Blaze.Provider", source, StringComparison.Ordinal);
        Assert.DoesNotContain("using Blaze.Radar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("namespace Blaze.Radar", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CameraHand", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RadarCompatibility_DelegatesWithoutSecondPipeEventSystemOrProcessImplementation()
    {
        var compatibilityRoot = Path.Combine(PackageRoot(), "Compatibility", "Radar");
        var source = string.Join(Environment.NewLine,
            Directory.EnumerateFiles(compatibilityRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("NamedPipeClientStream", source, StringComparison.Ordinal);
        Assert.DoesNotContain("class RadarPipeClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", source, StringComparison.Ordinal);
        Assert.Contains("RadarBridgeLauncher : InteractionBridgeLauncher", source, StringComparison.Ordinal);
        Assert.Contains("RadarInputModule : InteractionInputModule", source, StringComparison.Ordinal);
        Assert.Contains("InteractionManager.Instance", source, StringComparison.Ordinal);
        Assert.Contains("manager.FrameReceived += OnFrame", source, StringComparison.Ordinal);
        Assert.Contains("surface.LogicalWidth", source, StringComparison.Ordinal);
        Assert.Contains("sequence = frame.Sequence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mathf.CeilToInt(point.PixelPosition", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_UsesSupportedInteractionBridgeArgumentsAndInheritedUnityLifecycle()
    {
        var source = File.ReadAllText(Path.Combine(
            PackageRoot(),
            "Runtime",
            "InteractionBridgeLauncher.cs"));

        Assert.Contains("protected virtual async void Awake()", source, StringComparison.Ordinal);
        Assert.Contains("protected virtual void Update()", source, StringComparison.Ordinal);
        Assert.Contains("protected virtual async void OnApplicationQuit()", source, StringComparison.Ordinal);
        Assert.Contains("--pipe-name", source, StringComparison.Ordinal);
        Assert.Contains("InteractionProjectScopeResolver.Resolve", source, StringComparison.Ordinal);
        Assert.Contains("projectScope.PipeName", source, StringComparison.Ordinal);
        Assert.Contains("\" --data-root \" + QuoteArgument(projectScope.DataRoot)", source, StringComparison.Ordinal);
        Assert.Contains("\" --profile \" + QuoteArgument(projectScope.ProfilePath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndRunner_UseCurrentInteractionPackageAndVerifiedPayload()
    {
        var repositoryRoot = FindRepositoryRoot();
        var buildSource = File.ReadAllText(Path.Combine(PackageRoot(), "Editor", "InteractionBuildProcessor.cs"));
        Assert.Contains("PackageInfo.FindForAssembly", buildSource, StringComparison.Ordinal);
        Assert.Contains("InteractionBridgePayloadValidator.Validate", buildSource, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(destinationDirectory, true)", buildSource, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256", buildSource, StringComparison.Ordinal);

        var runner = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "test-unity-package.ps1"));
        Assert.Contains("com.blaze.interaction", runner, StringComparison.Ordinal);
        Assert.Contains("\"testables\": [\"com.blaze.interaction\"]", runner, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.Process]::Start", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("com.blaze.radar\": \"file:", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageDependencies_PinUnity2021CompatibleInputAndJsonPackages()
    {
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(PackageRoot(), "package.json")));
        var dependencies = package.RootElement.GetProperty("dependencies");

        Assert.Equal("1.0.0", dependencies.GetProperty("com.unity.ugui").GetString());
        Assert.Equal("3.0.2", dependencies.GetProperty("com.unity.nuget.newtonsoft-json").GetString());
    }

    [Fact]
    public void AssemblyDefinitionsKeepRuntimeProviderNeutralAndCompatibilityOneWay()
    {
        var runtime = File.ReadAllText(Path.Combine(PackageRoot(), "Runtime", "Blaze.Interaction.Runtime.asmdef"));
        var compatibility = File.ReadAllText(Path.Combine(
            PackageRoot(), "Compatibility", "Radar", "Blaze.Radar.Compatibility.asmdef"));
        var editor = File.ReadAllText(Path.Combine(PackageRoot(), "Editor", "Blaze.Interaction.Editor.asmdef"));

        Assert.DoesNotContain("Blaze.Radar", runtime, StringComparison.Ordinal);
        Assert.Contains("Blaze.Interaction.Runtime", compatibility, StringComparison.Ordinal);
        Assert.Contains("\"Editor\"", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void RadarCompatibilityTypesAreExplicitlyObsoleteMigrationAdapters()
    {
        var source = File.ReadAllText(Path.Combine(
            PackageRoot(), "Compatibility", "Radar", "RadarCompatibility.cs"));

        Assert.Contains("[Obsolete(", source, StringComparison.Ordinal);
        Assert.Contains("Use InteractionBridgeLauncher", source, StringComparison.Ordinal);
        Assert.Contains("Use InteractionInputModule", source, StringComparison.Ordinal);
        Assert.Contains("Use InteractionManager", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SamplesContainOneRuntimeAndOneEventSystemPerScene()
    {
        foreach (var sample in new[] { "BasicInteraction", "MultiSurfaceRouting" })
        {
            var scene = File.ReadAllText(Path.Combine(
                PackageRoot(), "Samples~", sample, sample + ".unity"));
            Assert.Equal(1, Count(scene, "m_Name: Blaze Interaction Runtime"));
            Assert.Equal(1, Count(scene, "m_Name: EventSystem"));
        }
    }

    [Fact]
    public void EmbeddedPayloadContainsOneHostExecutableAndBothExternalProviders()
    {
        var bridge = Path.Combine(PackageRoot(), "Bridge~", "win-x64");
        Assert.Equal(
            "BlazeInteractionBridge.exe",
            Path.GetFileName(Assert.Single(Directory.EnumerateFiles(bridge, "*.exe", SearchOption.AllDirectories))));
        Assert.True(File.Exists(Path.Combine(bridge, "Providers", "Radar", "provider.json")));
        Assert.True(File.Exists(Path.Combine(bridge, "Providers", "CameraVision", "provider.json")));
        Assert.True(File.Exists(Path.Combine(
            bridge,
            "Providers",
            "CameraVision",
            "Blaze.Provider.CameraVision.dll")));
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(bridge, "Providers", "CameraVision"),
                "*.dll",
                SearchOption.AllDirectories),
            path => string.Equals(
                Path.GetFileName(path),
                "OpenCvSharpExtern.dll",
                StringComparison.OrdinalIgnoreCase));
    }

    private static string PackageRoot() =>
        Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.interaction");

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindYamlObjectByScriptGuid(string scene, string scriptGuid)
    {
        var marker = "m_Script: {fileID: 11500000, guid: " + scriptGuid + ", type: 3}";
        var markerIndex = scene.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Scene does not reference script GUID " + scriptGuid + ".");

        var objectStart = scene.LastIndexOf("--- !u!", markerIndex, StringComparison.Ordinal);
        var objectEnd = scene.IndexOf("--- !u!", markerIndex + marker.Length, StringComparison.Ordinal);
        return scene.Substring(objectStart, objectEnd < 0 ? scene.Length - objectStart : objectEnd - objectStart);
    }

    private static string FindYamlObjectByAnchor(string scene, string classId, string fileId)
    {
        var marker = "--- !u!" + classId + " &" + fileId;
        var objectStart = scene.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(objectStart >= 0, "Scene does not contain YAML object " + marker + ".");

        var objectEnd = scene.IndexOf("--- !u!", objectStart + marker.Length, StringComparison.Ordinal);
        return scene.Substring(objectStart, objectEnd < 0 ? scene.Length - objectStart : objectEnd - objectStart);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate BlazeInteraction.sln.");
    }
}
