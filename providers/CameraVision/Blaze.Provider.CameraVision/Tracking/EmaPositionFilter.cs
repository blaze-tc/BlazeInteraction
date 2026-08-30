using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

internal sealed class EmaPositionFilter
{
    private readonly float _factor;
    private readonly Dictionary<long, Vector2Data> _positions = new();

    public EmaPositionFilter(float factor)
    {
        if (!float.IsFinite(factor) || factor is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(factor),
                "The EMA factor must be finite and between zero and one.");
        }

        _factor = factor;
    }

    public Vector2Data Update(long trackId, Vector2Data current)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId), "Track IDs must be positive.");
        }

        ArgumentNullException.ThrowIfNull(current);
        if (!_positions.TryGetValue(trackId, out var previous))
        {
            _positions.Add(trackId, current);
            return current;
        }

        var smoothed = _factor switch
        {
            0f => previous,
            1f => current,
            _ => new Vector2Data(
                previous.X + ((current.X - previous.X) * _factor),
                previous.Y + ((current.Y - previous.Y) * _factor))
        };
        _positions[trackId] = smoothed;
        return smoothed;
    }

    public bool Remove(long trackId) => _positions.Remove(trackId);

    public void Reset() => _positions.Clear();
}
