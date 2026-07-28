using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public sealed class RadarPointerOptions
{
    public RadarInteractionMode Mode { get; set; } = RadarInteractionMode.Touch;
    public int LostFrames { get; set; } = 3;
    public TimeSpan MinimumPressDuration { get; set; } = TimeSpan.FromMilliseconds(30);
    public TimeSpan DwellDuration { get; set; } = TimeSpan.FromMilliseconds(800);
    public float DwellRadiusNormalized { get; set; } = 0.03f;
    public float MaximumClickMovementNormalized { get; set; } = 0.03f;
    public float DragThresholdNormalized { get; set; } = 0.015f;
}

public sealed class PointerStateMachine
{
    private readonly RadarPointerOptions _options;
    private readonly Dictionary<int, PointerState> _states = [];

    public PointerStateMachine(RadarPointerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.LostFrames < 1 || _options.MinimumPressDuration < TimeSpan.Zero ||
            _options.DwellDuration < TimeSpan.Zero || _options.DwellRadiusNormalized < 0f)
        {
            throw new ArgumentException("Pointer options are invalid.", nameof(options));
        }
    }

    public IReadOnlyList<RadarPointer> Update(
        IReadOnlyList<RadarTarget> targets,
        DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var output = new List<RadarPointer>();
        var observedIds = new HashSet<int>();

        foreach (var target in targets.Where(target => target.IsConfirmed))
        {
            observedIds.Add(target.TrackId);
            if (!_states.TryGetValue(target.TrackId, out var state))
            {
                state = new PointerState
                {
                    PointerId = target.TrackId,
                    X = target.NormalizedX,
                    Y = target.NormalizedY,
                    Confidence = target.Confidence,
                    FirstSeen = timestamp,
                    DwellAnchorX = target.NormalizedX,
                    DwellAnchorY = target.NormalizedY,
                    DwellStarted = timestamp
                };
                _states.Add(target.TrackId, state);
            }
            else
            {
                state.X = target.NormalizedX;
                state.Y = target.NormalizedY;
                state.Confidence = target.Confidence;
                state.MissingFrames = 0;
            }

            EmitObserved(state, timestamp, output);
        }

        foreach (var state in _states.Values.ToArray())
        {
            if (observedIds.Contains(state.PointerId))
            {
                continue;
            }

            state.MissingFrames++;
            if (state.MissingFrames < _options.LostFrames)
            {
                continue;
            }

            if (_options.Mode == RadarInteractionMode.Touch && state.IsPressed &&
                timestamp - state.PressedAt < _options.MinimumPressDuration)
            {
                continue;
            }

            if (_options.Mode == RadarInteractionMode.Touch && state.IsPressed)
            {
                output.Add(ToPointer(state, RadarPointerPhase.Up, timestamp));
            }

            _states.Remove(state.PointerId);
        }

        return output;
    }

    public void Reset(DateTimeOffset timestamp)
    {
        _states.Clear();
    }

    internal bool ContainsPointer(int pointerId) => _states.ContainsKey(pointerId);

    private void EmitObserved(PointerState state, DateTimeOffset timestamp, List<RadarPointer> output)
    {
        switch (_options.Mode)
        {
            case RadarInteractionMode.Touch:
                if (!state.IsPressed)
                {
                    state.IsPressed = true;
                    state.PressedAt = timestamp;
                    output.Add(ToPointer(state, RadarPointerPhase.Down, timestamp));
                }
                else
                {
                    output.Add(ToPointer(state, RadarPointerPhase.Move, timestamp));
                }
                break;

            case RadarInteractionMode.Dwell:
                var moved = Distance(state.X, state.Y, state.DwellAnchorX, state.DwellAnchorY);
                if (moved > _options.DwellRadiusNormalized)
                {
                    state.DwellAnchorX = state.X;
                    state.DwellAnchorY = state.Y;
                    state.DwellStarted = timestamp;
                    state.Triggered = false;
                }

                if (!state.Triggered && timestamp - state.DwellStarted >= _options.DwellDuration)
                {
                    output.Add(ToPointer(state, RadarPointerPhase.Down, timestamp));
                    output.Add(ToPointer(state, RadarPointerPhase.Up, timestamp));
                    state.Triggered = true;
                }
                else if (!state.Triggered)
                {
                    output.Add(ToPointer(state, RadarPointerPhase.Hover, timestamp));
                }
                break;

            case RadarInteractionMode.EnterTrigger:
                if (!state.Triggered)
                {
                    output.Add(ToPointer(state, RadarPointerPhase.Down, timestamp));
                    output.Add(ToPointer(state, RadarPointerPhase.Up, timestamp));
                    state.Triggered = true;
                }
                break;

            case RadarInteractionMode.HoverOnly:
                output.Add(ToPointer(state, RadarPointerPhase.Hover, timestamp));
                break;

            default:
                throw new InvalidOperationException($"Unknown interaction mode {_options.Mode}.");
        }
    }

    private static RadarPointer ToPointer(PointerState state, RadarPointerPhase phase, DateTimeOffset timestamp)
    {
        return new RadarPointer(
            state.PointerId,
            state.X,
            state.Y,
            phase,
            state.Confidence,
            timestamp.ToUnixTimeMilliseconds());
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        var x = x2 - x1;
        var y = y2 - y1;
        return MathF.Sqrt(x * x + y * y);
    }

    private sealed class PointerState
    {
        public int PointerId { get; init; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Confidence { get; set; }
        public DateTimeOffset FirstSeen { get; init; }
        public int MissingFrames { get; set; }
        public bool IsPressed { get; set; }
        public DateTimeOffset PressedAt { get; set; }
        public float DwellAnchorX { get; set; }
        public float DwellAnchorY { get; set; }
        public DateTimeOffset DwellStarted { get; set; }
        public bool Triggered { get; set; }
    }
}
