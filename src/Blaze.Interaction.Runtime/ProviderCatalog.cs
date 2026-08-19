using System.Text.Json;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public enum ProviderCatalogIssue
{
    MissingManifest = 0,
    InvalidManifest = 1,
    UnsupportedApiVersion = 2
}

public sealed record ProviderCatalogEntry(
    string ProviderDirectory,
    ProviderManifest? Manifest,
    ProviderCatalogIssue? Issue,
    string? Error)
{
    public bool IsAvailable => Manifest is not null && Issue is null;
}

public sealed class ProviderCatalog
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IReadOnlyList<ProviderCatalogEntry> Discover(string providersRoot)
    {
        if (string.IsNullOrWhiteSpace(providersRoot))
        {
            throw new ArgumentException("A providers root directory is required.", nameof(providersRoot));
        }

        var fullRoot = Path.GetFullPath(providersRoot);
        if (!Directory.Exists(fullRoot))
        {
            return Array.Empty<ProviderCatalogEntry>();
        }

        return Directory.EnumerateDirectories(fullRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(DiscoverDirectory)
            .ToArray();
    }

    private static ProviderCatalogEntry DiscoverDirectory(string providerDirectory)
    {
        var fullDirectory = Path.GetFullPath(providerDirectory);
        var manifestPath = Path.Combine(fullDirectory, "provider.json");
        if (!File.Exists(manifestPath))
        {
            return new ProviderCatalogEntry(
                fullDirectory,
                null,
                ProviderCatalogIssue.MissingManifest,
                "The provider directory does not contain provider.json.");
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ProviderManifest>(json, ManifestJsonOptions)
                ?? throw new JsonException("The provider manifest is empty.");
            manifest.Validate();

            if (manifest.ProviderApiVersion != ProviderApi.CurrentMajorVersion)
            {
                return new ProviderCatalogEntry(
                    fullDirectory,
                    manifest,
                    ProviderCatalogIssue.UnsupportedApiVersion,
                    $"Provider API {manifest.ProviderApiVersion} is incompatible with supported major {ProviderApi.CurrentMajorVersion}.");
            }

            return new ProviderCatalogEntry(fullDirectory, manifest, null, null);
        }
        catch (Exception exception) when (exception is JsonException
                                          or FormatException
                                          or ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            return new ProviderCatalogEntry(
                fullDirectory,
                null,
                ProviderCatalogIssue.InvalidManifest,
                exception.Message);
        }
    }
}
