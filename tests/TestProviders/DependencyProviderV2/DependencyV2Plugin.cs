using System.Runtime.Loader;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.TestProviders.SharedDependency;

namespace Blaze.Interaction.TestProviders;

public sealed class DependencyV2Plugin : IInteractionProviderPlugin
{
    public ProviderDescriptor Descriptor { get; } = new("blaze.test.dependency-v2", "Dependency V2", new Version(2, 0), "Test", []);
    public IProviderSettingsViewFactory? SettingsViewFactory => null;
    public IInteractionProvider CreateProvider(ProviderCreateContext context) => throw new NotSupportedException();
    public string GetDependencyVersion() => DependencyVersion.Value;
    public string GetDependencyLoadContextName() => AssemblyLoadContext.GetLoadContext(typeof(DependencyVersion).Assembly)!.Name!;
}
