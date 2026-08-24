using System.Text.RegularExpressions;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

internal sealed class BridgeProviderStorageContext : IProviderStorageContext
{
    private static readonly Regex ProviderIdPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    internal BridgeProviderStorageContext(string dataRoot, string? profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
        ProfilePath = string.IsNullOrWhiteSpace(profilePath) ? null : Path.GetFullPath(profilePath);
    }

    public string DataRoot { get; }
    public string? ProfilePath { get; }

    public string GetProviderDataDirectory(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) ||
            providerId is "." or ".." ||
            !ProviderIdPattern.IsMatch(providerId))
        {
            throw new ArgumentException("Provider IDs may contain only letters, digits, '.', '_' and '-'.", nameof(providerId));
        }

        var providersRoot = Path.GetFullPath(Path.Combine(DataRoot, "Providers"));
        var candidate = Path.GetFullPath(Path.Combine(providersRoot, providerId));
        if (!IsStrictChildDirectory(providersRoot, candidate))
        {
            throw new ArgumentException("The provider data directory must remain within the project Providers directory.", nameof(providerId));
        }

        return candidate;
    }
}

internal sealed class BridgeServiceProvider : IServiceProvider
{
    private readonly IReadOnlyDictionary<Type, object> _services;

    internal BridgeServiceProvider(IEnumerable<object> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var registered = new Dictionary<Type, object>();
        foreach (var service in services)
        {
            ArgumentNullException.ThrowIfNull(service);
            Register(registered, service.GetType(), service);
            foreach (var serviceInterface in service.GetType().GetInterfaces())
            {
                Register(registered, serviceInterface, service);
            }
        }

        _services = registered;
    }

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return _services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private static void Register(IDictionary<Type, object> services, Type type, object service)
    {
        if (services.TryGetValue(type, out var existing) && !ReferenceEquals(existing, service))
        {
            throw new ArgumentException($"A service is already registered for '{type.FullName}'.", nameof(services));
        }

        services[type] = service;
    }

    private static bool IsStrictChildDirectory(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return !string.Equals(relative, ".", StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
