using Blaze.Interaction;
using Blaze.Interaction.Internal;

namespace Radar.Unity.Compatibility.Tests;

public sealed class LifecycleFrameBufferTests
{
    [Fact]
    public void MoveFloodIsCoalescedWhileDownUpCancelAndFollowingEmptyRemainOrdered()
    {
        var buffer = new LifecycleFrameBuffer(5);

        buffer.Publish(Frame(1, InteractionPhase.Down));
        for (var sequence = 2; sequence <= 101; sequence++) buffer.Publish(Frame(sequence, InteractionPhase.Move));
        buffer.Publish(Frame(102, InteractionPhase.Up));
        buffer.Publish(Frame(103, InteractionPhase.Cancel));
        buffer.Publish(Frame(104));

        Assert.Equal(5, buffer.PendingCount);
        Assert.Equal(99, buffer.DroppedCount);
        var sequences = new List<long>();
        while (buffer.TryConsume(out var frame)) sequences.Add(frame.Sequence);
        Assert.Equal([1L, 101L, 102L, 103L, 104L], sequences);
    }

    [Fact]
    public async Task FullLifecycleQueueBackpressuresProducerWithoutExceedingCapacityOrReorderingEdges()
    {
        var buffer = new LifecycleFrameBuffer(2);
        buffer.Publish(Frame(1, InteractionPhase.Down));
        buffer.Publish(Frame(2, InteractionPhase.Up));

        var thirdPublish = Task.Run(() => buffer.Publish(Frame(3, InteractionPhase.Cancel)));
        await Task.Delay(50);

        Assert.False(thirdPublish.IsCompleted);
        Assert.Equal(2, buffer.PendingCount);
        Assert.True(buffer.TryConsume(out var first));
        await thirdPublish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(buffer.TryConsume(out var second));
        Assert.True(buffer.TryConsume(out var third));
        Assert.Equal([1L, 2L, 3L], new[] { first, second, third }.Select(frame => frame.Sequence));
        Assert.False(buffer.TryConsume(out _));
    }

    private static InteractionFrame Frame(long sequence, InteractionPhase? phase = null) => new()
    {
        ProviderId = "blaze.radar.f10f20",
        ProviderInstanceId = "radar-main",
        SurfaceId = "main",
        Sequence = sequence,
        TimestampUnixMs = 1000 + sequence,
        Points = phase.HasValue
            ? [new InteractionPoint
            {
                Id = 7,
                SurfaceId = "main",
                ProviderId = "blaze.radar.f10f20",
                ProviderInstanceId = "radar-main",
                Phase = phase.Value
            }]
            : []
    };
}
