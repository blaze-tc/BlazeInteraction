using System.Reflection;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.TestProviders;

public sealed class ValidPlugin : IInteractionProviderPlugin
{
    public ProviderDescriptor Descriptor { get; } = new(
        "blaze.test.valid",
        "Valid test provider",
        new Version(1, 2, 3),
        "Test",
        ["interaction-point", "diagnostics"]);

    public IProviderSettingsViewFactory? SettingsViewFactory => null;

    public IInteractionProvider CreateProvider(ProviderCreateContext context) => new ValidProvider();

    public Assembly GetContractsAssembly() => typeof(InteractionFrame).Assembly;

    public Assembly GetAbstractionsAssembly() => typeof(IInteractionProviderPlugin).Assembly;
}

internal sealed class ValidProvider : IInteractionProvider
{
    public string ProviderInstanceId => "valid-instance";
    public ProviderRuntimeStatus Status => ProviderRuntimeStatus.Created;
    public event EventHandler<InteractionFrameEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }
    public Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
