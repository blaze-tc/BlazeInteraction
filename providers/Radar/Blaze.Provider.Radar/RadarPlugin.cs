using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.Radar;

public sealed class RadarPlugin : IInteractionProviderPlugin
{
    public ProviderDescriptor Descriptor { get; } = new(
        RadarFrameAdapter.ProviderId,
        "F10 / F20 激光雷达",
        new Version(1, 1, 1),
        "Radar",
        ["interaction-point", "preview", "multi-sensor", "calibration", "multi-surface"]);

    public IProviderSettingsViewFactory? SettingsViewFactory => RadarSettingsViewFactory.Instance;

    public IInteractionProvider CreateProvider(ProviderCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RadarInteractionProvider("radar-main", context);
    }
}

public sealed class RadarSettingsViewFactory : IProviderSettingsViewFactory
{
    public static RadarSettingsViewFactory Instance { get; } = new();

    private RadarSettingsViewFactory()
    {
    }

    public object CreateView(IInteractionProvider provider, IProviderSettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(context);
        return provider is RadarInteractionProvider radarProvider
            ? radarProvider.CreateSettingsView()
            : throw new ArgumentException("The Radar settings view requires a RadarInteractionProvider instance.", nameof(provider));
    }
}
