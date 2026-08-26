using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

internal interface ICameraUiDispatcher
{
    Task InvokeAsync(Action action);
}

internal sealed class CameraVisionSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ICameraVisionControl _control;
    private readonly ICameraUiDispatcher _dispatcher;
    private readonly string _surfaceId;
    private readonly object _snapshotGate = new();
    private readonly AsyncCommand _applyCommand;
    private readonly AsyncCommand _reconnectCommand;
    private readonly AsyncCommand _refreshDevicesCommand;
    private readonly AsyncCommand _resetCalibrationCommand;
    private CameraVisionStatusSnapshot? _pendingSnapshot;
    private bool _snapshotDispatchScheduled;
    private IReadOnlyList<CameraDeviceDescriptor> _devices = Array.Empty<CameraDeviceDescriptor>();
    private string? _errorMessage;
    private bool _isBusy;
    private int _disposed;
    private int _deviceIndex;
    private int _width;
    private int _height;
    private double _framesPerSecond;
    private bool _mirrorX;
    private CameraRotation _rotation;
    private int _maxHands;
    private float _minDetectionConfidence;
    private float _minTrackingConfidence;
    private HandTrackingPoint _trackingPoint;
    private float _smoothingFactor;
    private float _maximumMatchDistance;
    private int _lostFrameTolerance;

    internal CameraVisionSettingsViewModel(
        ICameraVisionControl control,
        ICameraUiDispatcher dispatcher,
        string surfaceId)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _surfaceId = string.IsNullOrWhiteSpace(surfaceId)
            ? throw new ArgumentException("A surface ID is required.", nameof(surfaceId))
            : surfaceId;
        var configuration = control.CurrentConfiguration
            ?? throw new InvalidOperationException("CameraVision settings require an initialized provider.");
        ApplyConfiguration(configuration);
        _applyCommand = new AsyncCommand(ApplyAsync, () => !IsBusy, ExecuteOperationAsync);
        _reconnectCommand = new AsyncCommand(
            token => _control.ReconnectAsync(token),
            () => !IsBusy,
            ExecuteOperationAsync);
        _refreshDevicesCommand = new AsyncCommand(
            RefreshDevicesAsync,
            () => !IsBusy,
            ExecuteOperationAsync);
        _resetCalibrationCommand = new AsyncCommand(
            token => _control.ResetCalibrationAsync(_surfaceId, token),
            () => !IsBusy,
            ExecuteOperationAsync);
        ApplyStatus(control.CurrentStatus);
        _control.StatusChanged += OnStatusChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<CameraDeviceDescriptor> Devices { get => _devices; private set => Set(ref _devices, value); }
    public int DeviceIndex { get => _deviceIndex; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _deviceIndex, value); } }
    public int Width { get => _width; set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _width, value); } }
    public int Height { get => _height; set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _height, value); } }
    public double FramesPerSecond { get => _framesPerSecond; set { if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _framesPerSecond, value); } }
    public bool MirrorX { get => _mirrorX; set => Set(ref _mirrorX, value); }
    public CameraRotation Rotation { get => _rotation; set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _rotation, value); } }
    public int MaxHands { get => _maxHands; set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _maxHands, value); } }
    public float MinDetectionConfidence { get => _minDetectionConfidence; set { ValidateUnit(value); Set(ref _minDetectionConfidence, value); } }
    public float MinTrackingConfidence { get => _minTrackingConfidence; set { ValidateUnit(value); Set(ref _minTrackingConfidence, value); } }
    public HandTrackingPoint TrackingPoint { get => _trackingPoint; set { if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _trackingPoint, value); } }
    public float SmoothingFactor { get => _smoothingFactor; set { ValidateUnit(value); Set(ref _smoothingFactor, value); } }
    public float MaximumMatchDistance { get => _maximumMatchDistance; set { if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _maximumMatchDistance, value); } }
    public int LostFrameTolerance { get => _lostFrameTolerance; set { if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); Set(ref _lostFrameTolerance, value); } }
    public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }
    public ProviderRuntimeStatus ProviderStatus { get; private set; }
    public CameraCaptureStatus CameraStatus { get; private set; }
    public double CameraFramesPerSecond { get; private set; }
    public double InferenceFramesPerSecond { get; private set; }
    public double OutputFramesPerSecond { get; private set; }
    public double InferenceLatencyMilliseconds { get; private set; }
    public int DetectedHandCount { get; private set; }
    public long DroppedFrames { get; private set; }
    public bool UnityConnected { get; private set; }
    public CameraPreviewSnapshot? Preview { get; private set; }
    public ICommand ApplyCommand => _applyCommand;
    public ICommand ReconnectCommand => _reconnectCommand;
    public ICommand RefreshDevicesCommand => _refreshDevicesCommand;
    public ICommand ResetCalibrationCommand => _resetCalibrationCommand;

    internal Task SetCalibrationPointAsync(
        int pointIndex,
        Vector2Data previewPosition,
        Vector2Data previewSize,
        CancellationToken cancellationToken) =>
        _control.SetCalibrationPointAsync(
            _surfaceId,
            pointIndex,
            previewPosition,
            previewSize,
            cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _control.StatusChanged -= OnStatusChanged;
        }
    }

    private Task ApplyAsync(CancellationToken cancellationToken)
    {
        var current = _control.CurrentConfiguration
            ?? throw new InvalidOperationException("CameraVision is not initialized.");
        var next = new CameraVisionConfiguration(
            CameraVisionConfiguration.CurrentSchemaVersion,
            new CameraCaptureOptions
            {
                DeviceIndex = DeviceIndex,
                Width = Width,
                Height = Height,
                FramesPerSecond = FramesPerSecond,
                MirrorX = MirrorX,
                Rotation = Rotation,
                ReconnectDelay = current.Capture.ReconnectDelay
            },
            MaxHands,
            MinDetectionConfidence,
            MinTrackingConfidence,
            TrackingPoint,
            SmoothingFactor,
            MaximumMatchDistance,
            LostFrameTolerance,
            current.Calibrations);
        return _control.ApplyAsync(next, cancellationToken);
    }

    private async Task RefreshDevicesAsync(CancellationToken cancellationToken) =>
        Devices = await _control.EnumerateDevicesAsync(cancellationToken).ConfigureAwait(false);

    private async Task ExecuteOperationAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _operationInFlight, 1, 0) != 0) return;
        await _dispatcher.InvokeAsync(() => { ErrorMessage = null; IsBusy = true; }).ConfigureAwait(false);
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => ErrorMessage = exception.Message).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _operationInFlight, 0);
            await _dispatcher.InvokeAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private int _operationInFlight;

    private void OnStatusChanged(CameraVisionStatusSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _pendingSnapshot = snapshot;
            if (_snapshotDispatchScheduled) return;
            _snapshotDispatchScheduled = true;
        }
        _ = DrainSnapshotAsync();
    }

    private async Task DrainSnapshotAsync()
    {
        while (true)
        {
            CameraVisionStatusSnapshot? snapshot;
            lock (_snapshotGate)
            {
                snapshot = _pendingSnapshot;
                _pendingSnapshot = null;
                if (snapshot is null)
                {
                    _snapshotDispatchScheduled = false;
                    return;
                }
            }
            await _dispatcher.InvokeAsync(() => ApplyStatus(snapshot)).ConfigureAwait(false);
        }
    }

    private void ApplyConfiguration(CameraVisionConfiguration configuration)
    {
        _deviceIndex = configuration.Capture.DeviceIndex;
        _width = configuration.Capture.Width;
        _height = configuration.Capture.Height;
        _framesPerSecond = configuration.Capture.FramesPerSecond;
        _mirrorX = configuration.Capture.MirrorX;
        _rotation = configuration.Capture.Rotation;
        _maxHands = configuration.MaxHands;
        _minDetectionConfidence = configuration.MinDetectionConfidence;
        _minTrackingConfidence = configuration.MinTrackingConfidence;
        _trackingPoint = configuration.TrackingPoint;
        _smoothingFactor = configuration.SmoothingFactor;
        _maximumMatchDistance = configuration.MaximumMatchDistance;
        _lostFrameTolerance = configuration.LostFrameTolerance;
    }

    private void ApplyStatus(CameraVisionStatusSnapshot status)
    {
        ProviderStatus = status.ProviderStatus;
        CameraStatus = status.CameraStatus;
        CameraFramesPerSecond = status.CameraFramesPerSecond;
        InferenceFramesPerSecond = status.InferenceFramesPerSecond;
        OutputFramesPerSecond = status.OutputFramesPerSecond;
        InferenceLatencyMilliseconds = status.InferenceLatencyMilliseconds;
        DetectedHandCount = status.DetectedHandCount;
        DroppedFrames = status.DroppedFrames;
        UnityConnected = status.UnityConnected;
        Preview = status.Preview;
        ErrorMessage = status.Error ?? ErrorMessage;
        foreach (var property in new[]
                 {
                     nameof(ProviderStatus), nameof(CameraStatus), nameof(CameraFramesPerSecond),
                     nameof(InferenceFramesPerSecond), nameof(OutputFramesPerSecond),
                     nameof(InferenceLatencyMilliseconds), nameof(DetectedHandCount),
                     nameof(DroppedFrames), nameof(UnityConnected), nameof(Preview)
                 }) OnPropertyChanged(property);
    }

    private void RaiseCommands()
    {
        _applyCommand?.RaiseCanExecuteChanged();
        _reconnectCommand?.RaiseCanExecuteChanged();
        _refreshDevicesCommand?.RaiseCanExecuteChanged();
        _resetCalibrationCommand?.RaiseCanExecuteChanged();
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static void ValidateUnit(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(value));
    }

    private sealed class AsyncCommand : ICommand
    {
        private readonly Func<CancellationToken, Task> _operation;
        private readonly Func<bool> _canExecute;
        private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _runner;
        internal AsyncCommand(Func<CancellationToken, Task> operation, Func<bool> canExecute,
            Func<Func<CancellationToken, Task>, CancellationToken, Task>? runner = null)
        {
            _operation = operation;
            _canExecute = canExecute;
            _runner = runner ?? ((op, token) => ExecuteOperationAsync(op, token));
        }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) { if (CanExecute(parameter)) _ = _runner(_operation, CancellationToken.None); }
        internal void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

        private static async Task ExecuteOperationAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken token) => await operation(token).ConfigureAwait(false);
    }
}
