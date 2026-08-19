using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.TestProviders;

public sealed class ThrowingPlugin : IInteractionProviderPlugin
{
    public ThrowingPlugin()
    {
        throw new InvalidOperationException("The fixture plugin constructor failed.");
    }

    public ProviderDescriptor Descriptor => throw new NotSupportedException();
    public IProviderSettingsViewFactory? SettingsViewFactory => null;
    public IInteractionProvider CreateProvider(ProviderCreateContext context) => throw new NotSupportedException();
}
