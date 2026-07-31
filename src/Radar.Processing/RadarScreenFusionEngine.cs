using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public readonly record struct SensorDetection(int DetectionId, float PixelX, float PixelY, float Confidence);

public sealed class SensorDetectionFrame
{
    public SensorDetectionFrame(string sensorId, DateTimeOffset timestamp, IReadOnlyList<SensorDetection> detections)
    {
        SensorId = sensorId ?? throw new ArgumentNullException(nameof(sensorId));
        Timestamp = timestamp;
        ArgumentNullException.ThrowIfNull(detections);
        Detections = Array.AsReadOnly(detections.ToArray());
    }

    public string SensorId { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<SensorDetection> Detections { get; }
}

public sealed record FusedScreenTarget(
    int TrackId,
    float PixelX,
    float PixelY,
    float Confidence,
    int SourceSensorCount,
    bool IsConfirmed);

public sealed class RadarScreenFusionResult
{
    public RadarScreenFusionResult(IReadOnlyList<FusedScreenTarget> targets, IReadOnlyList<RadarScreenPointer> pointers)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(pointers);
        Targets = Array.AsReadOnly(targets.ToArray());
        Pointers = Array.AsReadOnly(pointers.ToArray());
    }

    public IReadOnlyList<FusedScreenTarget> Targets { get; }
    public IReadOnlyList<RadarScreenPointer> Pointers { get; }
}

public sealed class RadarScreenFusionOptions
{
    public required RadarScreenInfo Screen { get; init; }
    public int SensorDataMaxAgeMilliseconds { get; init; } = 150;
    public float FusionDistancePixels { get; init; } = 80f;
    public float MaximumAssociationDistancePixels { get; init; } = 160f;
    public int ConfirmFrames { get; init; } = 2;
    public int LostFrames { get; init; } = 3;
    public float SmoothingAlpha { get; init; } = 0.5f;
    public RadarInteractionMode InteractionMode { get; init; } = RadarInteractionMode.Touch;
    public int DwellMilliseconds { get; init; } = 800;
    public float DwellRadiusNormalized { get; init; } = 0.03f;
    public float DragThresholdNormalized { get; init; } = 0.015f;
    public int MinimumPressMilliseconds { get; init; } = 30;
    public float MaximumClickMovementNormalized { get; init; } = 0.03f;
}

public interface IRadarScreenFusionEngine
{
    void Publish(SensorDetectionFrame frame);
    RadarScreenFusionResult Tick(DateTimeOffset timestamp);
    IReadOnlyList<RadarScreenPointer> SnapshotPressedPointers(DateTimeOffset timestamp);
    IReadOnlyList<RadarScreenPointer> Reset(DateTimeOffset timestamp);
}

public sealed class RadarScreenFusionEngine : IRadarScreenFusionEngine
{
    private const int MaximumScreenPixels = 32768;
    private const int MinimumSensorDataMaxAgeMilliseconds = 10;
    private const int MaximumSensorDataMaxAgeMilliseconds = 5000;
    private const int MaximumTrackingFrames = 120;

    private readonly RadarScreenFusionOptions _options;
    private readonly PointerStateMachine _pointerStateMachine;
    private readonly Dictionary<string, SensorDetectionFrame> _latestFrames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, TrackState> _tracks = [];
    private readonly Dictionary<int, PointerPosition> _pointerPositions = [];
    private readonly HashSet<int> _pressedTouchPointers = [];
    private int _nextTrackId = 1;
    private DateTimeOffset? _lastTickTimestamp;

    public RadarScreenFusionEngine(RadarScreenFusionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(_options);
        _pointerStateMachine = new PointerStateMachine(new RadarPointerOptions
        {
            Mode = options.InteractionMode,
            LostFrames = options.LostFrames,
            MinimumPressDuration = TimeSpan.FromMilliseconds(options.MinimumPressMilliseconds),
            DwellDuration = TimeSpan.FromMilliseconds(options.DwellMilliseconds),
            DwellRadiusNormalized = options.DwellRadiusNormalized,
            DragThresholdNormalized = options.DragThresholdNormalized,
            MaximumClickMovementNormalized = options.MaximumClickMovementNormalized
        });
    }

    public void Publish(SensorDetectionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var sensorId = NormalizeSensorId(frame.SensorId);
        ValidateFrame(frame);

        if (_latestFrames.TryGetValue(sensorId, out var current) && current.Timestamp >= frame.Timestamp)
        {
            return;
        }

        _latestFrames[sensorId] = new SensorDetectionFrame(sensorId, frame.Timestamp, frame.Detections);
    }

