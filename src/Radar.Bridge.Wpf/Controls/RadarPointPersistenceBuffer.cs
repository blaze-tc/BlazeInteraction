using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

public sealed record RadarDisplayLayer(
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> Points,
    int SourcePointCount,
    double Opacity);

public sealed class RadarPointPersistenceBuffer
{
    private const double MinimumTrailOpacity = 0.12d;
    private readonly TimeSpan _lifetime;
    private readonly int _maximumFrames;
    private readonly List<Frame> _frames = [];
    private long? _lastSequence;

    public RadarPointPersistenceBuffer(TimeSpan lifetime, int maximumFrames)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        if (maximumFrames < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFrames));
        }

        _lifetime = lifetime;
        _maximumFrames = maximumFrames;
    }

    public void Add(long sequence, DateTimeOffset receivedAt, IReadOnlyList<RadarPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_lastSequence == sequence)
        {
            return;
        }

        if (_lastSequence.HasValue && sequence < _lastSequence.Value)
        {
            _frames.Clear();
        }

        _lastSequence = sequence;
        _frames.Add(new Frame(receivedAt, points));
        while (_frames.Count > _maximumFrames)
        {
            _frames.RemoveAt(0);
        }

        RemoveExpired(receivedAt);
    }

    public void Clear()
    {
        _frames.Clear();
        _lastSequence = null;
    }

    internal IReadOnlyList<RadarDisplayLayer> GetLayers(DateTimeOffset now, RadarDisplayBudget budget)
    {
        RemoveExpired(now);
        if (_frames.Count == 0 || budget.MaximumPointsPerLayer < 1 || budget.MaximumTrailLayers < 1)
        {
            return [];
        }

        var firstFrameIndex = Math.Max(0, _frames.Count - budget.MaximumTrailLayers);
        var layers = new RadarDisplayLayer[_frames.Count - firstFrameIndex];
        for (var index = firstFrameIndex; index < _frames.Count; index++)
        {
            var frame = _frames[index];
            var age = now - frame.ReceivedAt;
            var ageRatio = Math.Clamp(age.TotalMilliseconds / _lifetime.TotalMilliseconds, 0d, 1d);
            var opacity = index == _frames.Count - 1
                ? 1d
                : Math.Max(MinimumTrailOpacity, 1d - ageRatio);
            layers[index - firstFrameIndex] = new RadarDisplayLayer(
                frame.ReceivedAt,
                frame.GetDisplayPoints(budget.MaximumPointsPerLayer),
                frame.Points.Count,
                opacity);
        }

        return layers;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        while (_frames.Count > 0 && now - _frames[0].ReceivedAt > _lifetime)
        {
            _frames.RemoveAt(0);
        }
    }

    private sealed class Frame(DateTimeOffset receivedAt, IReadOnlyList<RadarPoint> points)
    {
        private readonly Dictionary<int, IReadOnlyList<RadarPoint>> _sampledPoints = [];

        public DateTimeOffset ReceivedAt { get; } = receivedAt;
        public IReadOnlyList<RadarPoint> Points { get; } = points;

        public IReadOnlyList<RadarPoint> GetDisplayPoints(int maximumPoints)
        {
            if (Points.Count <= maximumPoints)
            {
                return Points;
            }

            if (maximumPoints == 1)
            {
                return [Points[0]];
            }

            if (_sampledPoints.TryGetValue(maximumPoints, out var cached))
            {
                return cached;
            }

            var sampled = new RadarPoint[maximumPoints];
            var lastSourceIndex = Points.Count - 1L;
            var lastDisplayIndex = maximumPoints - 1L;
            for (var index = 0; index < maximumPoints; index++)
            {
                var sourceIndex = (int)(index * lastSourceIndex / lastDisplayIndex);
                sampled[index] = Points[sourceIndex];
            }

            _sampledPoints[maximumPoints] = sampled;
            return sampled;
        }
    }
}
