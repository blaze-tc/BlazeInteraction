namespace Blaze.Provider.CameraVision;

public sealed class CameraDeviceEnumerator
{
    private readonly ICameraCaptureBackendFactory _factory;
    private readonly int _maximumDeviceCount;

    public CameraDeviceEnumerator(
        ICameraCaptureBackendFactory factory,
        int maximumDeviceCount = 8)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        if (maximumDeviceCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDeviceCount));
        }

        _maximumDeviceCount = maximumDeviceCount;
    }

    public Task<IReadOnlyList<CameraDeviceDescriptor>> EnumerateAsync(
        CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<CameraDeviceDescriptor>>(async () =>
        {
            var devices = new List<CameraDeviceDescriptor>();
            for (var index = 0; index < _maximumDeviceCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var backend = _factory.Create();
                var options = new CameraCaptureOptions
                {
                    DeviceIndex = index,
                    Width = 640,
                    Height = 480,
                    FramesPerSecond = 30
                };
                if (backend.TryOpen(options))
                {
                    devices.Add(new CameraDeviceDescriptor(index, $"Camera {index}"));
                }

                backend.Close();
            }

            return Array.AsReadOnly(devices.ToArray());
        }, cancellationToken);
}
