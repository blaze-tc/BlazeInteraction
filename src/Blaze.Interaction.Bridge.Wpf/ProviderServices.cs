using System.Text.RegularExpressions;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

internal sealed class BridgeInteractionHostStatus : IInteractionHostStatus
{
    private readonly object _gate = new();
    private readonly Action<InteractionHostStatus>? _beforePublish;
    private readonly Action? _beforeTerminate;
    private InteractionHostStatus _current = InteractionHostStatus.Disconnected;
    private Action<InteractionHostStatus>? _changed;
    private long _version;
    private int _terminated;
    private bool _isDraining;
    private readonly Queue<Publication> _publications = new();

    internal BridgeInteractionHostStatus(
        Action<InteractionHostStatus>? beforePublish = null,
        Action? beforeTerminate = null,
        long initialVersion = 0)
    {
        _beforePublish = beforePublish;
        _beforeTerminate = beforeTerminate;
        _version = initialVersion;
    }

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

    public event Action<InteractionHostStatus>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    public IInteractionHostStatusSubscription Subscribe(Action<InteractionHostStatus> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        lock (_gate)
        {
            _changed += changed;
            return new Subscription(this, changed, _current);
        }
    }

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

    internal void Terminate()
    {
        _beforeTerminate?.Invoke();
        var shouldDrain = false;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _terminated, 1) != 0)
            {
                return;
            }

            if (_current.IsConnected)
            {
                var value = InteractionHostStatus.Disconnected with { Version = NextVersionLocked() };
                _current = value;
                shouldDrain = EnqueueLocked(value);
            }
        }

        if (shouldDrain)
        {
            DrainPublications();
        }
    }

    private void Apply(InteractionHostStatus value)
    {
        var shouldDrain = false;
        lock (_gate)
        {
            if (Volatile.Read(ref _terminated) != 0 || StatusEquals(_current, value))
            {
                return;
            }

            var published = value with { Version = NextVersionLocked() };
            _current = published;
            shouldDrain = EnqueueLocked(published);
        }

        if (shouldDrain)
        {
            DrainPublications();
        }
    }

    private bool EnqueueLocked(InteractionHostStatus value)
    {
        _publications.Enqueue(new Publication(value, _changed));
        if (_isDraining)
        {
            return false;
        }

        _isDraining = true;
        return true;
    }

    private void DrainPublications()
    {
        while (true)
        {
            Publication publication;
            lock (_gate)
            {
                if (_publications.Count == 0)
                {
                    _isDraining = false;
                    return;
                }

                publication = _publications.Dequeue();
            }

            _beforePublish?.Invoke(publication.Value);
            if (publication.Handlers is null)
            {
                continue;
            }

            foreach (Action<InteractionHostStatus> handler in publication.Handlers.GetInvocationList())
            {
                try
                {
                    handler(publication.Value);
                }
                catch
                {
                    // A provider status subscriber cannot disrupt the Bridge IPC lifecycle.
                }
            }
        }
    }

    private void Unsubscribe(Action<InteractionHostStatus> changed)
    {
        lock (_gate)
        {
            _changed -= changed;
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

    private long NextVersionLocked() => _version = checked(_version + 1);

    private sealed record Publication(
        InteractionHostStatus Value,
        Action<InteractionHostStatus>? Handlers);

    private sealed class Subscription(
        BridgeInteractionHostStatus owner,
        Action<InteractionHostStatus> changed,
        InteractionHostStatus current) : IInteractionHostStatusSubscription
    {
        private int _disposed;
        public InteractionHostStatus Current { get; } = current;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(changed);
            }
        }
    }
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
