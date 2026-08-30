using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Protocol;

public sealed class RadarScanFrameBuilder
{
    private readonly int _minimumValidPointCount;
    private readonly int _minimumWrapDropRaw;
    private readonly List<RadarPoint> _currentPoints = [];
    private long _sequence;

    public RadarScanFrameBuilder(int minimumValidPointCount = 100, int minimumWrapDropRaw = 1000)
    {
        if (minimumValidPointCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumValidPointCount));
        }

        if (minimumWrapDropRaw < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWrapDropRaw));
        }

        _minimumValidPointCount = minimumValidPointCount;
        _minimumWrapDropRaw = minimumWrapDropRaw;
    }

    public IReadOnlyList<RadarPoint> CurrentPoints => _currentPoints.ToArray();
    public long RejectedAngleCount { get; private set; }

    public RadarScanFrame? AddPoint(RadarPoint point, DateTimeOffset? timestamp = null)
    {
        if (_currentPoints.Count == 0)
        {
            _currentPoints.Add(point);
            return null;
        }

        var previousAngle = _currentPoints[^1].AngleRaw;
        if (point.AngleRaw >= previousAngle)
        {
            _currentPoints.Add(point);
            return null;
        }

        if (previousAngle - point.AngleRaw < _minimumWrapDropRaw)
        {
            RejectedAngleCount++;
            return null;
        }

        RadarScanFrame? completed = null;
        if (_currentPoints.Count >= _minimumValidPointCount)
        {
            completed = new RadarScanFrame(
                ++_sequence,
                timestamp ?? DateTimeOffset.UtcNow,
                _currentPoints.ToArray());
        }

        _currentPoints.Clear();
        _currentPoints.Add(point);
        return completed;
    }

    public void Reset(bool resetSequence = false)
    {
        _currentPoints.Clear();
        RejectedAngleCount = 0;
        if (resetSequence)
        {
            _sequence = 0;
        }
    }
}
