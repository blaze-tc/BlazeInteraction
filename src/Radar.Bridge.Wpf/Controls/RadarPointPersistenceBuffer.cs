using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

public sealed record RadarPointPersistenceLayer(
    IReadOnlyList<RadarPoint> Points,
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

    public IReadOnlyList<RadarPointPersistenceLayer> GetLayers(DateTimeOffset now)
    {
        RemoveExpired(now);
        if (_frames.Count == 0)
        {
            return [];
        }

        var layers = new RadarPointPersistenceLayer[_frames.Count];
        for (var index = 0; index < _frames.Count; index++)
        {
            var age = now - _frames[index].ReceivedAt;
            var ageRatio = Math.Clamp(age.TotalMilliseconds / _lifetime.TotalMilliseconds, 0d, 1d);
            var opacity = index == _frames.Count - 1
                ? 1d
                : Math.Max(MinimumTrailOpacity, 1d - ageRatio);
            layers[index] = new RadarPointPersistenceLayer(_frames[index].Points, opacity);
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

    private sealed record Frame(DateTimeOffset ReceivedAt, IReadOnlyList<RadarPoint> Points);
}
