using Blaze.Radar.Internal;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePathResolverTests
{
    [Fact]
    public void ResolveEditorExecutable_UsesEmbeddedPublishWhenOverrideIsEmpty()
    {
        var packageRoot = Path.Combine("C:\\", "project", "Library", "PackageCache", "com.blaze.radar@1.1.0");

        var executable = BridgePathResolver.ResolveEditorExecutable(string.Empty, packageRoot);

        Assert.Equal(
            Path.Combine(packageRoot, "Bridge~", "win-x64", "RadarBridge.exe"),
            executable);
    }

    [Fact]
    public void ResolveEditorExecutable_PrefersConfiguredOverride()
    {
        var configured = Path.Combine("C:\\", "tools", "RadarBridge.exe");

        var executable = BridgePathResolver.ResolveEditorExecutable(
            configured,
            Path.Combine("C:\\", "package"));

        Assert.Equal(Path.GetFullPath(configured), executable);
    }

    [Fact]
    public void ResolvePlayerExecutable_UsesBridgeDirectoryBesidePlayer()
    {
        var playerDirectory = Path.Combine("C:\\", "build", "RadarGame");

        var executable = BridgePathResolver.ResolvePlayerExecutable(playerDirectory);

        Assert.Equal(
            Path.Combine(playerDirectory, "RadarBridge", "RadarBridge.exe"),
            executable);
    }
}
