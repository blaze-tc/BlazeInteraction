using System.Text.Json;

namespace Radar.Unity.Compatibility.Tests;

public sealed class EmbeddedBridgePayloadTests
{
    [Fact]
    public void PackageContainsOneSelfContainedInteractionBridgeAndExternalRadarProvider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.interaction");
        var publishDirectory = Path.Combine(packageRoot, "Bridge~", "win-x64");

        foreach (var relativePath in new[]
                 {
                     "BlazeInteractionBridge.exe",
                     "BlazeInteractionBridge.deps.json",
                     "BlazeInteractionBridge.runtimeconfig.json",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "bridge-version.txt",
                     Path.Combine("Providers", "Radar", "provider.json"),
                     Path.Combine("Providers", "Radar", "Blaze.Provider.Radar.dll")
                 })
        {
            Assert.True(File.Exists(Path.Combine(publishDirectory, relativePath)), relativePath);
        }

        Assert.Equal("BlazeInteractionBridge.exe", Path.GetFileName(Assert.Single(
            Directory.EnumerateFiles(publishDirectory, "*.exe", SearchOption.AllDirectories))));
        Assert.Equal("1.0.0", File.ReadAllText(Path.Combine(publishDirectory, "bridge-version.txt")).Trim());

        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            publishDirectory, "BlazeInteractionBridge.runtimeconfig.json")));
        Assert.True(runtimeConfig.RootElement.GetProperty("runtimeOptions").TryGetProperty(
            "includedFrameworks", out _));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            publishDirectory, "Providers", "Radar", "provider.json")));
        Assert.Equal("blaze.radar.f10f20", manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal("1.0.0", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("providerApiVersion").GetInt32());
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
