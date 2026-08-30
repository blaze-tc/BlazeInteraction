using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

public sealed class CameraVisionPlugin : IInteractionProviderPlugin
{
    public const string ProviderId = "blaze.camera.vision";
    public const string DefaultInstanceId = "camera-vision-main";

    public ProviderDescriptor Descriptor { get; } = new(
        ProviderId,
        "CameraVision RGB Camera",
        new Version(1, 1, 1),
        "Camera",
        ["interaction-point", "preview", "hand-landmarks"]);

    public IProviderSettingsViewFactory? SettingsViewFactory => CameraVisionSettingsViewFactory.Instance;

    public IInteractionProvider CreateProvider(ProviderCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new CameraVisionProvider(DefaultInstanceId, context);
    }
}
