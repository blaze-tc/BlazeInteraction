using Blaze.Radar.Internal;

namespace Blaze.Radar.Compatibility.Tests;

public sealed class LifecycleBatchBufferTests
{
    [Fact]
    public void MoveFloodIsCoalescedWhileDownUpAndFollowingZeroRemainOrdered()
    {
        var buffer = new LifecycleBatchBuffer(4);

        buffer.Publish(Batch(1, RadarPointerPhase.Down));
        for (var sequence = 2; sequence <= 101; sequence++) buffer.Publish(Batch(sequence, RadarPointerPhase.Move));
        buffer.Publish(Batch(102, RadarPointerPhase.Up));
        buffer.Publish(Batch(103));

        Assert.Equal(4, buffer.PendingCount);
        Assert.Equal(99, buffer.DroppedCount);
        var sequences = new List<long>();
        while (buffer.TryConsume(out var batch)) sequences.Add(batch.screens.Single().sequence);
        Assert.Equal([1L, 101L, 102L, 103L], sequences);
    }

    [Fact]
    public async Task FullLifecycleQueueBackpressuresProducerWithoutExceedingCapacityOrReorderingEdges()
    {
        var buffer = new LifecycleBatchBuffer(2);
        buffer.Publish(Batch(1, RadarPointerPhase.Down));
        buffer.Publish(Batch(2, RadarPointerPhase.Up));

        var thirdPublish = Task.Run(() => buffer.Publish(Batch(3, RadarPointerPhase.Down)));
        await Task.Delay(50);

        Assert.False(thirdPublish.IsCompleted);
        Assert.Equal(2, buffer.PendingCount);
        Assert.True(buffer.TryConsume(out var first));
        await thirdPublish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(buffer.TryConsume(out var second));
        Assert.True(buffer.TryConsume(out var third));
        Assert.Equal([1L, 2L, 3L], new[] { first, second, third }.Select(batch => batch.screens.Single().sequence));
        Assert.False(buffer.TryConsume(out _));
    }

    private static RadarPointerBatchPayload Batch(long sequence, RadarPointerPhase? phase = null) => new()
    {
        screens =
        [
            new RadarScreenPointerFrame
            {
                screen = new RadarScreenInfo { screenId = "main", name = "Main", widthPixels = 1920, heightPixels = 1080, isPrimary = true },
                sequence = sequence,
                timestampUnixMilliseconds = 1000 + sequence,
                pointers = phase.HasValue
                    ? [new RadarScreenPointer { pointerId = 7, phase = phase.Value, normalizedX = .5f, normalizedY = .5f }]
                    : []
            }
        ]
    };
}
