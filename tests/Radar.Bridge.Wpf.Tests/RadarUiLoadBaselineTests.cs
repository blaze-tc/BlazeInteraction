using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarUiLoadBaselineTests
{
    [Fact]
    public void CreateSnapshot_OneHundredThousandPoints_IsFiniteAndDeterministic()
    {
        var snapshot = RadarUiLoadFixture.CreateSnapshot(100_000, sequence: 42);

        Assert.Equal(42, snapshot.Sequence);
        Assert.Equal(100_000, snapshot.RawPoints.Count);
        Assert.Equal(100_000, snapshot.ValidPoints.Count);
        Assert.All(snapshot.RawPoints, point =>
        {
            Assert.True(float.IsFinite(point.AngleDegrees));
            Assert.True(float.IsFinite(point.X));
            Assert.True(float.IsFinite(point.Y));
        });

        var first = snapshot.RawPoints[0];
        Assert.Equal(0, first.DistanceCentimeters);
        Assert.Equal(0, first.AngleRaw);
        Assert.Equal(0f, first.X);
        Assert.Equal(5f, first.Y);

        var last = snapshot.RawPoints[^1];
        Assert.Equal(ushort.MaxValue, last.DistanceCentimeters);
        Assert.Equal(279, last.AngleRaw);
        Assert.Equal(MathF.Sin(999.99f) * 5f, last.X);
        Assert.Equal(MathF.Cos(999.99f) * 5f, last.Y);
    }

    [Fact]
    public void ManualDispatcher_PreservesOrderAndReportsPendingCount()
    {
        var dispatcher = new ManualRadarUiDispatcher();
        var applied = new List<int>();

        dispatcher.Post(_ => applied.Add(1), null);
        dispatcher.Post(_ => applied.Add(2), null);

        Assert.Equal(2, dispatcher.PendingCount);
        Assert.Empty(applied);

        dispatcher.Drain();

        Assert.Equal([1, 2], applied);
        Assert.Equal(0, dispatcher.PendingCount);
    }
}

internal static class RadarUiLoadFixture
{
    public static RadarSensorRuntimeSnapshot CreateSnapshot(int pointCount, long sequence)
    {
        var points = Enumerable.Range(0, pointCount)
            .Select(index => new RadarPoint(
                Math.Min(index, ushort.MaxValue),
                index % 360,
                index % 360,
                MathF.Sin(index * 0.01f) * 5f,
                MathF.Cos(index * 0.01f) * 5f))
            .ToArray();
        return new RadarSensorRuntimeSnapshot(
            "main",
            "sensor-1",
            sequence,
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            points,
            points,
            Array.Empty<RadarCluster>(),
            Array.Empty<SensorDetection>(),
            30d,
            0d,
            0,
            0,
            0);
    }
}

internal sealed class ManualRadarUiDispatcher : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

    public int PendingCount
    {
        get
        {
            lock (_callbacks)
            {
                return _callbacks.Count;
            }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        lock (_callbacks)
        {
            _callbacks.Enqueue((d, state));
        }
    }

    public void Drain()
    {
        while (true)
        {
            (SendOrPostCallback Callback, object? State) callback;
            lock (_callbacks)
            {
                if (_callbacks.Count == 0)
                {
                    return;
                }

                callback = _callbacks.Dequeue();
            }

            callback.Callback(callback.State);
        }
    }
}
