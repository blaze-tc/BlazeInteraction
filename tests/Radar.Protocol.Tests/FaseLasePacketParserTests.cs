using Yuexin.Radar.Protocol;

namespace Yuexin.Radar.Protocol.Tests;

public sealed class FaseLasePacketParserTests
{
    private static readonly byte[] ManualSample = [0x30, 0x14, 0x13, 0xAF];

    [Fact]
    public void ManualSample_HasValidCrc()
    {
        Assert.Equal(3, FaseLaseCrc.ReadPacketCrc(ManualSample[0]));
        Assert.Equal(3, FaseLaseCrc.Calculate(ManualSample[1], ManualSample[2], ManualSample[3]));
        Assert.True(FaseLaseCrc.IsValid(ManualSample[0], ManualSample[1], ManualSample[2], ManualSample[3]));
    }

    [Fact]
    public void ManualSample_DecodesDocumentedDistanceAndAngle()
    {
        var parsed = FaseLasePacketParser.TryParse(ManualSample, out var point);

        Assert.True(parsed);
        Assert.Equal(40, point.DistanceCentimeters);
        Assert.Equal(2479, point.AngleRaw);
        Assert.Equal(154.9375f, point.AngleDegrees);
        Assert.Equal(-0.3623385f, point.X, 5);
        Assert.Equal(0.1694427f, point.Y, 5);
    }

    [Theory]
    [InlineData(0xB0, 0x14, 0x13, 0xAF)]
    [InlineData(0x30, 0x94, 0x13, 0xAF)]
    [InlineData(0x30, 0x14, 0x93, 0xAF)]
    [InlineData(0x30, 0x14, 0x13, 0x2F)]
    public void InvalidHighBitPattern_IsRejected(byte a, byte b, byte c, byte d)
    {
        Assert.False(FaseLasePacketParser.TryParse([a, b, c, d], out _));
    }

    [Fact]
    public void InvalidCrc_IsRejected()
    {
        var corrupted = ManualSample.ToArray();
        corrupted[0] = 0x20;

        Assert.False(FaseLasePacketParser.TryParse(corrupted, out _));
    }
}
