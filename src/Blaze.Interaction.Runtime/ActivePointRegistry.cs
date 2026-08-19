using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Runtime;

internal sealed class ActivePointRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<PointKey, InteractionPoint> _points = [];
    private readonly Dictionary<SurfaceKey, long> _sequences = [];
    private bool _disposed;

    public void Apply(InteractionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            ThrowIfDisposed();
            var surface = new SurfaceKey(frame.ProviderId, frame.ProviderInstanceId, frame.SurfaceId);
            _sequences[surface] = _sequences.TryGetValue(surface, out var sequence)
                ? Math.Max(sequence, frame.Sequence)
                : frame.Sequence;

            foreach (var point in frame.Points)
            {
                var key = new PointKey(frame.ProviderId, frame.ProviderInstanceId, frame.SurfaceId, point.Id);
                if (point.Phase is InteractionPhase.Up or InteractionPhase.Cancel)
                {
                    _points.Remove(key);
                }
                else
                {
                    _points[key] = point;
                }
            }

            if (!_points.Keys.Any(key => key.Surface == surface))
            {
                _sequences.Remove(surface);
            }
        }
    }

    public IReadOnlyList<InteractionFrame> DrainCancellationFrames(
        ProviderIdentity provider,
        long timestampUnixMs)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            ThrowIfDisposed();
            var groups = _points
                .Where(pair => pair.Key.ProviderId == provider.ProviderId
                               && pair.Key.ProviderInstanceId == provider.ProviderInstanceId)
                .GroupBy(pair => pair.Key.SurfaceId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();
            var frames = new List<InteractionFrame>(groups.Length);

            foreach (var group in groups)
            {
                var surface = new SurfaceKey(provider.ProviderId, provider.ProviderInstanceId, group.Key);
                var previousSequence = _sequences.TryGetValue(surface, out var sequence) ? sequence : 0;
                var cancellationSequence = previousSequence == long.MaxValue ? long.MaxValue : previousSequence + 1;
                var points = group
                    .OrderBy(pair => pair.Key.PointId)
                    .Select(pair => pair.Value with
                    {
                        Phase = InteractionPhase.Cancel,
                        TimestampUnixMs = timestampUnixMs
                    })
                    .ToArray();

                frames.Add(new InteractionFrame
                {
                    ProviderId = provider.ProviderId,
                    ProviderInstanceId = provider.ProviderInstanceId,
                    SurfaceId = group.Key,
                    Sequence = cancellationSequence,
                    TimestampUnixMs = timestampUnixMs,
                    Points = points
                });

                foreach (var pair in group)
                {
                    _points.Remove(pair.Key);
                }

                _sequences.Remove(surface);
            }

            return frames.AsReadOnly();
        }
    }

    public void Discard(ProviderIdentity provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var key in _points.Keys
                         .Where(key => key.ProviderId == provider.ProviderId
                                       && key.ProviderInstanceId == provider.ProviderInstanceId)
                         .ToArray())
            {
                _points.Remove(key);
            }

            foreach (var key in _sequences.Keys
                         .Where(key => key.ProviderId == provider.ProviderId
                                       && key.ProviderInstanceId == provider.ProviderInstanceId)
                         .ToArray())
            {
                _sequences.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _points.Clear();
            _sequences.Clear();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct SurfaceKey(
        string ProviderId,
        string ProviderInstanceId,
        string SurfaceId);

    private readonly record struct PointKey(
        string ProviderId,
        string ProviderInstanceId,
        string SurfaceId,
        long PointId)
    {
        internal SurfaceKey Surface => new(ProviderId, ProviderInstanceId, SurfaceId);
    }
}