    public RadarScreenFusionResult Tick(DateTimeOffset timestamp)
    {
        if (_lastTickTimestamp.HasValue && timestamp <= _lastTickTimestamp.Value)
        {
            return new RadarScreenFusionResult([], []);
        }

        _lastTickTimestamp = timestamp;
        var observations = FuseCurrentDetections(timestamp);
        var activeTracks = AssociateAndTrack(observations);
        var targets = activeTracks
            .OrderBy(track => track.TrackId)
            .Select(track => new FusedScreenTarget(
                track.TrackId,
                track.PixelX,
                track.PixelY,
                track.Confidence,
                track.SourceSensorCount,
                track.ObservedFrames >= _options.ConfirmFrames))
            .ToArray();

        var pointerTargets = activeTracks
            .Where(track => track.MissingFrames == 0 && track.ObservedFrames >= _options.ConfirmFrames)
            .Select(track => new RadarTarget(
                track.TrackId,
                track.PixelX / _options.Screen.WidthPixels,
                track.PixelY / _options.Screen.HeightPixels,
                track.PixelX,
                track.PixelY,
                track.Confidence,
                track.SourceSensorCount,
                true))
            .ToArray();

        foreach (var target in targets.Where(target => target.IsConfirmed))
        {
            _pointerPositions[target.TrackId] = new PointerPosition(target.PixelX, target.PixelY, target.Confidence);
        }

        var pointerEvents = _pointerStateMachine.Update(pointerTargets, timestamp);
        var pointers = new List<RadarScreenPointer>(pointerEvents.Count);
        foreach (var pointer in pointerEvents)
        {
            if (!_pointerPositions.TryGetValue(pointer.PointerId, out var position))
            {
                continue;
            }

            pointers.Add(ToScreenPointer(pointer.PointerId, pointer.Phase, position, timestamp));
            if (pointer.Phase == RadarPointerPhase.Down)
            {
                _pressedTouchPointers.Add(pointer.PointerId);
            }
            else if (pointer.Phase == RadarPointerPhase.Up)
            {
                _pressedTouchPointers.Remove(pointer.PointerId);
                _pointerPositions.Remove(pointer.PointerId);
            }
        }

        var heldPhase = _options.InteractionMode switch
        {
            RadarInteractionMode.Touch => RadarPointerPhase.Move,
            RadarInteractionMode.Dwell or RadarInteractionMode.HoverOnly => RadarPointerPhase.Hover,
            _ => (RadarPointerPhase?)null
        };
        if (heldPhase.HasValue)
        {
            var emittedPointerIds = pointers.Select(pointer => pointer.PointerId).ToHashSet();
            foreach (var track in activeTracks.Where(track => track.MissingFrames > 0 &&
                                                               track.ObservedFrames >= _options.ConfirmFrames &&
                                                               !emittedPointerIds.Contains(track.TrackId) &&
                                                               _pointerStateMachine.ContainsPointer(track.TrackId)))
            {
                if (_pointerPositions.TryGetValue(track.TrackId, out var position))
                {
                    pointers.Add(ToScreenPointer(track.TrackId, heldPhase.Value, position, timestamp));
                }
            }
        }

        foreach (var retiredPointerId in _pointerPositions.Keys
                     .Where(pointerId => !_pointerStateMachine.ContainsPointer(pointerId))
                     .ToArray())
        {
            _pointerPositions.Remove(retiredPointerId);
            _pressedTouchPointers.Remove(retiredPointerId);
        }

        return new RadarScreenFusionResult(targets, pointers);
    }

    public IReadOnlyList<RadarScreenPointer> SnapshotPressedPointers(DateTimeOffset timestamp) => Array.AsReadOnly(_pressedTouchPointers
        .OrderBy(pointerId => pointerId)
        .Where(pointerId => _pointerPositions.ContainsKey(pointerId))
        .Select(pointerId => ToScreenPointer(pointerId, RadarPointerPhase.Up, _pointerPositions[pointerId], timestamp))
        .ToArray());

    public IReadOnlyList<RadarScreenPointer> Reset(DateTimeOffset timestamp)
    {
        var output = SnapshotPressedPointers(timestamp);

        _latestFrames.Clear();
        _tracks.Clear();
        _pointerPositions.Clear();
        _pressedTouchPointers.Clear();
        _pointerStateMachine.Reset(timestamp);
        _nextTrackId = 1;
        if (!_lastTickTimestamp.HasValue || timestamp > _lastTickTimestamp.Value)
        {
            _lastTickTimestamp = timestamp;
        }
        return output;
    }

