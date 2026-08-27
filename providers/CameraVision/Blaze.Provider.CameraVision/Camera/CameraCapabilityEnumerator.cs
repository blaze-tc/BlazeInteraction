namespace Blaze.Provider.CameraVision;

public sealed class CameraCapabilityEnumerator
{
    private const string FallbackWarning = "设备无法报告完整能力，正在使用兼容预设。";

    private static readonly CameraCaptureMode[] DefaultCandidates =
        (from size in new[] { (640, 480), (1280, 720), (1920, 1080), (3840, 2160) }
         from framesPerSecond in new[] { 15d, 24d, 30d, 60d }
         select new CameraCaptureMode(size.Item1, size.Item2, framesPerSecond))
        .ToArray();

    private readonly ICameraCaptureBackendFactory _factory;
    private readonly IReadOnlyList<CameraCaptureMode> _candidates;

    public CameraCapabilityEnumerator(
        ICameraCaptureBackendFactory factory,
        IEnumerable<CameraCaptureMode>? candidates = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _candidates = (candidates ?? DefaultCandidates).ToArray();
    }

    public async Task<CameraDeviceCapabilities> EnumerateAsync(
        CameraDeviceDescriptor device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var verifiedModes = new HashSet<CameraCaptureMode>();

        foreach (var candidate in _candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var backend = _factory.Create();
            var opened = backend.TryOpen(new CameraCaptureOptions
            {
                DeviceIndex = device.Index,
                Width = candidate.Width,
                Height = candidate.Height,
                FramesPerSecond = candidate.FramesPerSecond
            });

            if (opened && backend.TryGetActiveMode(out var activeMode) &&
                activeMode is not null && Matches(candidate, activeMode))
            {
                verifiedModes.Add(candidate);
            }

            backend.Close();
        }

        var orderedModes = Sort(verifiedModes);
        if (orderedModes.Count > 0)
        {
            return new CameraDeviceCapabilities(device, orderedModes, false, null);
        }

        return new CameraDeviceCapabilities(
            device,
            Sort(_candidates.Count > 0 ? _candidates : DefaultCandidates),
            true,
            FallbackWarning);
    }

    private static bool Matches(CameraCaptureMode requested, CameraCaptureMode actual) =>
        requested.Width == actual.Width &&
        requested.Height == actual.Height &&
        Math.Abs(requested.FramesPerSecond - actual.FramesPerSecond) <= 1d;

    private static IReadOnlyList<CameraCaptureMode> Sort(IEnumerable<CameraCaptureMode> modes) =>
        modes
            .Distinct()
            .OrderBy(mode => (long)mode.Width * mode.Height)
            .ThenBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .ThenBy(mode => mode.FramesPerSecond)
            .ToArray();
}
