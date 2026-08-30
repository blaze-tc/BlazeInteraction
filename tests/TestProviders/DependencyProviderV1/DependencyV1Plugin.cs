using System.Runtime.Loader;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.TestProviders.SharedDependency;

namespace Blaze.Interaction.TestProviders;

public sealed class DependencyV1Plugin : IInteractionProviderPlugin
{
    public ProviderDescriptor Descriptor { get; } = new(
        "blaze.test.dependency-v1",
        "Valid test provider",
        new Version(1, 2, 3),
        "Test",
        ["interaction-point", "diagnostics"]);
    public IProviderSettingsViewFactory? SettingsViewFactory => null;
    public IInteractionProvider CreateProvider(ProviderCreateContext context) => throw new NotSupportedException();
    public string GetDependencyVersion() => DependencyVersion.Value;
    public string GetDependencyLoadContextName() => AssemblyLoadContext.GetLoadContext(typeof(DependencyVersion).Assembly)!.Name!;
}
