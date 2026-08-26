using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

internal interface ICameraVisionControl
{
    CameraVisionConfiguration? CurrentConfiguration { get; }
    CameraVisionStatusSnapshot CurrentStatus { get; }
    event Action<CameraVisionStatusSnapshot>? StatusChanged;
    Task<IReadOnlyList<CameraDeviceDescriptor>> EnumerateDevicesAsync(CancellationToken cancellationToken);
    Task ApplyAsync(CameraVisionConfiguration configuration, CancellationToken cancellationToken);
    Task ReconnectAsync(CancellationToken cancellationToken);
    Task SetCalibrationPointAsync(
        string surfaceId,
        int pointIndex,
        Vector2Data previewPosition,
        Vector2Data previewSize,
        CancellationToken cancellationToken);
    Task ResetCalibrationAsync(string surfaceId, CancellationToken cancellationToken);
}
