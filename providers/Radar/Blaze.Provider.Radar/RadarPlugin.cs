using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.Radar;

public sealed class RadarPlugin : IInteractionProviderPlugin
{
    public ProviderDescriptor Descriptor { get; } = new(
        RadarFrameAdapter.ProviderId,
        "F10 / F20 激光雷达",
        new Version(1, 0, 0),
        "Radar",
        ["interaction-point", "preview", "multi-sensor", "calibration", "multi-surface"]);

    public IProviderSettingsViewFactory? SettingsViewFactory => null;

    public IInteractionProvider CreateProvider(ProviderCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new RadarInteractionProvider("radar-main", context);
    }
}
