namespace Blaze.Provider.CameraVision.Tests;

public sealed class LatestFrameSlotTests
{
    [Fact]
    public void Publish_ReplacesAndDisposesOldFrameWithoutBacklog()
    {
        using var slot = new LatestFrameSlot<DisposableValue>();
        var first = new DisposableValue(1);
        var second = new DisposableValue(2);
        var third = new DisposableValue(3);

        slot.Publish(first);
        slot.Publish(second);
        slot.Publish(third);

        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
        Assert.False(third.IsDisposed);
        Assert.True(slot.TryTake(out var actual));
        Assert.Same(third, actual);
        Assert.False(slot.TryTake(out _));
        Assert.Equal(3, slot.PublishedCount);
        Assert.Equal(2, slot.DroppedCount);
        actual!.Dispose();
    }

    [Fact]
    public void Dispose_ReleasesPendingFrame()
    {
        var slot = new LatestFrameSlot<DisposableValue>();
        var value = new DisposableValue(1);
        slot.Publish(value);

        slot.Dispose();

        Assert.True(value.IsDisposed);
    }

    private sealed class DisposableValue(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
