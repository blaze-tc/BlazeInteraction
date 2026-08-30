using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class LatestValueBufferTests
{
    [Fact]
    public void Write_ReplacesOlderUnreadValue()
    {
        var buffer = new LatestValueBuffer<int>();

        buffer.Write(1);
        buffer.Write(2);
        buffer.Write(3);

        Assert.True(buffer.TryRead(out var value));
        Assert.Equal(3, value);
        Assert.False(buffer.TryRead(out _));
        Assert.Equal(2, buffer.DroppedValueCount);
    }
}
