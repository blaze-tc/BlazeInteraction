using Blaze.Interaction.Runtime;

namespace Blaze.Interaction.Runtime.Tests;

public sealed class ProviderCatalogTests
{
    [Fact]
    public void Discover_ValidManifest_ReturnsValidatedProviderMetadata()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("Radar");
        ProviderTestDirectory.WriteManifest(directory);

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.True(result.IsAvailable);
        Assert.Null(result.Issue);
        Assert.Equal("blaze.test.valid", result.Manifest!.Id);
        Assert.Equal("Valid test provider", result.Manifest.DisplayName);
        Assert.Equal(new[] { "interaction-point", "diagnostics" }, result.Manifest.Capabilities);
        Assert.Equal(Path.GetFullPath(directory), result.ProviderDirectory);
    }

    [Fact]
    public void Discover_MissingManifest_ReportsFolderWithoutHidingValidSibling()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddEmptyProvider("Broken");
        var valid = fixture.AddEmptyProvider("Valid");
        ProviderTestDirectory.WriteManifest(valid);

        var results = new ProviderCatalog().Discover(fixture.Root);

        Assert.Equal(2, results.Count);
        Assert.Equal(ProviderCatalogIssue.MissingManifest, results[0].Issue);
        Assert.True(results[1].IsAvailable);
    }

    [Fact]
    public void Discover_CorruptManifest_ReportsInvalidJson()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("Corrupt");
        File.WriteAllText(Path.Combine(directory, "provider.json"), "{ not-json }");

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.False(result.IsAvailable);
        Assert.Equal(ProviderCatalogIssue.InvalidManifest, result.Issue);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void Discover_UnsupportedApiMajor_ReportsIncompatibility()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("Future");
        ProviderTestDirectory.WriteManifest(directory, providerApiVersion: 2);

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.False(result.IsAvailable);
        Assert.Equal(ProviderCatalogIssue.UnsupportedApiVersion, result.Issue);
        Assert.Equal(2, result.Manifest!.ProviderApiVersion);
    }

    [Fact]
    public void Discover_MissingProvidersRoot_ReturnsEmptyCollection()
    {
        using var fixture = new ProviderTestDirectory();
        var missing = Path.Combine(fixture.Root, "does-not-exist");

        var results = new ProviderCatalog().Discover(missing);

        Assert.Empty(results);
    }

    [Fact]
    public void Discover_DuplicateProviderIdsIgnoringCase_MarksEveryConflictUnavailable()
    {
        using var fixture = new ProviderTestDirectory();
        var first = fixture.AddEmptyProvider("First");
        var second = fixture.AddEmptyProvider("Second");
        ProviderTestDirectory.WriteManifest(first, id: "blaze.test.Duplicate");
        ProviderTestDirectory.WriteManifest(second, id: "BLAZE.TEST.duplicate");

        var results = new ProviderCatalog().Discover(fixture.Root);

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.False(result.IsAvailable);
            Assert.Equal(ProviderCatalogIssue.DuplicateProviderId, result.Issue);
            Assert.NotNull(result.Manifest);
        });
    }

    [Fact]
    public void Discover_ManifestOverSizeLimit_IsRejectedWithoutLeakingPhysicalPath()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("Oversized");
        ProviderTestDirectory.WriteManifest(directory);
        File.AppendAllText(Path.Combine(directory, "provider.json"), new string(' ', 70 * 1024));

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.Equal(ProviderCatalogIssue.ManifestTooLarge, result.Issue);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain(fixture.Root, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_CaseInsensitiveDuplicateTopLevelField_IsRejected()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("DuplicateField");
        File.WriteAllText(
            Path.Combine(directory, "provider.json"),
            """
            {"id":"blaze.first","ID":"blaze.second","displayName":"Duplicate","version":"1.0.0","providerApiVersion":1,"entryAssembly":"Provider.dll","entryType":"Plugin","category":"Test","capabilities":[]}
            """);

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.Equal(ProviderCatalogIssue.InvalidManifest, result.Issue);
        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void Discover_UnknownUniqueTopLevelField_RemainsForwardCompatible()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("FutureField");
        File.WriteAllText(
            Path.Combine(directory, "provider.json"),
            """
            {"id":"blaze.test.future","displayName":"Future","version":"1.0.0","providerApiVersion":1,"entryAssembly":"Provider.dll","entryType":"Plugin","category":"Test","capabilities":[],"futureSetting":{"enabled":true}}
            """);

        var result = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.True(result.IsAvailable, result.Error);
    }

    [Fact]
    public void Discover_ManifestLinkOutsideProviderRoot_IsRejectedAsUnsafePath()
    {
        using var fixture = new ProviderTestDirectory();
        var outsideDirectory = fixture.AddEmptyProvider("Outside");
        ProviderTestDirectory.WriteManifest(outsideDirectory);
        var providerDirectory = fixture.AddEmptyProvider("Provider");
        File.CreateSymbolicLink(
            Path.Combine(providerDirectory, "provider.json"),
            Path.Combine(outsideDirectory, "provider.json"));

        var results = new ProviderCatalog().Discover(fixture.Root);
        var result = Assert.Single(results, entry => entry.ProviderDirectory == Path.GetFullPath(providerDirectory));

        Assert.False(result.IsAvailable);
        Assert.Equal(ProviderCatalogIssue.UnsafeProviderPath, result.Issue);
        Assert.DoesNotContain(fixture.Root, result.Error!, StringComparison.OrdinalIgnoreCase);
    }
}
