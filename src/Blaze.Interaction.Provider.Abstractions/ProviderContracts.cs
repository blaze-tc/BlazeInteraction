using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Provider.Abstractions;

public static class ProviderApi
{
    public const int CurrentMajorVersion = 1;
}

public sealed record ProviderDescriptor
{
    public ProviderDescriptor(
        string id,
        string displayName,
        Version version,
        string category,
        IReadOnlyList<string> capabilities)
    {
        Id = RequireText(id, nameof(id));
        DisplayName = RequireText(displayName, nameof(displayName));
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Category = RequireText(category, nameof(category));
        ArgumentNullException.ThrowIfNull(capabilities);
        Capabilities = Array.AsReadOnly(capabilities.Select(capability => RequireText(capability, nameof(capabilities))).ToArray());
    }

    public string Id { get; }
    public string DisplayName { get; }
    public Version Version { get; }
    public string Category { get; }
    public IReadOnlyList<string> Capabilities { get; }

    private static string RequireText(string? value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName)
            : value;
    }
}

public sealed record ProviderCreateContext
{
    public ProviderCreateContext(string providerDirectory, IServiceProvider services)
    {
        if (string.IsNullOrWhiteSpace(providerDirectory))
        {
            throw new ArgumentException("A provider directory is required.", nameof(providerDirectory));
        }

        ProviderDirectory = Path.GetFullPath(providerDirectory);
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public string ProviderDirectory { get; }
    public IServiceProvider Services { get; }
}

public sealed record ProviderInitializationContext
{
    public ProviderInitializationContext(
        IReadOnlyList<InteractionSurface> surfaces,
        IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        Surfaces = Array.AsReadOnly(surfaces.ToArray());
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public IReadOnlyList<InteractionSurface> Surfaces { get; }
    public IServiceProvider Services { get; }
}

public enum ProviderRuntimeStatus
{
    Created = 0,
    Initializing = 1,
    Ready = 2,
    Starting = 3,
    Running = 4,
    Stopping = 5,
    Stopped = 6,
    Faulted = 7
}

public sealed class InteractionFrameEventArgs : EventArgs
{
    public InteractionFrameEventArgs(InteractionFrame frame)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public InteractionFrame Frame { get; }
}

public sealed class ProviderStatusChangedEventArgs : EventArgs
{
    public ProviderStatusChangedEventArgs(
        ProviderRuntimeStatus previousStatus,
        ProviderRuntimeStatus status,
        Exception? error = null)
    {
        PreviousStatus = previousStatus;
        Status = status;
        Error = error;
    }

    public ProviderRuntimeStatus PreviousStatus { get; }
    public ProviderRuntimeStatus Status { get; }
    public Exception? Error { get; }
}

public interface IInteractionProviderPlugin
{
    ProviderDescriptor Descriptor { get; }

    IInteractionProvider CreateProvider(ProviderCreateContext context);

    IProviderSettingsViewFactory? SettingsViewFactory { get; }
}

public interface IInteractionProvider : IAsyncDisposable
{
    string ProviderInstanceId { get; }

    ProviderRuntimeStatus Status { get; }

    event EventHandler<InteractionFrameEventArgs>? FrameReceived;

    event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;

    Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IProviderSettingsViewFactory
{
    object CreateView(IInteractionProvider provider, IProviderSettingsContext context);
}

public interface IProviderSettingsContext
{
    ValueTask<string?> GetValueAsync(string key, CancellationToken cancellationToken);

    ValueTask SetValueAsync(string key, string? value, CancellationToken cancellationToken);
}
