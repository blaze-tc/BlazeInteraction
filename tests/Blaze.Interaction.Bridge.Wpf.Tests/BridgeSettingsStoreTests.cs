using System.Text;

namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class BridgeSettingsStoreTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task MissingFile_ReturnsFirstRunSettingsWithoutCreatingAFile()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);

        var settings = await store.LoadAsync(None);

        Assert.Equal(1, settings.SchemaVersion);
        Assert.Null(settings.SelectedProviderId);
        Assert.False(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSelectedProvider()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);

        await store.SaveAsync(new BridgeSettings(1, "blaze.camera.vision"), None);
        var settings = await store.LoadAsync(None);

        Assert.Equal(new BridgeSettings(1, "blaze.camera.vision"), settings);
    }

    [Fact]
    public async Task DifferentDataRootsRememberDifferentProviders()
    {
        using var rootA = new TemporaryDirectory();
        using var rootB = new TemporaryDirectory();
        var storeA = new BridgeSettingsStore(rootA.Path);
        var storeB = new BridgeSettingsStore(rootB.Path);

        await storeA.SaveAsync(new BridgeSettings(1, "blaze.radar.f10f20"), None);
        await storeB.SaveAsync(new BridgeSettings(1, "blaze.camera.vision"), None);

        Assert.Equal("blaze.radar.f10f20", (await storeA.LoadAsync(None)).SelectedProviderId);
        Assert.Equal("blaze.camera.vision", (await storeB.LoadAsync(None)).SelectedProviderId);
    }

    [Fact]
    public async Task Save_ReplacesExistingFileAndLeavesNoTemporaryFile()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);
        await store.SaveAsync(new BridgeSettings(1, "blaze.radar.f10f20"), None);

        await store.SaveAsync(new BridgeSettings(1, "blaze.camera.vision"), None);

        Assert.Equal("blaze.camera.vision", (await store.LoadAsync(None)).SelectedProviderId);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(root.Path),
            path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MalformedJson_ThrowsAndPreservesSourceBytes()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);
        Directory.CreateDirectory(root.Path);
        var malformed = Encoding.UTF8.GetBytes("{\"schemaVersion\":}");
        await File.WriteAllBytesAsync(store.SettingsPath, malformed, None);

        var error = await Assert.ThrowsAsync<BridgeSettingsException>(
            () => store.LoadAsync(None));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(malformed, await File.ReadAllBytesAsync(store.SettingsPath, None));
    }

    [Fact]
    public async Task Save_RejectsBlankProviderIdWithoutCreatingAFile()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);

        await Assert.ThrowsAsync<BridgeSettingsException>(
            () => store.SaveAsync(new BridgeSettings(1, "  "), None));

        Assert.False(File.Exists(store.SettingsPath));
    }

    [Fact]
    public async Task Load_RejectsUnsupportedSchemaWithoutChangingSource()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);
        Directory.CreateDirectory(root.Path);
        var unsupported = Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":2,\"selectedProviderId\":\"blaze.camera.vision\"}");
        await File.WriteAllBytesAsync(store.SettingsPath, unsupported, None);

        await Assert.ThrowsAsync<BridgeSettingsException>(() => store.LoadAsync(None));

        Assert.Equal(unsupported, await File.ReadAllBytesAsync(store.SettingsPath, None));
    }

    [Fact]
    public async Task Reset_ReplacesSelectionWithFirstRunSettings()
    {
        using var root = new TemporaryDirectory();
        var store = new BridgeSettingsStore(root.Path);
        await store.SaveAsync(new BridgeSettings(1, "blaze.camera.vision"), None);

        await store.ResetAsync(None);

        Assert.Equal(new BridgeSettings(1, null), await store.LoadAsync(None));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Blaze.Interaction.Bridge.Wpf.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