    private IReadOnlyList<FusedObservation> FuseCurrentDetections(DateTimeOffset timestamp)
    {
        var current = new List<DetectionInput>();
        foreach (var pair in _latestFrames.ToArray())
        {
            if (pair.Value.Timestamp > timestamp)
            {
                continue;
            }

            if (timestamp - pair.Value.Timestamp > TimeSpan.FromMilliseconds(_options.SensorDataMaxAgeMilliseconds))
            {
                _latestFrames.Remove(pair.Key);
                continue;
            }

            current.AddRange(pair.Value.Detections.Select(detection => new DetectionInput(pair.Key, detection)));
        }

        var detections = current
            .OrderBy(detection => detection.SensorId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(detection => detection.Detection.PixelX)
            .ThenBy(detection => detection.Detection.PixelY)
            .ThenBy(detection => detection.Detection.DetectionId)
            .ThenBy(detection => detection.Detection.Confidence)
            .ToArray();
        var parent = Enumerable.Range(0, detections.Length).ToArray();
        var candidates = new List<FusionCandidate>();

        for (var left = 0; left < detections.Length; left++)
        {
            for (var right = left + 1; right < detections.Length; right++)
            {
                if (string.Equals(detections[left].SensorId, detections[right].SensorId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var distance = Distance(detections[left].Detection.PixelX, detections[left].Detection.PixelY,
                    detections[right].Detection.PixelX, detections[right].Detection.PixelY);
                if (distance <= _options.FusionDistancePixels)
                {
                    candidates.Add(new FusionCandidate(left, right, distance));
                }
            }
        }

        foreach (var candidate in candidates.OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Left).ThenBy(candidate => candidate.Right))
        {
            var leftRoot = Find(parent, candidate.Left);
            var rightRoot = Find(parent, candidate.Right);
            if (leftRoot == rightRoot || HaveSharedSensor(parent, detections, leftRoot, rightRoot))
            {
                continue;
            }

            parent[rightRoot] = leftRoot;
        }

        return Enumerable.Range(0, detections.Length)
            .GroupBy(index => Find(parent, index))
            .OrderBy(group => group.Min())
            .Select(group =>
            {
                var members = group.Select(index => detections[index]).ToArray();
                return new FusedObservation(
                    members.Average(member => member.Detection.PixelX),
                    members.Average(member => member.Detection.PixelY),
                    members.Average(member => member.Detection.Confidence),
                    members.Select(member => member.SensorId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            })
            .ToArray();
    }

    private IReadOnlyList<TrackState> AssociateAndTrack(IReadOnlyList<FusedObservation> observations)
    {
        var matchedTrackIds = new HashSet<int>();
        var matchedObservationIndices = new HashSet<int>();
        var candidates = new List<Association>();
        foreach (var track in _tracks.Values.OrderBy(track => track.TrackId))
        {
            for (var index = 0; index < observations.Count; index++)
            {
                var observation = observations[index];
                var distance = Distance(track.PixelX, track.PixelY, observation.PixelX, observation.PixelY);
                if (distance <= _options.MaximumAssociationDistancePixels)
                {
                    candidates.Add(new Association(track.TrackId, index, distance));
                }
            }
        }

        foreach (var candidate in candidates.OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.TrackId).ThenBy(candidate => candidate.ObservationIndex))
        {
            if (matchedTrackIds.Contains(candidate.TrackId) || matchedObservationIndices.Contains(candidate.ObservationIndex))
            {
                continue;
            }

            matchedTrackIds.Add(candidate.TrackId);
            matchedObservationIndices.Add(candidate.ObservationIndex);

            var track = _tracks[candidate.TrackId];
            var observation = observations[candidate.ObservationIndex];
            track.PixelX = Smooth(track.PixelX, observation.PixelX);
            track.PixelY = Smooth(track.PixelY, observation.PixelY);
            track.Confidence = Smooth(track.Confidence, observation.Confidence);
            track.SourceSensorCount = observation.SourceSensorCount;
            track.ObservedFrames++;
            track.MissingFrames = 0;
        }

        for (var index = 0; index < observations.Count; index++)
        {
            if (matchedObservationIndices.Contains(index))
            {
                continue;
            }

            var observation = observations[index];
            var track = new TrackState(_nextTrackId++, observation.PixelX, observation.PixelY, observation.Confidence, observation.SourceSensorCount);
            _tracks.Add(track.TrackId, track);
            matchedTrackIds.Add(track.TrackId);
        }

        foreach (var track in _tracks.Values.ToArray())
        {
            if (matchedTrackIds.Contains(track.TrackId))
            {
                continue;
            }

            track.MissingFrames++;
            if (track.MissingFrames >= _options.LostFrames)
            {
                _tracks.Remove(track.TrackId);
            }
        }

        return _tracks.Values.OrderBy(track => track.TrackId).ToArray();
    }

    private RadarScreenPointer ToScreenPointer(int pointerId, RadarPointerPhase phase, PointerPosition position, DateTimeOffset timestamp)
    {
        return new RadarScreenPointer(
            pointerId,
            phase,
            position.PixelX / _options.Screen.WidthPixels,
            position.PixelY / _options.Screen.HeightPixels,
            position.PixelX,
            position.PixelY,
            position.Confidence,
            timestamp.ToUnixTimeMilliseconds());
    }

    private float Smooth(float previous, float current) => previous + _options.SmoothingAlpha * (current - previous);

    private static bool HaveSharedSensor(int[] parent, DetectionInput[] detections, int leftRoot, int rightRoot)
    {
        var leftSensors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rightSensors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < detections.Length; index++)
        {
            var root = Find(parent, index);
            if (root == leftRoot)
            {
                leftSensors.Add(detections[index].SensorId);
            }
            else if (root == rightRoot)
            {
                rightSensors.Add(detections[index].SensorId);
            }
        }

        return leftSensors.Overlaps(rightSensors);
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        var x = x2 - x1;
        var y = y2 - y1;
        return MathF.Sqrt(x * x + y * y);
    }

    private void ValidateFrame(SensorDetectionFrame frame)
    {
        foreach (var detection in frame.Detections)
        {
            if (!float.IsFinite(detection.PixelX) || !float.IsFinite(detection.PixelY) || !float.IsFinite(detection.Confidence) ||
                detection.PixelX < 0f || detection.PixelY < 0f || detection.PixelX > _options.Screen.WidthPixels ||
                detection.PixelY > _options.Screen.HeightPixels || detection.Confidence is < 0f or > 1f)
            {
                throw new ArgumentOutOfRangeException(nameof(frame));
            }
        }
    }

    private static void ValidateOptions(RadarScreenFusionOptions options)
    {
        var screen = options.Screen;
        if (screen is null || !IsConfigurationId(screen.ScreenId) || screen.WidthPixels is < 1 or > MaximumScreenPixels || screen.HeightPixels is < 1 or > MaximumScreenPixels ||
            options.SensorDataMaxAgeMilliseconds is < MinimumSensorDataMaxAgeMilliseconds or > MaximumSensorDataMaxAgeMilliseconds ||
            options.ConfirmFrames is < 1 or > MaximumTrackingFrames || options.LostFrames is < 1 or > MaximumTrackingFrames ||
            options.DwellMilliseconds < 0 || options.MinimumPressMilliseconds < 0 ||
            !float.IsFinite(options.FusionDistancePixels) || options.FusionDistancePixels <= 0f ||
            !float.IsFinite(options.MaximumAssociationDistancePixels) || options.MaximumAssociationDistancePixels <= 0f ||
            !float.IsFinite(options.SmoothingAlpha) || options.SmoothingAlpha is <= 0f or > 1f ||
            !float.IsFinite(options.DwellRadiusNormalized) || options.DwellRadiusNormalized < 0f ||
            !float.IsFinite(options.DragThresholdNormalized) || options.DragThresholdNormalized < 0f ||
            !float.IsFinite(options.MaximumClickMovementNormalized) || options.MaximumClickMovementNormalized < 0f ||
            !Enum.IsDefined(options.InteractionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Screen fusion options are invalid.");
        }
    }

    private static string NormalizeSensorId(string sensorId)
    {
        var normalized = sensorId?.ToLowerInvariant() ?? string.Empty;
        if (!IsConfigurationId(normalized))
        {
            throw new ArgumentException("Sensor ID must match ^[a-z0-9_-]{1,64}$.", nameof(sensorId));
        }

        return normalized;
    }

    private static bool IsConfigurationId(string? value)
    {
        if (value is null || value.Length is < 1 or > 64)
        {
            return false;
        }

        return value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    }

    private sealed class TrackState(int trackId, float pixelX, float pixelY, float confidence, int sourceSensorCount)
    {
        public int TrackId { get; } = trackId;
        public float PixelX { get; set; } = pixelX;
        public float PixelY { get; set; } = pixelY;
        public float Confidence { get; set; } = confidence;
        public int SourceSensorCount { get; set; } = sourceSensorCount;
        public int ObservedFrames { get; set; } = 1;
        public int MissingFrames { get; set; }
    }

    private readonly record struct DetectionInput(string SensorId, SensorDetection Detection);
    private readonly record struct FusionCandidate(int Left, int Right, float Distance);
    private readonly record struct FusedObservation(float PixelX, float PixelY, float Confidence, int SourceSensorCount);
    private readonly record struct Association(int TrackId, int ObservationIndex, float Distance);
    private readonly record struct PointerPosition(float PixelX, float PixelY, float Confidence);
}
