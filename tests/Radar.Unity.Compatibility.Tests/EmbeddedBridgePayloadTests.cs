namespace Radar.Unity.Compatibility.Tests;

using System.Text.Json;

public sealed class EmbeddedBridgePayloadTests
{
    [Fact]
    public void PackageContainsSelfContainedBridgeAndProfiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var publishDirectory = Path.Combine(
            repositoryRoot,
            "UnityPackage",
            "com.blaze.radar",
            "Bridge~",
            "win-x64");

        AssertFileExists(publishDirectory, "RadarBridge.exe");
        AssertFileExists(publishDirectory, "RadarBridge.deps.json");
        AssertFileExists(publishDirectory, "RadarBridge.runtimeconfig.json");
        AssertFileExists(publishDirectory, "coreclr.dll");
        AssertFileExists(publishDirectory, "bridge-version.txt");
        AssertFileExists(publishDirectory, "profiles", "default-profile.json");
        AssertFileExists(publishDirectory, "profiles", "f20-profile.json");

        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "UnityPackage",
            "com.blaze.radar",
            "package.json")));
        var expectedVersion = packageJson.RootElement.GetProperty("version").GetString();
        var embeddedVersion = File.ReadAllText(Path.Combine(publishDirectory, "bridge-version.txt")).Trim();

        Assert.Equal(expectedVersion, embeddedVersion);

        foreach (var profileName in new[] { "default-profile.json", "f20-profile.json" })
        {
            using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                publishDirectory,
                "profiles",
                profileName)));
            Assert.Equal(2, profile.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    private static void AssertFileExists(params string[] pathParts)
    {
        var path = Path.Combine(pathParts);
        Assert.True(File.Exists(path), $"Expected embedded Bridge file: {path}");
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
