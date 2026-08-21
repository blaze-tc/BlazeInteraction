using Blaze.Interaction.Internal;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePathResolverTests
{
    [Fact]
    public void ResolveEditorExecutable_UsesEmbeddedPublishWhenOverrideIsEmpty()
    {
        var packageRoot = Path.Combine("C:\\", "project", "Library", "PackageCache", "com.blaze.interaction@1.0.0");

        var executable = InteractionBridgePathResolver.ResolveEditorExecutable(string.Empty, packageRoot);

        Assert.Equal(
            Path.Combine(packageRoot, "Bridge~", "win-x64", "BlazeInteractionBridge.exe"),
            executable);
    }

    [Fact]
    public void ResolveEditorExecutable_PrefersConfiguredOverride()
    {
        var configured = Path.Combine("C:\\", "tools", "BlazeInteractionBridge.exe");

        var executable = InteractionBridgePathResolver.ResolveEditorExecutable(
            configured,
            Path.Combine("C:\\", "package"));

        Assert.Equal(Path.GetFullPath(configured), executable);
    }

    [Fact]
    public void ResolvePlayerExecutable_UsesBridgeDirectoryBesidePlayer()
    {
        var playerDirectory = Path.Combine("C:\\", "build", "InteractionGame");

        var executable = InteractionBridgePathResolver.ResolvePlayerExecutable(playerDirectory);

        Assert.Equal(
            Path.Combine(playerDirectory, "BlazeInteractionBridge", "BlazeInteractionBridge.exe"),
            executable);
    }
}
