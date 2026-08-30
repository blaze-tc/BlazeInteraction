using System.Text.Json;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public enum ProviderCatalogIssue
{
    MissingManifest = 0,
    InvalidManifest = 1,
    UnsupportedApiVersion = 2,
    DuplicateProviderId = 3,
    ManifestTooLarge = 4,
    UnsafeProviderPath = 5
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
    public const int MaximumManifestBytes = 64 * 1024;

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

        var entries = Directory.EnumerateDirectories(fullRoot)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Select(DiscoverDirectory)
            .ToArray();

        var duplicateIds = entries
            .Where(entry => entry.IsAvailable)
            .GroupBy(entry => entry.Manifest!.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (duplicateIds.Count == 0)
        {
            return entries;
        }

        return entries.Select(entry =>
        {
            if (entry.Manifest is null || !duplicateIds.Contains(entry.Manifest.Id))
            {
                return entry;
            }

            return entry with
            {
                Issue = ProviderCatalogIssue.DuplicateProviderId,
                Error = "The provider identifier is duplicated."
            };
        }).ToArray();
    }

    private static ProviderCatalogEntry DiscoverDirectory(string providerDirectory)
    {
        var fullDirectory = Path.GetFullPath(providerDirectory);
        if (!ProviderPathSecurity.TryGetSafeProviderRoot(fullDirectory, out _))
        {
            return new ProviderCatalogEntry(
                fullDirectory,
                null,
                ProviderCatalogIssue.UnsafeProviderPath,
                "The provider directory is not a safe local directory.");
        }

        var manifestPath = Path.Combine(fullDirectory, "provider.json");
        if (!File.Exists(manifestPath))
        {
            return new ProviderCatalogEntry(
                fullDirectory,
                null,
                ProviderCatalogIssue.MissingManifest,
                "The provider directory does not contain provider.json.");
        }

        if (!ProviderPathSecurity.TryResolveContainedFile(fullDirectory, manifestPath, out var resolvedManifestPath))
        {
            return new ProviderCatalogEntry(
                fullDirectory,
                null,
                ProviderCatalogIssue.UnsafeProviderPath,
                "The provider manifest is not a safe provider-local file.");
        }

        try
        {
            var manifestBytes = ReadManifestBytes(resolvedManifestPath);
            using var document = JsonDocument.Parse(manifestBytes);
            RejectDuplicateTopLevelFields(document.RootElement);
            var manifest = JsonSerializer.Deserialize<ProviderManifest>(manifestBytes, ManifestJsonOptions)
                ?? throw new JsonException("The provider manifest is empty.");
            manifest.Validate();
            manifest = manifest with
            {
                Capabilities = Array.AsReadOnly(manifest.Capabilities.ToArray())
            };

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
                exception is ManifestTooLargeException
                    ? ProviderCatalogIssue.ManifestTooLarge
                    : ProviderCatalogIssue.InvalidManifest,
                exception is ManifestTooLargeException
                    ? "The provider manifest exceeds the size limit."
                    : "The provider manifest is invalid.");
        }
    }

    private static byte[] ReadManifestBytes(string manifestPath)
    {
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumManifestBytes)
        {
            throw new ManifestTooLargeException();
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void RejectDuplicateTopLevelFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A provider manifest must be a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException("A provider manifest contains a duplicate top-level field.");
            }
        }
    }

    private sealed class ManifestTooLargeException : IOException;
}
