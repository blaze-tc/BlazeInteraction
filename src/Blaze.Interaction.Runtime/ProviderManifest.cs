using System.Text.Json.Serialization;

namespace Blaze.Interaction.Runtime;

public sealed record ProviderManifest
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required int ProviderApiVersion { get; init; }

    public required string EntryAssembly { get; init; }

    public required string EntryType { get; init; }

    public required string Category { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    internal void Validate()
    {
        RequireText(Id, nameof(Id));
        RequireText(DisplayName, nameof(DisplayName));
        RequireText(EntryAssembly, nameof(EntryAssembly));
        RequireText(EntryType, nameof(EntryType));
        RequireText(Category, nameof(Category));

        if (!System.Version.TryParse(Version, out _))
        {
            throw new FormatException("Provider version must be a valid dotted version.");
        }

        if (!ProviderPathSecurity.IsSimpleFileName(EntryAssembly))
        {
            throw new FormatException("Entry assembly must be a file name inside the provider directory.");
        }

        if (!EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Entry assembly must be a DLL file.");
        }

        ArgumentNullException.ThrowIfNull(Capabilities);
        if (Capabilities.Any(string.IsNullOrWhiteSpace))
        {
            throw new FormatException("Provider capabilities cannot contain empty values.");
        }

        if (Capabilities.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Capabilities.Count)
        {
            throw new FormatException("Provider capabilities cannot contain duplicates.");
        }
    }

    private static void RequireText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"Provider manifest field '{name}' is required.");
        }
    }
}
