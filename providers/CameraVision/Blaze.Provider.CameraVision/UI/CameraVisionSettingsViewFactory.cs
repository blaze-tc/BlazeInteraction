using System.Windows.Threading;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

public sealed class CameraVisionSettingsViewFactory : IProviderSettingsViewFactory
{
    public static CameraVisionSettingsViewFactory Instance { get; } = new();
    private CameraVisionSettingsViewFactory() { }

    public object CreateView(IInteractionProvider provider, IProviderSettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (provider is not CameraVisionProvider camera || provider is not ICameraVisionControl control)
        {
            throw new ArgumentException("CameraVision settings require a CameraVision provider.", nameof(provider));
        }

        var surfaceId = camera.ActiveSurfaceId
            ?? throw new InvalidOperationException("CameraVision has no active Interaction Surface.");
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new WpfCameraUiDispatcher(Dispatcher.CurrentDispatcher),
            surfaceId,
            camera.ActiveSurface);
        return new CameraVisionSettingsWindow { DataContext = viewModel };
    }
}
