using System.Text.RegularExpressions;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

internal sealed class BridgeInteractionHostStatus : IInteractionHostStatus
{
    private readonly object _gate = new();
    private InteractionHostStatus _current = InteractionHostStatus.Disconnected;

    public InteractionHostStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action<InteractionHostStatus>? Changed;

    internal void ApplyConnected(HelloPayload hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        Apply(new InteractionHostStatus(
            true,
            hello.UnityPid,
            hello.UnityVersion,
            FreezeSurfaces(hello.Surfaces)));
    }

    internal void ApplyDisconnected() => Apply(InteractionHostStatus.Disconnected);

    private void Apply(InteractionHostStatus value)
    {
        Action<InteractionHostStatus>? handlers;
        lock (_gate)
        {
            if (StatusEquals(_current, value))
            {
                return;
            }

            _current = value;
            handlers = Changed;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<InteractionHostStatus> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // A provider status subscriber cannot disrupt the Bridge IPC lifecycle.
            }
        }
    }

    private static IReadOnlyList<InteractionSurface> FreezeSurfaces(IReadOnlyList<InteractionSurface> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        return Array.AsReadOnly(surfaces.ToArray());
    }

    private static bool StatusEquals(InteractionHostStatus left, InteractionHostStatus right) =>
        left.IsConnected == right.IsConnected &&
        left.ProcessId == right.ProcessId &&
        string.Equals(left.ClientVersion, right.ClientVersion, StringComparison.Ordinal) &&
        left.Surfaces.SequenceEqual(right.Surfaces);
}

internal sealed class BridgeProviderStorageContext : IProviderStorageContext
{
    private static readonly Regex ProviderIdPattern = new("^[a-z0-9_-]+(?:\\.[a-z0-9_-]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex ReservedDeviceNamePattern = new(
        "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
            !ProviderIdPattern.IsMatch(providerId) ||
            ReservedDeviceNamePattern.IsMatch(providerId))
        {
            throw new ArgumentException("Provider IDs must be lowercase safe path names and cannot be Windows device names.", nameof(providerId));
        }

        var providersRoot = Path.GetFullPath(Path.Combine(DataRoot, "Providers"));
        var candidate = Path.GetFullPath(Path.Combine(providersRoot, providerId));
        if (!IsStrictChildDirectory(providersRoot, candidate))
        {
            throw new ArgumentException("The provider data directory must remain within the project Providers directory.", nameof(providerId));
        }

        return candidate;
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

}
