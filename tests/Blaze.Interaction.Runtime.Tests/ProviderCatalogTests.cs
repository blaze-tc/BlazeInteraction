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
}
