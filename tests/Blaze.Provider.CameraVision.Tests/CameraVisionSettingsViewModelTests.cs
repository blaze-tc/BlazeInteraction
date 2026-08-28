using System.Windows.Input;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionSettingsViewModelTests
{
    [Fact]
    public async Task SelectingDevice_LoadsItsProfileAndFiltersFrameRatesByResolution()
    {
        var profiles = new Dictionary<int, CameraDeviceProfile>
        {
            [0] = new(new CameraCaptureMode(1280, 720, 30), false, CameraRotation.Rotate0),
            [1] = new(new CameraCaptureMode(1920, 1080, 60), true, CameraRotation.Rotate90)
        };
        var control = new FakeControl(Configuration(deviceIndex: 0, profiles: profiles));
        control.Capabilities[1] = new CameraDeviceCapabilities(
            new CameraDeviceDescriptor(1, "Camera 1"),
            [
                new CameraCaptureMode(1280, 720, 30),
                new CameraCaptureMode(1920, 1080, 30),
                new CameraCaptureMode(1920, 1080, 60)
            ],
            false,
            null);
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        viewModel.DeviceIndex = 1;
        await viewModel.WaitForCapabilitiesAsync();

        Assert.Equal(new CameraResolutionOption(1920, 1080), viewModel.SelectedResolution);
        Assert.Equal(new double[] { 30, 60 }, viewModel.FrameRates);
        Assert.Equal(60, viewModel.SelectedFrameRate);
        Assert.True(viewModel.MirrorX);
        Assert.Equal(CameraRotation.Rotate90, viewModel.Rotation);
        Assert.Null(viewModel.CapabilityWarning);
    }

    [Fact]
    public async Task SelectingResolution_FiltersFrameRatesAndApplyPreservesOtherDeviceProfiles()
    {
        var profiles = new Dictionary<int, CameraDeviceProfile>
        {
            [0] = new(new CameraCaptureMode(1280, 720, 30), false, CameraRotation.Rotate0),
            [1] = new(new CameraCaptureMode(1920, 1080, 60), false, CameraRotation.Rotate0)
        };
        var control = new FakeControl(Configuration(deviceIndex: 0, profiles: profiles));
        control.Capabilities[0] = new CameraDeviceCapabilities(
            new CameraDeviceDescriptor(0, "Camera 0"),
            [
                new CameraCaptureMode(1280, 720, 30),
                new CameraCaptureMode(1280, 720, 60),
                new CameraCaptureMode(1920, 1080, 30)
            ],
            false,
            null);
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        viewModel.RefreshDevicesCommand.Execute(null);
        await WaitUntilAsync(() => control.EnumerateCalls == 1 && !viewModel.IsBusy);
        await viewModel.WaitForCapabilitiesAsync();
        viewModel.SelectedResolution = new CameraResolutionOption(1280, 720);
        viewModel.SelectedFrameRate = 60;
        viewModel.ApplyCommand.Execute(null);
        await WaitUntilAsync(() => control.ApplyCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(new double[] { 30, 60 }, viewModel.FrameRates);
        Assert.Equal(60, control.AppliedConfiguration!.Capture.FramesPerSecond);
        Assert.Equal(60, control.AppliedConfiguration.DeviceProfiles[0].Mode.FramesPerSecond);
        Assert.Equal(
            new CameraCaptureMode(1920, 1080, 60),
            control.AppliedConfiguration.DeviceProfiles[1].Mode);
    }

    [Fact]
    public async Task CaptureAndHandSettingsRoundTripThroughApply()
    {
        var control = new FakeControl(Configuration());
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");
        viewModel.DeviceIndex = 2;
        viewModel.Width = 1920;
        viewModel.Height = 1080;
        viewModel.FramesPerSecond = 60;
        viewModel.MirrorX = true;
        viewModel.Rotation = CameraRotation.Rotate90;
        viewModel.MaxHands = 32;
        viewModel.TrackingPoint = HandTrackingPoint.PalmCenter;
        viewModel.SmoothingFactor = 0.6f;
        viewModel.MinDetectionConfidence = 0.7f;
        viewModel.MinTrackingConfidence = 0.8f;

        viewModel.ApplyCommand.Execute(null);
        await WaitUntilAsync(() => control.ApplyCalls == 1 && !viewModel.IsBusy);

        var saved = control.AppliedConfiguration!;
        Assert.Equal(2, saved.Capture.DeviceIndex);
        Assert.Equal(1920, saved.Capture.Width);
        Assert.Equal(1080, saved.Capture.Height);
        Assert.Equal(60, saved.Capture.FramesPerSecond);
        Assert.True(saved.Capture.MirrorX);
        Assert.Equal(CameraRotation.Rotate90, saved.Capture.Rotation);
        Assert.Equal(32, saved.MaxHands);
        Assert.Equal(HandTrackingPoint.PalmCenter, saved.TrackingPoint);
        Assert.Equal(0.6f, saved.SmoothingFactor);
        Assert.Equal(0.7f, saved.MinDetectionConfidence);
        Assert.Equal(0.8f, saved.MinTrackingConfidence);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void MaxHandsAcceptsAnyPositiveValue(int maxHands)
    {
        var viewModel = ViewModel();
        viewModel.MaxHands = maxHands;
        Assert.Equal(maxHands, viewModel.MaxHands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MaxHandsRejectsNonPositiveValue(int maxHands)
    {
        var viewModel = ViewModel();
        Assert.Throws<ArgumentOutOfRangeException>(() => viewModel.MaxHands = maxHands);
    }

    [Fact]
    public void ModelHasTrackingModeButNoHandednessProperty()
    {
        var properties = typeof(CameraVisionSettingsViewModel).GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(CameraVisionSettingsViewModel.TrackingPoint), properties);
        Assert.DoesNotContain(properties, name =>
            name.Contains("Handedness", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Left", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Right", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfiguredDeviceIsImmediatelySelectableBeforeHardwareRefresh()
    {
        var configuration = Configuration(deviceIndex: 3);
        var viewModel = new CameraVisionSettingsViewModel(
            new FakeControl(configuration),
            new ImmediateDispatcher(),
            "main");

        var device = Assert.Single(viewModel.Devices);
        Assert.Equal(3, device.Index);
        Assert.Equal("Camera 3", device.DisplayName);
    }

    [Fact]
    public void ConstructorUsesConfiguredModeWithoutProbingCameraCapabilities()
    {
        var control = new FakeControl(Configuration());

        using var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        Assert.Equal(0, control.CapabilityCalls);
        Assert.Equal(new CameraResolutionOption(1280, 720), Assert.Single(viewModel.Resolutions));
        Assert.Equal(30, Assert.Single(viewModel.FrameRates));
    }

    [Fact]
    public async Task RefreshDevicesPreservesConfiguredDeviceWhenItIsAlreadyInUse()
    {
        var configuration = Configuration(deviceIndex: 3);
        var control = new FakeControl(configuration)
        {
            EnumeratedDevices = [new CameraDeviceDescriptor(1, "Camera 1")]
        };
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        viewModel.RefreshDevicesCommand.Execute(null);
        await WaitUntilAsync(() => control.EnumerateCalls == 1 && !viewModel.IsBusy);

        Assert.Equal(new[] { 1, 3 }, viewModel.Devices.Select(device => device.Index));
    }

    [Fact]
    public async Task ApplyBusyAndErrorStateAreObservable()
    {
        var control = new FakeControl(Configuration());
        control.BlockApply();
        control.ApplyFailure = new InvalidOperationException("apply failed");
        var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        viewModel.ApplyCommand.Execute(null);
        await control.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.ApplyCommand.CanExecute(null));

        control.ReleaseApply();
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.Contains("apply failed", viewModel.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotUpdatesUseDispatcherAndDisposeUnsubscribes()
    {
        var control = new FakeControl(Configuration());
        var dispatcher = new RecordingDispatcher();
        var viewModel = new CameraVisionSettingsViewModel(control, dispatcher, "main");
        Assert.Equal(1, control.SubscriberCount);

        control.Publish(Status(detectedHands: 8));
        await WaitUntilAsync(() => viewModel.DetectedHandCount == 8);

        Assert.True(dispatcher.InvokeCalls > 0);
        viewModel.Dispose();
        Assert.Equal(0, control.SubscriberCount);
    }

    [Fact]
    public async Task VisualSnapshotsAreLimitedAndKeepLatestStatus()
    {
        var control = new FakeControl(Configuration());
        using var viewModel = new CameraVisionSettingsViewModel(
            control,
            new ImmediateDispatcher(),
            "main");

        control.Publish(Status(detectedHands: 1));
        control.Publish(Status(detectedHands: 2));
        control.Publish(Status(detectedHands: 3));
        await WaitUntilAsync(() => viewModel.VisualSnapshot?.DetectedHandCount == 3);

        Assert.True(viewModel.UiRenderedFrames >= 2);
        Assert.True(viewModel.UiSupersededFrames >= 1);
        Assert.Equal(3, viewModel.VisualSnapshot!.DetectedHandCount);
    }

    private static CameraVisionSettingsViewModel ViewModel() => new(
        new FakeControl(Configuration()),
        new ImmediateDispatcher(),
        "main");

    private static CameraVisionConfiguration Configuration(
        int deviceIndex = 0,
        IReadOnlyDictionary<int, CameraDeviceProfile>? profiles = null)
    {
        var selectedProfile = profiles is not null && profiles.TryGetValue(deviceIndex, out var profile)
            ? profile
            : new CameraDeviceProfile(
                new CameraCaptureMode(1280, 720, 30),
                false,
                CameraRotation.Rotate0);
        return new CameraVisionConfiguration(
        2,
        new CameraCaptureOptions
        {
            DeviceIndex = deviceIndex,
            Width = selectedProfile.Mode.Width,
            Height = selectedProfile.Mode.Height,
            FramesPerSecond = selectedProfile.Mode.FramesPerSecond,
            MirrorX = selectedProfile.MirrorX,
            Rotation = selectedProfile.Rotation
        },
        8,
        0.5f,
        0.5f,
        HandTrackingPoint.IndexTip,
        0.35f,
        0.2f,
        2,
        null,
        profiles);
    }

    private static CameraVisionStatusSnapshot Status(int detectedHands) => new(
        Blaze.Interaction.Provider.Abstractions.ProviderRuntimeStatus.Running,
        CameraCaptureStatus.Connected,
        30,
        25,
        25,
        4,
        detectedHands,
        Array.Empty<Vector2Data>(),
        2,
        true,
        null,
        null,
        null);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeControl(CameraVisionConfiguration configuration) : ICameraVisionControl
    {
        private Action<CameraVisionStatusSnapshot>? _statusChanged;
        private TaskCompletionSource? _releaseApply;
        public CameraVisionConfiguration? CurrentConfiguration { get; } = configuration;
        public CameraVisionStatusSnapshot CurrentStatus { get; private set; } = Status(0);
        public int ApplyCalls { get; private set; }
        public int SubscriberCount { get; private set; }
        public CameraVisionConfiguration? AppliedConfiguration { get; private set; }
        public Exception? ApplyFailure { get; set; }
        public IReadOnlyList<CameraDeviceDescriptor> EnumeratedDevices { get; set; } =
            [new CameraDeviceDescriptor(0, "Camera 0")];
        public Dictionary<int, CameraDeviceCapabilities> Capabilities { get; } = new();
        public int EnumerateCalls { get; private set; }
        public int CapabilityCalls { get; private set; }
        public TaskCompletionSource ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<CameraVisionStatusSnapshot>? StatusChanged
        {
            add { _statusChanged += value; SubscriberCount++; }
            remove { _statusChanged -= value; SubscriberCount--; }
        }
        public Task<IReadOnlyList<CameraDeviceDescriptor>> EnumerateDevicesAsync(CancellationToken cancellationToken)
        {
            EnumerateCalls++;
            return Task.FromResult(EnumeratedDevices);
        }
        public Task<CameraDeviceCapabilities> GetCapabilitiesAsync(
            int deviceIndex,
            CancellationToken cancellationToken)
        {
            CapabilityCalls++;
            if (Capabilities.TryGetValue(deviceIndex, out var capabilities))
            {
                return Task.FromResult(capabilities);
            }

            return Task.FromResult(new CameraDeviceCapabilities(
                new CameraDeviceDescriptor(deviceIndex, $"Camera {deviceIndex}"),
                [new CameraCaptureMode(
                    CurrentConfiguration!.Capture.Width,
                    CurrentConfiguration.Capture.Height,
                    CurrentConfiguration.Capture.FramesPerSecond)],
                false,
                null));
        }
        public async Task ApplyAsync(CameraVisionConfiguration next, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            ApplyStarted.TrySetResult();
            if (_releaseApply is not null) await _releaseApply.Task.WaitAsync(cancellationToken);
            if (ApplyFailure is not null) throw ApplyFailure;
            AppliedConfiguration = next;
        }
        public Task ReconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetCalibrationPointAsync(string surfaceId, int pointIndex,
            Vector2Data previewPosition, Vector2Data previewSize, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task ResetCalibrationAsync(string surfaceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        internal void BlockApply() =>
            _releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseApply() => _releaseApply?.TrySetResult();
        internal void Publish(CameraVisionStatusSnapshot status)
        {
            CurrentStatus = status;
            _statusChanged?.Invoke(status);
        }
    }

    private sealed class ImmediateDispatcher : ICameraUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
    }

    private sealed class RecordingDispatcher : ICameraUiDispatcher
    {
        public int InvokeCalls { get; private set; }
        public Task InvokeAsync(Action action) { InvokeCalls++; action(); return Task.CompletedTask; }
    }
}
